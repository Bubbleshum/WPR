namespace WPR.Wp8Native
{
    /// <summary>
    /// A stream this host implements (a WinRT random-access stream, or an IStream it made), opened
    /// for reading and writing from host code. The data is shared with the stream it came from, and
    /// so is the position: a byte stream over a stream moves that stream's seek pointer, as
    /// Media Foundation's wrappers do.
    /// </summary>
    internal sealed class HostStreamBinding
    {
        public required Stream Data { get; init; }

        public required Func<long> GetPosition { get; init; }

        public required Action<long> SetPosition { get; init; }

        public bool CanRead { get; init; }

        public bool CanWrite { get; init; }

        /// <summary>IContentTypeProvider's answer, when the stream has one.</summary>
        public string? ContentType { get; init; }

        /// <summary>True once the stream it came from was closed (IClosable::Close).</summary>
        public Func<bool> IsClosed { get; init; } = () => false;

        /// <summary>Drops the reference this binding holds on the stream. Runs once.</summary>
        public Action? Release { get; init; }

        /// <summary>The interface pointer the binding was made from.</summary>
        public long Source { get; init; }
    }

    /// <summary>
    /// The host's streams, as Media Foundation needs them: <see cref="HostStubs"/> answers for the
    /// WinRT streams (<c>WinRtStreams.cs</c>) and its own IStreams (<c>WinRtAbiLibrary.cs</c>).
    /// </summary>
    internal interface IHostStreams
    {
        /// <summary>A binding to the stream behind <paramref name="pointer"/>, holding a reference on it; null when it is not the host's.</summary>
        HostStreamBinding? Bind(long pointer);

        /// <summary>
        /// A new reference, as interface <paramref name="iid"/>, to a stream over the same data as
        /// <paramref name="binding"/> (IRandomAccessStream and its relatives, or IStream); 0 when
        /// none can be made for that interface.
        /// </summary>
        long Expose(HostStreamBinding binding, Guid iid);
    }

    /// <summary>
    /// IMFByteStream: <c>MFCreateMFByteStreamOnStream(Ex)</c> over a stream, and
    /// <c>MFCreateStreamOnMFByteStream(Ex)</c> back the other way.
    /// </summary>
    /// <remarks>
    /// Over a host stream every call completes at once. Over a game's own IStream each call is
    /// made on that IStream (Seek then Read/Write, Stat for the length), so it costs guest calls
    /// but works for any implementation. A game's own <c>IRandomAccessStream</c> cannot be read
    /// here - its reads are WinRT async operations the host would have to wait on - so that case
    /// fails with E_NOTIMPL and is recorded, as is every other bridge this file cannot build.
    /// The asynchronous BeginRead/BeginWrite do the transfer at once and queue the callback through
    /// the work-queue machinery (<see cref="InvokeLater"/>), never on the caller's stack.
    /// </remarks>
    internal sealed partial class MediaFoundation
    {
        public static readonly Guid IidByteStream = new("ad4c1b00-4bf7-422f-9175-756693d9130d");
        public static readonly Guid IidStream = new("0000000c-0000-0000-c000-000000000046");
        public static readonly Guid IidSequentialStream = new("0c733a30-2a1c-11ce-ade5-00aa0044773d");
        public static readonly Guid IidRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
        public static readonly Guid ByteStreamContentType = new("fc358289-3cb6-460c-a424-b6681260375a");

        public const uint CapReadable = 0x1, CapWritable = 0x2, CapSeekable = 0x4, CapDoesNotUseNetwork = 0x800;
        private const long RoClosed = unchecked((int)0x80000013);

        private IHostStreams? _hostStreams;

        /// <summary>The host's streams; without them only a game's own IStream can be wrapped.</summary>
        public IHostStreams? HostStreams { get => _hostStreams; set => _hostStreams = value; }


        // ------------------------------------------------------------------------------
        // Backends. Every operation is a guest-call script (MediaCom.Run): a host stream's yields
        // nothing and completes at once; a game's IStream yields its calls one by one.
        // ------------------------------------------------------------------------------

        /// <summary>Where a byte stream's bytes come from. Each operation leaves its HRESULT in op.Hr and its result in op.Value.</summary>
        internal abstract class ByteBackend
        {
            public abstract bool CanRead { get; }

            public abstract bool CanWrite { get; }

            public virtual string? ContentType => null;

            public abstract long Position { get; set; }

            public abstract IEnumerable<GuestCall> Length(GuestOp op);

            public abstract IEnumerable<GuestCall> SetLength(long length, GuestOp op);

            /// <summary>Reads at <see cref="Position"/> into guest memory and advances; op.Value is the bytes read.</summary>
            public abstract IEnumerable<GuestCall> Read(long destination, int count, GuestOp op);

            public abstract IEnumerable<GuestCall> Write(long source, int count, GuestOp op);

            public abstract IEnumerable<GuestCall> Flush(GuestOp op);

            /// <summary>COM references the backend holds, released with the byte stream.</summary>
            public virtual IEnumerable<long> Held => [];

            public virtual void Release()
            {
            }

            public virtual HostStreamBinding? Host => null;
        }

        /// <summary>Over a host stream: synchronous, no guest calls.</summary>
        private sealed class HostBackend(HostStreamBinding binding, ArmEmulator e) : ByteBackend
        {
            private bool _released;

            public override bool CanRead => binding.CanRead;

            public override bool CanWrite => binding.CanWrite;

            public override string? ContentType => binding.ContentType;

            public override long Position { get => binding.GetPosition(); set => binding.SetPosition(value); }

            public override HostStreamBinding Host => binding;

            /// <summary>Runs <paramref name="act"/> unless the stream was closed; I/O failures become HRESULTs.</summary>
            private static IEnumerable<GuestCall> Do(HostStreamBinding binding, GuestOp op, Func<long> act)
            {
                op.Value = 0;
                try
                {
                    op.Hr = binding.IsClosed() ? RoClosed : act();
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
                {
                    op.Hr = ex is ObjectDisposedException ? RoClosed : MediaCom.EFail;
                }

                return [];
            }

            public override IEnumerable<GuestCall> Length(GuestOp op) => Do(binding, op, () =>
            {
                op.Value = binding.Data.Length;
                return MediaCom.SOk;
            });

            public override IEnumerable<GuestCall> SetLength(long length, GuestOp op) => Do(binding, op, () =>
            {
                if (!binding.CanWrite)
                {
                    return MediaCom.EAccessDenied;
                }

                binding.Data.SetLength(length);
                return MediaCom.SOk;
            });

            public override IEnumerable<GuestCall> Read(long destination, int count, GuestOp op) => Do(binding, op, () =>
            {
                if (!binding.CanRead)
                {
                    return MediaCom.EAccessDenied;
                }

                long at = Position;
                int read = 0;
                if (at < binding.Data.Length && count > 0)
                {
                    byte[] bytes = new byte[(int)Math.Min(count, binding.Data.Length - at)];
                    binding.Data.Position = at;
                    while (read < bytes.Length)
                    {
                        int n = binding.Data.Read(bytes, read, bytes.Length - read);
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    if (read > 0)
                    {
                        e.WriteMemory(destination, bytes.AsSpan(0, read).ToArray());
                    }
                }

                Position = at + read;
                op.Value = read;
                return MediaCom.SOk;
            });

            public override IEnumerable<GuestCall> Write(long source, int count, GuestOp op) => Do(binding, op, () =>
            {
                if (!binding.CanWrite)
                {
                    return MediaCom.EAccessDenied;
                }

                long at = Position;
                binding.Data.Position = at;
                if (count > 0)
                {
                    binding.Data.Write(e.ReadMemory(source, count));
                }

                Position = at + count;
                op.Value = count;
                return MediaCom.SOk;
            });

            public override IEnumerable<GuestCall> Flush(GuestOp op) => Do(binding, op, () =>
            {
                binding.Data.Flush();
                return MediaCom.SOk;
            });

            public override void Release()
            {
                if (!_released)
                {
                    _released = true;
                    binding.Release?.Invoke();
                }
            }
        }

        /// <summary>
        /// Over a game's own IStream: Read 3, Write 4, Seek 5, SetSize 6, Commit 8, Stat 12. The
        /// byte stream keeps its own position and seeks the IStream to it before each transfer.
        /// </summary>
        private sealed class GuestStreamBackend(MediaCom com, long stream) : ByteBackend
        {
            public override bool CanRead => true;

            public override bool CanWrite => true;

            public override long Position { get; set; }

            public override IEnumerable<long> Held => [stream];

            public long Stream => stream;

            private GuestCall? Method(string name, int slot, params long[] args) =>
                com.GuestMethod(stream, slot) is { } fn ? new GuestCall($"IStream::{name}", fn, [stream, .. args]) : null;

            private static bool Failed(long hr) => (hr & 0x80000000) != 0;

            public override IEnumerable<GuestCall> Length(GuestOp op)
            {
                // STATSTG on ARM is 72 bytes, cbSize at 8. STATFLAG_NONAME (1): no name to free.
                op.Value = 0;
                long stat = com.Alloc(72);
                try
                {
                    if (Method("Stat", 12, stat, 1) is not { } call)
                    {
                        op.Hr = MediaCom.EPointer;
                        yield break;
                    }

                    yield return call;
                    op.Hr = Failed(op.Last) ? op.Last : MediaCom.SOk;
                    op.Value = op.Hr == MediaCom.SOk ? (long)com.U64(stat + 8) : 0;
                }
                finally
                {
                    com.Free(stat);
                }
            }

            public override IEnumerable<GuestCall> SetLength(long length, GuestOp op)
            {
                if (Method("SetSize", 6, 0, length & 0xFFFFFFFF, length >> 32) is not { } call)
                {
                    op.Hr = MediaCom.EPointer;
                    yield break;
                }

                yield return call;
                op.Hr = Failed(op.Last) ? op.Last : MediaCom.SOk;
            }

            private IEnumerable<GuestCall> Transfer(string name, int slot, long buffer, int count, GuestOp op)
            {
                op.Value = 0;
                long scratch = com.Alloc(16);
                try
                {
                    // Seek(LARGE_INTEGER move in r2:r3, STREAM_SEEK_SET on the stack, ULARGE_INTEGER* out).
                    GuestCall? seek = Method("Seek", 5, 0, Position & 0xFFFFFFFF, Position >> 32, 0, scratch);
                    GuestCall? transfer = Method(name, slot, buffer, count, scratch + 8);
                    if (seek is null || transfer is null)
                    {
                        op.Hr = MediaCom.EPointer;
                        yield break;
                    }

                    yield return seek.Value;
                    if (Failed(op.Last))
                    {
                        op.Hr = op.Last;
                        yield break;
                    }

                    yield return transfer.Value;
                    if (Failed(op.Last))
                    {
                        op.Hr = op.Last;
                        yield break;
                    }

                    // S_FALSE from Read (the end was reached) is success with a short count.
                    op.Hr = MediaCom.SOk;
                    op.Value = Math.Min(com.U32(scratch + 8), count);
                    Position += op.Value;
                }
                finally
                {
                    com.Free(scratch);
                }
            }

            public override IEnumerable<GuestCall> Read(long destination, int count, GuestOp op) => Transfer("Read", 3, destination, count, op);

            public override IEnumerable<GuestCall> Write(long source, int count, GuestOp op) => Transfer("Write", 4, source, count, op);

            public override IEnumerable<GuestCall> Flush(GuestOp op)
            {
                op.Hr = MediaCom.SOk;
                if (Method("Commit", 8, 0) is { } call)
                {
                    yield return call;
                    op.Hr = Failed(op.Last) ? op.Last : MediaCom.SOk;
                }
            }
        }

        // ------------------------------------------------------------------------------
        // The byte stream
        // ------------------------------------------------------------------------------

        internal sealed class MfByteStream : MfAttributedObject
        {
            public override string Kind => "IMFByteStream";

            public required ByteBackend Backend { get; init; }

            public bool Closed { get; set; }

            public override IEnumerable<long> Destroy()
            {
                Backend.Release();
                return [.. base.Destroy(), .. Backend.Held];
            }
        }

        private MediaInterface? _byteStreamShape;

        // IMFByteStream: GetCapabilities 3, GetLength 4, SetLength 5, GetCurrentPosition 6,
        // SetCurrentPosition 7, IsEndOfStream 8, Read 9, BeginRead 10, EndRead 11, Write 12,
        // BeginWrite 13, EndWrite 14, Seek 15, Flush 16, Close 17.
        private MediaInterface ByteStreamShape => _byteStreamShape ??= new("IMFByteStream", [IidByteStream], 18, () => new()
        {
            [3] = ("GetCapabilities", () => WithStream(s =>
            {
                uint caps = CapSeekable | (s.Backend.CanRead ? CapReadable : 0) | (s.Backend.CanWrite ? CapWritable : 0) |
                            (s.Backend.Host is not null ? CapDoesNotUseNetwork : 0);
                Out32(Arg(1), caps);
            }, allowClosed: true)),
            [4] = ("GetLength", () => WithStream(s =>
            {
                long outLength = Arg(1);
                GuestOp op = new();
                _com.Run(op, s.Backend.Length(op), () =>
                {
                    _com.W64(outLength, (ulong)op.Value);
                    Return(outLength == 0 ? MediaCom.EPointer : op.Hr);
                });
            })),

            // SetLength(QWORD): the pair is r2:r3.
            [5] = ("SetLength", () => WithStream(s =>
            {
                GuestOp op = new();
                _com.Run(op, s.Backend.SetLength(Pair(2), op), () => Return(op.Hr));
            })),
            [6] = ("GetCurrentPosition", () => WithStream(s =>
            {
                _com.W64(Arg(1), (ulong)s.Backend.Position);
                Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            })),
            [7] = ("SetCurrentPosition", () => WithStream(s =>
            {
                long position = Pair(2);
                if (position < 0)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                s.Backend.Position = position;
                Return(MediaCom.SOk);
            })),
            [8] = ("IsEndOfStream", () => WithStream(s =>
            {
                long outEnd = Arg(1);
                GuestOp op = new();
                _com.Run(op, s.Backend.Length(op), () =>
                {
                    _com.W32(outEnd, op.Hr == MediaCom.SOk && s.Backend.Position >= op.Value ? 1 : 0);
                    Return(outEnd == 0 ? MediaCom.EPointer : op.Hr);
                });
            })),

            // Read(BYTE* pb, ULONG cb, ULONG* pcbRead).
            [9] = ("Read", () => WithStream(s => Transfer(s, write: false))),

            // BeginRead(BYTE* pb, ULONG cb, IMFAsyncCallback*, IUnknown* punkState).
            [10] = ("BeginRead", () => WithStream(s => BeginTransfer(s, write: false))),

            // EndRead(IMFAsyncResult*, ULONG* pcbRead).
            [11] = ("EndRead", EndTransfer),
            [12] = ("Write", () => WithStream(s => Transfer(s, write: true))),
            [13] = ("BeginWrite", () => WithStream(s => BeginTransfer(s, write: true))),
            [14] = ("EndWrite", EndTransfer),

            // Seek(MFBYTESTREAM_SEEK_ORIGIN, LONGLONG offset, DWORD flags, QWORD* pqwCurrentPosition):
            // the origin in r1, the offset in r2:r3, the flags and the out-pointer on the stack.
            [15] = ("Seek", () => WithStream(s =>
            {
                long target = Arg(1) switch
                {
                    0 => Pair(2),                         // msoBegin
                    1 => s.Backend.Position + Pair(2),    // msoCurrent
                    _ => -1,
                };
                if (target < 0)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                s.Backend.Position = target;
                _com.W64(Arg(5), (ulong)target);
                Return(MediaCom.SOk);
            })),
            [16] = ("Flush", () => WithStream(s =>
            {
                GuestOp op = new();
                _com.Run(op, s.Backend.Flush(op), () => Return(op.Hr));
            })),

            // Close: the stream is unusable afterwards; a second Close is harmless.
            [17] = ("Close", () => WithStream(s =>
            {
                s.Closed = true;
                Return(MediaCom.SOk);
            }, allowClosed: true)),
        });

        private void Out32(long pointer, long value)
        {
            _com.W32(pointer, value);
            Return(pointer == 0 ? MediaCom.EPointer : MediaCom.SOk);
        }

        private void WithStream(Action<MfByteStream> act, bool allowClosed = false)
        {
            if (_com.Self<MfByteStream>() is not { } s)
            {
                Return(MediaCom.EPointer);
            }
            else if (s.Closed && !allowClosed)
            {
                Return(MfEShutdown);
            }
            else
            {
                act(s);
            }
        }

        /// <summary>Read/Write(BYTE* pb, ULONG cb, ULONG* pcb).</summary>
        private void Transfer(MfByteStream s, bool write)
        {
            long buffer = Arg(1), outCount = Arg(3);
            int count = (int)Math.Min(Arg(2), CallFrame.SaneLengthLimit);
            if (buffer == 0 && count != 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            GuestOp op = new();
            _com.Run(op, write ? s.Backend.Write(buffer, count, op) : s.Backend.Read(buffer, count, op), () =>
            {
                _com.W32(outCount, op.Value);
                Return(op.Hr);
            });
        }

        private void BeginTransfer(MfByteStream s, bool write)
        {
            long buffer = Arg(1), callback = Arg(3), state = Arg(4);
            int count = (int)Math.Min(Arg(2), CallFrame.SaneLengthLimit);
            if (callback == 0 || (buffer == 0 && count != 0))
            {
                Return(MediaCom.EPointer);
                return;
            }

            GuestOp op = new();
            long queued = MediaCom.SOk;

            IEnumerable<GuestCall> Script()
            {
                foreach (GuestCall c in write ? s.Backend.Write(buffer, count, op) : s.Backend.Read(buffer, count, op))
                {
                    yield return c;
                }

                List<GuestCall> calls = new();
                long result = CreateAsyncResult(0, callback, state, calls);
                MfAsyncResult r = (MfAsyncResult)_com.Lookup(result)!;
                r.Status = op.Hr;
                r.Transferred = (int)op.Value;
                r.Origin = s;
                queued = InvokeLater(result);
                _com.ReleaseAny(result, calls); // the queued invocation holds its own reference
                foreach (GuestCall c in calls)
                {
                    yield return c;
                }
            }

            _com.Run(op, Script(), () => Return(queued));
        }

        /// <summary>EndRead/EndWrite(IMFAsyncResult*, ULONG* pcb): the count, and the transfer's HRESULT.</summary>
        private void EndTransfer()
        {
            MfByteStream? s = _com.Self<MfByteStream>();
            if (s is null || _com.Lookup(Arg(1)) is not MfAsyncResult { Origin: { } origin } r || origin != s)
            {
                _com.W32(Arg(2), 0);
                Return(s is null || Arg(1) == 0 ? MediaCom.EPointer : MediaCom.EInvalidArg);
                return;
            }

            _com.W32(Arg(2), r.Transferred);
            Return(r.Status);
        }

        public long CreateByteStream(ByteBackend backend)
        {
            MfByteStream stream = new() { Backend = backend };
            if (backend.ContentType is { Length: > 0 } type)
            {
                stream.Attributes.Set(ByteStreamContentType, new MfValue { Type = MfValue.VtLpwstr, Text = type });
            }

            long pointer = _com.Expose(stream, ByteStreamShape);
            _com.Expose(stream, AttributesShape);
            return pointer;
        }

        public MfByteStream? ByteStream(long pointer) => _com.Lookup(pointer) as MfByteStream;

        /// <summary>
        /// A script reading a whole byte stream from its start, leaving its position where it was:
        /// <paramref name="bytes"/> gets the data when op.Hr is S_OK. For SetSourceFromByteStream.
        /// </summary>
        public IEnumerable<GuestCall> ReadAll(MfByteStream s, GuestOp op, Action<byte[]> bytes)
        {
            const int chunk = 256 * 1024;
            long saved = s.Backend.Position;
            s.Backend.Position = 0;
            MemoryStream collected = new();
            long scratch = _com.Alloc(chunk);
            try
            {
                while (true)
                {
                    foreach (GuestCall c in s.Backend.Read(scratch, chunk, op))
                    {
                        yield return c;
                    }

                    if (op.Hr != MediaCom.SOk || op.Value <= 0)
                    {
                        break;
                    }

                    collected.Write(_e.ReadMemory(scratch, (int)op.Value));
                    if (collected.Length > CallFrame.SaneLengthLimit)
                    {
                        op.Hr = MediaCom.EOutOfMemory;
                        break;
                    }
                }

                if (op.Hr == MediaCom.SOk)
                {
                    bytes(collected.ToArray());
                }
            }
            finally
            {
                _com.Free(scratch);
                s.Backend.Position = saved;
            }
        }

        // ------------------------------------------------------------------------------
        // The bridge functions
        // ------------------------------------------------------------------------------

        private void RegisterByteStreams(Dictionary<string, Action> handlers)
        {
            // HRESULT MFCreateMFByteStreamOnStreamEx(IUnknown* punkStream, IMFByteStream**): over a
            // WinRT IRandomAccessStream (or one of the host's IStreams).
            handlers["MFCreateMFByteStreamOnStreamEx"] = () => CreateOnStream(Arg(0), Arg(1), "MFCreateMFByteStreamOnStreamEx", acceptGuestIStream: false);

            // HRESULT MFCreateMFByteStreamOnStream(IStream*, IMFByteStream**).
            handlers["MFCreateMFByteStreamOnStream"] = () => CreateOnStream(Arg(0), Arg(1), "MFCreateMFByteStreamOnStream", acceptGuestIStream: true);

            // HRESULT MFCreateStreamOnMFByteStreamEx(IMFByteStream*, REFIID riid, void** ppv).
            handlers["MFCreateStreamOnMFByteStreamEx"] = () => CreateStreamOnByteStream(Arg(0), _com.ReadGuid(Arg(1)), Arg(2), "MFCreateStreamOnMFByteStreamEx");

            // HRESULT MFCreateStreamOnMFByteStream(IMFByteStream*, IStream**).
            handlers["MFCreateStreamOnMFByteStream"] = () => CreateStreamOnByteStream(Arg(0), IidStream, Arg(1), "MFCreateStreamOnMFByteStream");
        }

        private void CreateOnStream(long source, long outPointer, string function, bool acceptGuestIStream)
        {
            if (outPointer == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            _com.W32(outPointer, 0);
            if (source == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            if (_hostStreams?.Bind(source) is { } binding)
            {
                _com.W32(outPointer, CreateByteStream(new HostBackend(binding, _e)));
                Return(MediaCom.SOk);
                return;
            }

            if (_com.Lookup(source) is not null)
            {
                // One of the media objects, which is not a stream.
                Return(MediaCom.ENoInterface);
                return;
            }

            if (!acceptGuestIStream || _com.GuestMethod(source, 3) is null)
            {
                _com.Record($"{function} over a stream the game implemented itself (WinRT stream reads are asynchronous operations the host cannot wait on) -> E_NOTIMPL");
                Return(MediaCom.ENotImpl);
                return;
            }

            List<GuestCall> calls = new();
            _com.AddRefAny(source, calls);
            long created = CreateByteStream(new GuestStreamBackend(_com, source));
            _com.W32(outPointer, created);
            _com.Then(calls, _ => Return(MediaCom.SOk));
        }

        private void CreateStreamOnByteStream(long byteStream, Guid iid, long outPointer, string function)
        {
            if (outPointer == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            _com.W32(outPointer, 0);
            MfByteStream? s = ByteStream(byteStream);
            if (s is null)
            {
                if (byteStream != 0)
                {
                    _com.Record($"{function} over an IMFByteStream the game implemented itself -> E_NOTIMPL");
                }

                Return(byteStream == 0 ? MediaCom.EPointer : MediaCom.ENotImpl);
                return;
            }

            if (s.Backend.Host is { } binding)
            {
                long made = _hostStreams?.Expose(binding, iid) ?? 0;
                if (made == 0)
                {
                    _com.Record($"{function}({iid}): no stream of that interface can be made over this byte stream's data here -> E_NOINTERFACE");
                }

                _com.W32(outPointer, made);
                Return(made != 0 ? MediaCom.SOk : MediaCom.ENoInterface);
                return;
            }

            // Over a game's IStream: the IStream it wraps is the stream asked for.
            if (s.Backend is GuestStreamBackend guest && (iid == IidStream || iid == IidSequentialStream || iid == MediaCom.IidUnknown))
            {
                _com.QueryAny(guest.Stream, iid, outPointer, Return);
                return;
            }

            _com.Record($"{function}({iid}) over a byte stream on a game's own IStream: no WinRT stream can be made over guest code -> E_NOTIMPL");
            Return(MediaCom.ENotImpl);
        }
    }
}
