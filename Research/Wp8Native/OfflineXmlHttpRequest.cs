namespace WPR.Wp8Native
{
    /// <summary>
    /// <c>IXMLHTTPRequest2</c> for a phone with no signal: every request is accepted and then
    /// fails, through the game's own callback, the way it would on a device that cannot reach
    /// the internet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WP8 native titles make HTTP requests through msxml6's <c>IXMLHTTPRequest2</c>, created
    /// with <c>CoCreateInstanceFromApp(CLSID_FreeThreadedXMLHTTP60, …)</c>. This used to answer
    /// REGDB_E_CLASSNOTREG, which no phone ever does - the class always exists there, and being
    /// offline shows up later, as <c>IXMLHTTPRequest2Callback::OnError</c>. Rovio's networking
    /// library (Angry Birds Space, Stella) turns the creation failure into an
    /// <c>HttpRequestException</c> its callers were never written to expect: Stella's
    /// "connecting" board (Stella, a phone, a dotted line, a globe) then waited for ever.
    /// Failing the request instead is the case the game was written for.
    /// </para>
    /// <para>
    /// The error is delivered from the deferred-call queue, i.e. on a later pass of the frame
    /// loop and never from inside <c>Send</c> - msxml6 calls back on its own thread, after
    /// <c>Send</c> has returned, and a game that is still inside <c>Send</c> when its callback
    /// runs sees half-built state. The callback is not AddRef'd: the game keeps it alive while
    /// it waits, which is the only time it is called.
    /// </para>
    /// <para>
    /// Rovio's cloud services (cloud/mist/smoke/account.rovio.com) are what these requests are
    /// for. The intent is to serve them from WPR Hub eventually - the place to start is
    /// <see cref="Send"/>: hand the request to the hub instead of failing it, and deliver
    /// <c>OnHeadersAvailable</c> / <c>OnResponseReceived</c> (callback slots 4 and 6) with an
    /// <c>ISequentialStream</c> over the body.
    /// </para>
    /// </remarks>
    public sealed class OfflineXmlHttpRequest
    {
        // msxml6.h. Angry Birds Stella asks for the free-threaded one.
        private static readonly Guid FreeThreadedXmlHttp60 = new("88d96a09-f192-11d4-a65f-0040963251e5");
        private static readonly Guid XmlHttp60 = new("88d96a0a-f192-11d4-a65f-0040963251e5");
        private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid IidXmlHttpRequest2 = new("e5d37dc0-552a-4d52-9cc0-a14d546fbd04");

        private const long HResultOk = 0;
        private const long NoInterface = unchecked((int)0x80004002);
        private const long Pending = unchecked((int)0x8000000A);
        private const long InvalidArgument = unchecked((int)0x80070057);
        private const long NotAllInterfaces = 0x00080012;

        /// <summary>
        /// HRESULT_FROM_WIN32(ERROR_INTERNET_NAME_NOT_RESOLVED): what a phone with no signal
        /// reports for any request, since resolving the host name is the first thing to fail.
        /// </summary>
        public const long NameNotResolved = unchecked((int)0x80072EE7);

        // IXMLHTTPRequest2: IUnknown 0-2, Open 3, Send 4, Abort 5, SetCookie 6,
        // SetCustomResponseStream 7, SetProperty 8, SetRequestHeader 9,
        // GetAllResponseHeaders 10, GetCookie 11, GetResponseHeader 12.
        private const int RequestSlots = 13;

        // IXMLHTTPRequest2Callback: IUnknown 0-2, OnRedirect 3, OnHeadersAvailable 4,
        // OnDataAvailable 5, OnResponseReceived 6, OnError 7.
        private const int CallbackOnError = 7;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Dictionary<long, Request> _requests = new();
        private readonly List<string> _log = new();
        private long _vtable;
        private long _deliverError;
        private int _deliveriesWatched;

        /// <summary>
        /// How long a request takes to fail. A real one spends this resolving the host name;
        /// answering on the very next frame instead let Angry Birds Stella's sign-in loop retry
        /// about 170 times a second, and the exception handling every failure costs exhausted
        /// the trap page within seconds.
        /// </summary>
        private static readonly long FailureLatency = System.Diagnostics.Stopwatch.Frequency;

        private readonly List<(long Due, long Callback, long Request)> _pending = new();

        /// <summary>
        /// Hands every failure that is due to the deferred queue. Called once per frame by the
        /// main loop, just before it drains that queue.
        /// </summary>
        public void QueueDue()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < _pending.Count; i++)
            {
                (long due, long callback, long request) = _pending[i];
                if (due > now)
                {
                    continue;
                }

                _emulator.QueueDeferredCall("IXMLHTTPRequest2Callback::OnError", DeliverErrorTrap(), callback, request, NameNotResolved);
                _pending.RemoveAt(i--);
            }
        }

        /// <summary>True while a request has failed but the game has not been told yet.</summary>
        public bool HasPending => _pending.Count > 0;

        /// <summary>
        /// Queues every pending failure now, due or not - for a main-thread wait, which cannot
        /// block for one, since the main loop is what delivers it.
        /// </summary>
        public void QueueAll()
        {
            foreach ((long _, long callback, long request) in _pending)
            {
                _emulator.QueueDeferredCall("IXMLHTTPRequest2Callback::OnError", DeliverErrorTrap(), callback, request, NameNotResolved);
            }

            _pending.Clear();
        }

        private sealed class Request
        {
            public long Callback;
            public string Method = "";
            public string Url = "";
        }

        public OfflineXmlHttpRequest(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        /// <summary>What the image asked for: one line per request, and what it was told.</summary>
        public IReadOnlyList<string> Log => _log;

        private long Arg(int index) => _frame.Arg(index);

        private void Return(long value) => _frame.Return(value);

        private void Write(long address, uint value)
        {
            if (address != 0)
            {
                _emulator.WriteUInt32(address, value);
            }
        }

        private void Note(string line)
        {
            // Bounded: a game that retries on a timer would otherwise grow this for ever.
            if (_log.Count < 1000)
            {
                _log.Add(line);

                // Trace reaches the session log (Logs/wpr-game.log) on a device, in Release too.
                System.Diagnostics.Trace.WriteLine("[wpr-http] " + line);
            }
        }

        /// <summary>
        /// HRESULT CoCreateInstanceFromApp(REFCLSID, IUnknown* outer, DWORD clsctx, void* reserved,
        /// DWORD count, MULTI_QI* results). Answers the msxml6 HTTP classes; anything else gets
        /// <paramref name="otherwise"/>.
        /// </summary>
        public void CoCreateInstanceFromApp(long otherwise)
        {
            Guid clsid = Arg(0) == 0 ? Guid.Empty : _emulator.ReadGuid(Arg(0));
            if (clsid != FreeThreadedXmlHttp60 && clsid != XmlHttp60)
            {
                Return(otherwise);
                return;
            }

            long count = Arg(4);
            long results = Arg(5);
            long instance = CreateRequest();

            // MULTI_QI is { const IID* pIID; IUnknown* pItf; HRESULT hr; }: 12 bytes on ARM.
            int answered = 0;
            for (long i = 0; i < count && results != 0; i++)
            {
                long entry = results + (i * 12);
                long iidAddress = _emulator.ReadUInt32(entry, 0);
                Guid iid = iidAddress == 0 ? Guid.Empty : _emulator.ReadGuid(iidAddress);
                bool ours = iid == IidUnknown || iid == IidXmlHttpRequest2;
                Write(entry + 4, ours ? (uint)instance : 0);
                Write(entry + 8, (uint)(ours ? HResultOk : NoInterface));
                answered += ours ? 1 : 0;
            }

            Return(answered == count ? HResultOk : answered > 0 ? NotAllInterfaces : NoInterface);
        }

        private long CreateRequest()
        {
            long instance = _emulator.AllocateHeap(8);
            _emulator.WriteUInt32(instance, (uint)Vtable());
            _emulator.WriteUInt32(instance + 4, 1);
            _requests[instance] = new Request();
            return instance;
        }

        private long Vtable()
        {
            if (_vtable != 0)
            {
                return _vtable;
            }

            var methods = new Dictionary<int, (string Name, Action Handler)>
            {
                [0] = ("QueryInterface", QueryInterface),
                [1] = ("AddRef", () => Return(AdjustReferences(Arg(0), +1))),
                [2] = ("Release", () => Return(AdjustReferences(Arg(0), -1))),
                [3] = ("Open", Open),
                [4] = ("Send", Send),
                [5] = ("Abort", () => Return(HResultOk)),
                // SetCookie(cookie, DWORD* state): no cookie jar, and nothing to report.
                [6] = ("SetCookie", () => { Write(Arg(2), 0); Return(HResultOk); }),
                [7] = ("SetCustomResponseStream", () => Return(HResultOk)),
                [8] = ("SetProperty", () => Return(HResultOk)),
                [9] = ("SetRequestHeader", () => Return(HResultOk)),
                // Headers never arrive, so asking for them is "not yet".
                [10] = ("GetAllResponseHeaders", () => { Write(Arg(1), 0); Return(Pending); }),
                // GetCookie(url, name, flags, DWORD* count, XHR_COOKIE** cookies).
                [11] = ("GetCookie", () => { Write(Arg(4), 0); Write(Arg(5), 0); Return(HResultOk); }),
                [12] = ("GetResponseHeader", () => { Write(Arg(2), 0); Return(Pending); }),
            };

            // Headroom past the last member, as the other COM tables here carry: a vtable one
            // short does not report a missing method, it jumps into the next heap object.
            const int padding = 8;
            _vtable = _emulator.AllocateHeap((RequestSlots + padding) * 4);
            for (int slot = 0; slot < RequestSlots + padding; slot++)
            {
                int captured = slot;
                (string name, Action handler) = methods.TryGetValue(slot, out var known)
                    ? known
                    : ($"slot{slot}", () =>
                    {
                        Note($"IXMLHTTPRequest2::slot{captured} called; answered S_OK");
                        Return(HResultOk);
                    });

                long trap = _emulator.RegisterVtableMethod($"IXMLHTTPRequest2::{name}", handler);
                _emulator.WriteUInt32(_vtable + (slot * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            return _vtable;
        }

        /// <summary>
        /// A trap that stands in for the game's <c>OnError</c> on the deferred queue: it notes the
        /// delivery and tail-calls the real method with the arguments untouched, so the log says
        /// the game was told, not only that it was going to be.
        /// </summary>
        private long DeliverErrorTrap()
        {
            if (_deliverError != 0)
            {
                return _deliverError;
            }

            long trap = _emulator.RegisterVtableMethod("IXMLHTTPRequest2Callback::OnError<-deliver", () =>
            {
                long callback = Arg(0);
                long request = Arg(1);
                long error = Arg(2);
                long onError = _emulator.ReadUInt32(_emulator.ReadUInt32(callback, 0) + (CallbackOnError * 4), 0);
                long back = _emulator.ReturnAddress;

                // What the game does with the error, for the first few: the imports it reached
                // and anything it threw. "Delivered" alone cannot tell a game that handled the
                // error from one that dropped it.
                bool watch = _deliveriesWatched++ < 8;
                var countsBefore = watch ? new Dictionary<string, int>(_emulator.CallCounts) : null;
                long importsBefore = _emulator.CallOrderTotal;
                int throwsBefore = _emulator.Stubs.ThrowHistory.Count;

                _emulator.CallEmulated("IXMLHTTPRequest2Callback::OnError", onError, [callback, request, error], () =>
                {
                    if (countsBefore is not null)
                    {
                        var reached = _emulator.CallCounts
                            .Select(c => (c.Key, Calls: c.Value - countsBefore.GetValueOrDefault(c.Key)))
                            .Where(c => c.Calls > 0)
                            .OrderByDescending(c => c.Calls)
                            .Take(12)
                            .Select(c => $"{c.Key} x{c.Calls}");
                        Note($"OnError(0x{unchecked((uint)error):X8}) to 0x{callback:X8} returned 0x{_emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_R0):X8} " +
                             $"after {_emulator.CallOrderTotal - importsBefore} import(s): {string.Join(", ", reached)}");
                        foreach (string thrown in _emulator.Stubs.ThrowHistory.Skip(throwsBefore).Take(4))
                        {
                            Note($"   threw: {thrown}");
                        }
                    }

                    _emulator.ContinueAt(back);
                }, recycle: true);
            });

            _deliverError = ArmEmulator.ThumbEntry(trap);
            return _deliverError;
        }

        private void QueryInterface()
        {
            Guid iid = Arg(1) == 0 ? Guid.Empty : _emulator.ReadGuid(Arg(1));
            if (iid == IidUnknown || iid == IidXmlHttpRequest2)
            {
                AdjustReferences(Arg(0), +1);
                Write(Arg(2), (uint)Arg(0));
                Return(HResultOk);
                return;
            }

            Write(Arg(2), 0);
            Return(NoInterface);
        }

        // The count is kept so AddRef/Release return believable numbers. Nothing is freed at
        // zero: the heap block is eight bytes and a stale pointer then still reads a vtable.
        private long AdjustReferences(long instance, int delta)
        {
            long count = Math.Max(0, _emulator.ReadUInt32(instance + 4, 0) + delta);
            _emulator.WriteUInt32(instance + 4, (uint)count);
            return count;
        }

        // Open(method, url, callback, user, password, proxyUser, proxyPassword).
        private void Open()
        {
            if (!_requests.TryGetValue(Arg(0), out Request? request))
            {
                Return(InvalidArgument);
                return;
            }

            if (Arg(1) == 0 || Arg(2) == 0 || Arg(3) == 0)
            {
                Return(InvalidArgument);
                return;
            }

            request.Method = _emulator.ReadUtf16String(Arg(1), 16);
            request.Url = _emulator.ReadUtf16String(Arg(2), 1024);
            request.Callback = Arg(3);
            Return(HResultOk);
        }

        // Send(ISequentialStream* body, ULONGLONG length).
        private void Send()
        {
            if (!_requests.TryGetValue(Arg(0), out Request? request) || request.Callback == 0)
            {
                Return(InvalidArgument);
                return;
            }

            long onError = _emulator.ReadUInt32(_emulator.ReadUInt32(request.Callback, 0) + (CallbackOnError * 4), 0);
            if (!_emulator.IsExecutableCode(onError))
            {
                Note($"{request.Method} {request.Url}: callback has no OnError; dropped");
                Return(HResultOk);
                return;
            }

            Note($"{request.Method} {request.Url} -> offline (0x{unchecked((uint)NameNotResolved):X8})");
            _pending.Add((System.Diagnostics.Stopwatch.GetTimestamp() + FailureLatency, request.Callback, Arg(0)));
            Return(HResultOk);
        }
    }
}
