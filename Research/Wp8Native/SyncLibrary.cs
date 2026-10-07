namespace WPR.Wp8Native
{
    /// <summary>
    /// The Win32 synchronisation primitives: events, critical sections and SRW locks.
    /// </summary>
    /// <remarks>
    /// There is one CPU here and one thread on it, so a lock can never be contended and
    /// taking one is a no-op that has to succeed. Events are different: they carry state a
    /// producer sets and a consumer reads, so they need to be real even when both are the
    /// same thread.
    /// <para>
    /// What this file is actually for is a specific failure. Every unimplemented import
    /// returns zero, which for <c>CreateEventExW</c> means NULL - a failure - and this image
    /// checks. It threw its own <c>lang::Exception</c> reading "lang::Signal:
    /// CreateEventExW: {0}", caught it three funclets deep, and carried on into a state
    /// where whatever the event was guarding never happened. The title screen then sat on
    /// LOADING for as long as it was given: ten thousand frames, with the game drawing
    /// happily and nothing wrong anywhere in the trace. A stub returning zero is not a
    /// neutral placeholder when zero is the failure value.
    /// </para>
    /// </remarks>
    public sealed class SyncLibrary
    {
        /// <summary>CREATE_EVENT_MANUAL_RESET.</summary>
        private const long FlagManualReset = 0x1;

        /// <summary>CREATE_EVENT_INITIAL_SET.</summary>
        private const long FlagInitialSet = 0x2;

        private const long WaitObject0 = 0x0;

        private const long WaitTimeout = 0x102;

        /// <summary>
        /// Handles start here rather than at 1, so a handle is recognisable in a register and
        /// cannot be confused with a small integer the image is carrying around.
        /// </summary>
        private const long HandleBase = 0x0E000000;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Dictionary<long, Event> _events = new();

        /// <summary>ConcRT events, keyed by the object's own address.</summary>
        private readonly Dictionary<long, bool> _concurrencyEvents = new();
        private long _nextHandle = HandleBase;

        public SyncLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        /// <summary>Waits that could not be satisfied, which are the interesting ones.</summary>
        public List<string> Log { get; } = new();

        /// <summary>How many events the image made, and how many are signalled now.</summary>
        public string Summary()
            => $"{_events.Count} event(s), {_events.Values.Count(e => e.Signalled)} signalled, " +
               $"{_satisfied} wait(s) satisfied, {_timedOut} timed out";

        private int _satisfied;
        private int _timedOut;

        private enum Kind
        {
            Event,
            Mutex,
            Semaphore,
        }

        /// <summary>A waitable handle: an event, a mutex (owner + recursion) or a semaphore (count).</summary>
        private sealed class Event
        {
            public Kind Kind { get; init; }

            public bool ManualReset { get; init; }

            public bool Signalled { get; set; }

            public string Name { get; init; } = string.Empty;

            public int Owner { get; set; }

            public int Recursion { get; set; }

            public long Count { get; set; }

            public long Maximum { get; init; }
        }

        /// <summary>Whether a wait on <paramref name="e"/> by <paramref name="thread"/> would succeed now.</summary>
        private static bool CanTake(Event e, int thread) => e.Kind switch
        {
            Kind.Mutex => e.Owner == 0 || e.Owner == thread,
            Kind.Semaphore => e.Count > 0,
            _ => e.Signalled,
        };

        /// <summary>What a satisfied wait does to the object: resets, takes ownership, or decrements.</summary>
        private static void Take(Event e, int thread)
        {
            switch (e.Kind)
            {
                case Kind.Mutex:
                    e.Owner = thread;
                    e.Recursion++;
                    break;
                case Kind.Semaphore:
                    e.Count--;
                    break;
                default:
                    if (!e.ManualReset)
                    {
                        e.Signalled = false;
                    }

                    break;
            }
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // HANDLE CreateEventExW(SECURITY_ATTRIBUTES*, LPCWSTR name, DWORD flags, DWORD access)
            handlers["CreateEventExW"] = () =>
            {
                long flags = _frame.Arg(2);
                long handle = _nextHandle++;

                _events[handle] = new Event
                {
                    ManualReset = (flags & FlagManualReset) != 0,
                    Signalled = (flags & FlagInitialSet) != 0,
                    Name = _frame.Arg(1) == 0 ? string.Empty : _emulator.ReadUtf16String(_frame.Arg(1)),
                };

                _frame.Return(handle);
            };

            // The pre-Windows-8 spelling, for anything that still uses it. bManualReset and
            // bInitialState are separate BOOLs here rather than one flags word.
            handlers["CreateEventW"] = () =>
            {
                long handle = _nextHandle++;
                _events[handle] = new Event
                {
                    ManualReset = _frame.Arg(1) != 0,
                    Signalled = _frame.Arg(2) != 0,
                    Name = _frame.Arg(3) == 0 ? string.Empty : _emulator.ReadUtf16String(_frame.Arg(3)),
                };

                _frame.Return(handle);
            };

            // HANDLE CreateMutexExW(SECURITY_ATTRIBUTES*, LPCWSTR name, DWORD flags, DWORD access):
            // CREATE_MUTEX_INITIAL_OWNER (1) makes the caller the owner. Unimplemented, every mutex
            // was handle 0 and Modern Combat 4's locks guarded nothing.
            handlers["CreateMutexExW"] = () =>
            {
                long handle = _nextHandle++;
                bool owned = (_frame.Arg(2) & 1) != 0;
                _events[handle] = new Event
                {
                    Kind = Kind.Mutex,
                    Name = _frame.Arg(1) == 0 ? string.Empty : _emulator.ReadUtf16String(_frame.Arg(1)),
                    Owner = owned ? _emulator.CurrentThreadId : 0,
                    Recursion = owned ? 1 : 0,
                };
                _frame.Return(handle);
            };
            handlers["CreateMutexW"] = () =>
            {
                long handle = _nextHandle++;
                bool owned = _frame.Arg(1) != 0;
                _events[handle] = new Event
                {
                    Kind = Kind.Mutex,
                    Owner = owned ? _emulator.CurrentThreadId : 0,
                    Recursion = owned ? 1 : 0,
                };
                _frame.Return(handle);
            };
            handlers["ReleaseMutex"] = () =>
            {
                if (_events.TryGetValue(_frame.Arg(0), out Event? e) && e.Kind == Kind.Mutex && e.Recursion > 0)
                {
                    if (--e.Recursion == 0)
                    {
                        e.Owner = 0;
                    }

                    _frame.Return(1);
                    return;
                }

                _frame.Return(0);
            };

            // HANDLE CreateSemaphoreExW(SECURITY_ATTRIBUTES*, LONG initial, LONG maximum, LPCWSTR name,
            // DWORD flags, DWORD access).
            handlers["CreateSemaphoreExW"] = () =>
            {
                long handle = _nextHandle++;
                _events[handle] = new Event
                {
                    Kind = Kind.Semaphore,
                    Count = _frame.SignedArg(1),
                    Maximum = _frame.SignedArg(2),
                };
                _frame.Return(handle);
            };
            handlers["CreateSemaphoreW"] = handlers["CreateSemaphoreExW"];

            // BOOL ReleaseSemaphore(HANDLE, LONG count, LONG* previous).
            handlers["ReleaseSemaphore"] = () =>
            {
                if (!_events.TryGetValue(_frame.Arg(0), out Event? e) || e.Kind != Kind.Semaphore)
                {
                    _frame.Return(0);
                    return;
                }

                if (_frame.Arg(2) != 0)
                {
                    _emulator.WriteUInt32(_frame.Arg(2), (uint)e.Count);
                }

                e.Count = Math.Min(e.Maximum > 0 ? e.Maximum : long.MaxValue, e.Count + _frame.SignedArg(1));
                _frame.Return(1);
            };

            handlers["SetEvent"] = () =>
            {
                if (_events.TryGetValue(_frame.Arg(0), out Event? e))
                {
                    e.Signalled = true;
                }

                _frame.Return(1);
            };

            handlers["ResetEvent"] = () =>
            {
                if (_events.TryGetValue(_frame.Arg(0), out Event? e))
                {
                    e.Signalled = false;
                }

                _frame.Return(1);
            };

            // DWORD WaitForSingleObjectEx(HANDLE, DWORD milliseconds, BOOL alertable)
            handlers["WaitForSingleObjectEx"] = () => Wait(_frame.Arg(0), _frame.Arg(1));
            handlers["WaitForSingleObject"] = () => Wait(_frame.Arg(0), _frame.Arg(1));

            // One thread, so a lock is never contended. These must still return success:
            // InitializeCriticalSectionEx answers a BOOL and the image checks it.
            handlers["InitializeCriticalSectionEx"] = () => _frame.Return(1);
            handlers["InitializeCriticalSectionAndSpinCount"] = () => _frame.Return(1);
            handlers["InitializeCriticalSection"] = () => _frame.Return(0);
            handlers["EnterCriticalSection"] = () => _frame.Return(0);
            handlers["TryEnterCriticalSection"] = () => _frame.Return(1);
            handlers["LeaveCriticalSection"] = () => _frame.Return(0);
            handlers["DeleteCriticalSection"] = () => _frame.Return(0);

            // Concurrency::event - the ConcRT one, which is not a Win32 event and has its
            // own vocabulary: wait answers 0 when signalled and SIZE_MAX on timeout.
            handlers["??0event@Concurrency@@QAA@XZ"] = () =>
            {
                _concurrencyEvents[_frame.Arg(0)] = false;
                _frame.Return(_frame.Arg(0));
            };

            handlers["??1event@Concurrency@@QAA@XZ"] = () =>
            {
                _concurrencyEvents.Remove(_frame.Arg(0));
                _frame.Return(0);
            };

            handlers["?set@event@Concurrency@@QAAXXZ"] = () =>
            {
                _concurrencyEvents[_frame.Arg(0)] = true;
                _frame.Return(0);
            };

            handlers["?reset@event@Concurrency@@QAAXXZ"] = () =>
            {
                _concurrencyEvents[_frame.Arg(0)] = false;
                _frame.Return(0);
            };

            handlers["?wait@event@Concurrency@@QAAII@Z"] = WaitConcurrencyEvent;

            handlers["InitializeSRWLock"] = () => _frame.Return(0);
            handlers["AcquireSRWLockExclusive"] = () => _frame.Return(0);
            handlers["ReleaseSRWLockExclusive"] = () => _frame.Return(0);
            handlers["AcquireSRWLockShared"] = () => _frame.Return(0);
            handlers["ReleaseSRWLockShared"] = () => _frame.Return(0);
            handlers["TryAcquireSRWLockExclusive"] = () => _frame.Return(1);
            handlers["TryAcquireSRWLockShared"] = () => _frame.Return(1);
        }

        /// <summary>
        /// Waits on a <c>Concurrency::event</c>, running queued work first if it is not set.
        /// </summary>
        /// <remarks>
        /// This used to answer 0 - signalled - immediately and unconditionally, and that is a
        /// worse lie than it sounds. The image's loading handshake is exactly the shape this
        /// breaks: queue a work item, wait on the event the work item will set, then read what
        /// it produced. Answering at once lets the main thread past the wait *before the work
        /// item has run at all*, so it reads a result nobody has written yet and concludes
        /// there is nothing to load.
        /// <para>
        /// The measurement that found it: both queued callbacks returned S_OK having made ten
        /// and eleven import calls, three of which were <c>event::set</c>. Ten calls is not a
        /// loader running, it is task plumbing signalling completion - and the main thread had
        /// already gone past.
        /// </para>
        /// <para>
        /// A wait is the best yield point an image can offer, so an unset event now drains the
        /// whole queue before answering. Draining rather than yielding once is deliberate: a
        /// blocked thread is blocked until someone sets the event, and the only candidate for
        /// "someone" here is the work already queued.
        /// </para>
        /// </remarks>
        private void WaitConcurrencyEvent()
        {
            bool signalled = _concurrencyEvents.GetValueOrDefault(_frame.Arg(0));
            if (signalled || _emulator.PendingDeferredCalls == 0)
            {
                _concurrencyWaits.Add(
                    $"event 0x{_frame.Arg(0):X8} wait -> {(signalled ? "already set" : "nothing queued")}");
                _frame.Return(0);
                return;
            }

            long resume = _emulator.ReturnAddress;
            int queued = _emulator.PendingDeferredCalls;
            _concurrencyWaits.Add($"event 0x{_frame.Arg(0):X8} wait -> draining {queued} queued callback(s)");

            _emulator.DrainDeferredCalls(() =>
            {
                _frame.Return(0);
                _emulator.ContinueAt(resume);
            });
        }

        /// <summary>What happened at each ConcRT wait, capped.</summary>
        private readonly List<string> _concurrencyWaits = new();

        /// <summary>ConcRT waits, for the report.</summary>
        public IReadOnlyList<string> ConcurrencyWaits => _concurrencyWaits;

        /// <summary>
        /// Waits on an event, yielding to queued work if it is not already signalled.
        /// </summary>
        /// <remarks>
        /// A wait is the clearest yield point an image ever offers: it has said outright
        /// that it cannot proceed and is willing to be interrupted. So an unsignalled wait
        /// runs a queued callback - which is very often the thing that will signal it - and
        /// reports a timeout if that did not help.
        /// <para>
        /// Timing out rather than claiming success is deliberate. Answering WAIT_OBJECT_0 on
        /// an event nobody set tells the image that work it is waiting on has finished, and
        /// it then reads whatever that work was supposed to produce. A timeout is a normal
        /// answer that callers are written to handle, and it keeps the lie small.
        /// </para>
        /// </remarks>
        private void Wait(long handle, long milliseconds)
        {
            if (!_events.TryGetValue(handle, out Event? e))
            {
                // Not one of ours - a file handle, or something never created. Saying
                // "signalled" is the forgiving answer and matches how the rest of this probe
                // treats a handle it does not know.
                _frame.Return(WaitObject0);
                return;
            }

            int me = _emulator.CurrentThreadId;
            if (CanTake(e, me))
            {
                Take(e, me);
                _satisfied++;
                _frame.Return(WaitObject0);
                return;
            }

            // With guest threads, an infinite wait really waits: the thread blocks and whoever
            // will set the event runs. Only if nothing else can run does it fall through.
            if (milliseconds == 0xFFFFFFFF)
            {
                _frame.Return(WaitObject0);
                if (_emulator.BlockCurrentThread(
                        () => CanTake(e, me),
                        () =>
                        {
                            _satisfied++;
                            Take(e, me);
                        },
                        why: $"{e.Kind.ToString().ToLowerInvariant()} {(e.Name.Length > 0 ? e.Name : $"0x{handle:X8}")}"))
                {
                    return;
                }
            }

            // Set the answer first: YieldToDeferredWork takes over the return path only if it
            // found something to run, and this value has to be already in place if it did not.
            _timedOut++;
            if (Log.Count < 40)
            {
                Log.Add($"wait on {(e.Name.Length > 0 ? e.Name : $"handle 0x{handle:X8}")} " +
                        $"({milliseconds} ms) - not signalled");
            }

            _frame.Return(WaitTimeout);
            _emulator.YieldToDeferredWork();
        }
    }
}
