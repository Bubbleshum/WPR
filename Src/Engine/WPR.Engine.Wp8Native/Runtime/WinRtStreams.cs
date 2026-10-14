namespace WPR.Wp8Native
{
    /// <summary>
    /// Windows.Storage.Streams: <c>Buffer</c> (with <c>IBufferByteAccess</c>, which is how native code
    /// reaches a buffer's bytes), random-access streams over host files and memory, their input and
    /// output views, <c>DataReader</c>, <c>DataWriter</c>, <c>RandomAccessStream</c>'s copy statics and
    /// <c>RandomAccessStreamReference</c>.
    /// </summary>
    public sealed partial class WinRtRuntime
    {
        private sealed partial class SystemClasses
        {
            // -----------------------------------------------------------------------------------
            // Buffer. Its bytes live in guest memory, since IBufferByteAccess::Buffer hands out a
            // pointer the game reads and writes directly.
            // -----------------------------------------------------------------------------------

            public sealed class BufferState
            {
                public long Data { get; init; }

                public uint Capacity { get; init; }

                public uint Length { get; set; }
            }

            private static readonly InterfaceDef BufferByteAccess = Manual(
                "IBufferByteAccess",
                new Guid("905a0fef-bc53-11df-8c49-001e4fc686da"),
                ["Buffer() : Object"],   // HRESULT Buffer(byte** value): one pointer out
                unknownBased: true);

            private ComClass? _bufferClass;

            public long NewBuffer(uint capacity)
            {
                _bufferClass ??= Class("Windows.Storage.Streams.Buffer", [
                    I(
                        Meta("Windows.Storage.Streams.IBuffer"),
                        ("get_Capacity", c => c.RetU32(c.State<BufferState>().Capacity)),
                        ("get_Length", c => c.RetU32(c.State<BufferState>().Length)),
                        ("put_Length", c =>
                        {
                            BufferState b = c.State<BufferState>();
                            if ((uint)c.P(0) > b.Capacity)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            b.Length = (uint)c.P(0);
                            c.Ok();
                        })),
                    I(BufferByteAccess, ("Buffer", c => c.RetObj(c.State<BufferState>().Data))),
                ]);

                long data = _e.AllocateHeap(Math.Max(capacity, 1));
                _e.WriteMemory(data, new byte[Math.Max(capacity, 1)]);
                return New(_bufferClass, new BufferState { Data = data, Capacity = capacity }).Primary;
            }

            /// <summary>A buffer holding exactly <paramref name="content"/>.</summary>
            public long NewBuffer(byte[] content)
            {
                long buffer = NewBuffer((uint)content.Length);
                BufferState state = StateOf<BufferState>(buffer)!;
                if (content.Length > 0)
                {
                    _e.WriteMemory(state.Data, content);
                }

                state.Length = (uint)content.Length;
                return buffer;
            }

            // -----------------------------------------------------------------------------------
            // Streams. Every view of one underlying host stream has its own position, as WinRT's
            // GetInputStreamAt / GetOutputStreamAt / CloneStream do; the host stream closes with the
            // last view.
            // -----------------------------------------------------------------------------------

            public sealed class SharedStream(Stream data)
            {
                public Stream Data { get; } = data;

                public int Open { get; set; } = 1;
            }

            public sealed class StreamState
            {
                public required SharedStream Shared { get; init; }

                public long Position { get; set; }

                public bool CanRead { get; init; }

                public bool CanWrite { get; init; }

                public string ContentType { get; init; } = string.Empty;

                public bool Closed { get; set; }
            }

            private static readonly string BufferSig = Sig.Of("Windows.Storage.Streams.IBuffer");

            private bool Closed(RtCall c)
            {
                if (!c.State<StreamState>().Closed)
                {
                    return false;
                }

                c.Fail(RO_E_CLOSED);
                return true;
            }

            private Iface ClosableStream() => I(Closable, ("Close", c =>
            {
                StreamState s = c.State<StreamState>();
                if (!s.Closed)
                {
                    s.Closed = true;
                    if (--s.Shared.Open == 0)
                    {
                        s.Shared.Data.Dispose();
                    }
                }

                c.Ok();
            }));

            /// <summary>InputStreamOptions is a hint here: everything asked for that exists is read at once.</summary>
            private Iface InputStream() => I(
                Meta("Windows.Storage.Streams.IInputStream"),
                ("ReadAsync", c =>
                {
                    if (Closed(c))
                    {
                        return;
                    }

                    StreamState s = c.State<StreamState>();
                    long buffer = c.P(0);
                    uint count = (uint)c.P(1);
                    string progress = Sig.UInt32;
                    if (StateOf<BufferState>(buffer) is not { } b)
                    {
                        Note("IInputStream.ReadAsync: into a buffer the game implemented itself, which cannot be filled here");
                        c.RetObj(ProgressOperation(BufferSig, progress, _ => { }, E_INVALIDARG));
                        return;
                    }

                    if (!s.CanRead || count > b.Capacity)
                    {
                        c.RetObj(ProgressOperation(BufferSig, progress, _ => { }, s.CanRead ? E_INVALIDARG : E_ACCESSDENIED));
                        return;
                    }

                    byte[] bytes = new byte[count];
                    s.Shared.Data.Position = Math.Min(s.Position, s.Shared.Data.Length);
                    int read = s.Position >= s.Shared.Data.Length ? 0 : ReadFully(s.Shared.Data, bytes);
                    s.Position += read;
                    if (read > 0)
                    {
                        _e.WriteMemory(b.Data, bytes[..read]);
                    }

                    b.Length = (uint)read;
                    c.RetObj(ProgressOperation(BufferSig, progress, r => r.RetObj(buffer)));
                }));

            private static int ReadFully(Stream stream, byte[] into)
            {
                int total = 0;
                while (total < into.Length)
                {
                    int n = stream.Read(into, total, into.Length - total);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                }

                return total;
            }

            private Iface OutputStream() => I(
                Meta("Windows.Storage.Streams.IOutputStream"),
                ("WriteAsync", c =>
                {
                    if (Closed(c))
                    {
                        return;
                    }

                    StreamState s = c.State<StreamState>();
                    if (StateOf<BufferState>(c.P(0)) is not { } b)
                    {
                        Note("IOutputStream.WriteAsync: from a buffer the game implemented itself, which cannot be read here");
                        c.RetObj(ProgressOperation(Sig.UInt32, Sig.UInt32, _ => { }, E_INVALIDARG));
                        return;
                    }

                    if (!s.CanWrite)
                    {
                        c.RetObj(ProgressOperation(Sig.UInt32, Sig.UInt32, _ => { }, E_ACCESSDENIED));
                        return;
                    }

                    byte[] bytes = b.Length == 0 ? [] : _e.ReadMemory(b.Data, (int)b.Length);
                    WriteAt(s, bytes);
                    c.RetObj(ProgressOperation(Sig.UInt32, Sig.UInt32, r => r.RetU32((uint)bytes.Length)));
                }),
                ("FlushAsync", c =>
                {
                    if (Closed(c))
                    {
                        return;
                    }

                    c.State<StreamState>().Shared.Data.Flush();
                    c.RetObj(OperationOfBool(true));
                }));

            private static void WriteAt(StreamState s, byte[] bytes)
            {
                s.Shared.Data.Position = s.Position;
                s.Shared.Data.Write(bytes);
                s.Position += bytes.Length;
            }

            /// <summary>A random-access stream (IRandomAccessStream, IClosable, IInputStream, IOutputStream, and the content-type pair OpenReadAsync promises).</summary>
            public ComObject RandomAccessStream(SharedStream shared, bool canRead, bool canWrite, string contentType, string className = "Windows.Storage.Streams.FileRandomAccessStream", long position = 0)
            {
                ComClass cls = Class(className, [
                    I(
                        Meta("Windows.Storage.Streams.IRandomAccessStream"),
                        ("get_Size", c => c.RetU64((ulong)c.State<StreamState>().Shared.Data.Length)),
                        ("put_Size", c =>
                        {
                            if (Closed(c))
                            {
                                return;
                            }

                            StreamState s = c.State<StreamState>();
                            if (!s.CanWrite)
                            {
                                c.Fail(E_ACCESSDENIED);
                                return;
                            }

                            s.Shared.Data.SetLength(c.P64(0));
                            c.Ok();
                        }),
                        ("GetInputStreamAt", c =>
                        {
                            if (Closed(c))
                            {
                                return;
                            }

                            StreamState s = c.State<StreamState>();
                            c.RetObj(View(s, c.P64(0), input: true));
                        }),
                        ("GetOutputStreamAt", c =>
                        {
                            if (Closed(c))
                            {
                                return;
                            }

                            StreamState s = c.State<StreamState>();
                            if (!s.CanWrite)
                            {
                                c.Fail(E_ACCESSDENIED);
                                return;
                            }

                            c.RetObj(View(s, c.P64(0), input: false));
                        }),
                        ("get_Position", c => c.RetU64((ulong)c.State<StreamState>().Position)),
                        ("Seek", c =>
                        {
                            if (Closed(c))
                            {
                                return;
                            }

                            c.State<StreamState>().Position = c.P64(0);
                            c.Ok();
                        }),
                        ("CloneStream", c =>
                        {
                            if (Closed(c))
                            {
                                return;
                            }

                            StreamState s = c.State<StreamState>();
                            s.Shared.Open++;
                            c.RetObj(RandomAccessStream(s.Shared, s.CanRead, s.CanWrite, s.ContentType, c.Self.Class.Name).Primary);
                        }),
                        ("get_CanRead", c => c.RetBool(c.State<StreamState>().CanRead)),
                        ("get_CanWrite", c => c.RetBool(c.State<StreamState>().CanWrite))),
                    ClosableStream(),
                    InputStream(),
                    OutputStream(),
                    I(Meta("Windows.Storage.Streams.IRandomAccessStreamWithContentType")),
                    I(Meta("Windows.Storage.Streams.IContentTypeProvider"), ("get_ContentType", c => c.RetStr(c.State<StreamState>().ContentType))),
                ]);

                return New(cls, new StreamState { Shared = shared, CanRead = canRead, CanWrite = canWrite, ContentType = contentType, Position = position });
            }

            /// <summary>An input-only or output-only view at a position, sharing the host stream.</summary>
            private long View(StreamState of, long position, bool input)
            {
                of.Shared.Open++;
                ComClass cls = input
                    ? Class("Windows.Storage.Streams.InputStreamOverStream", [InputStream(), ClosableStream()])
                    : Class("Windows.Storage.Streams.OutputStreamOverStream", [OutputStream(), ClosableStream()]);
                return New(cls, new StreamState { Shared = of.Shared, CanRead = input, CanWrite = !input, Position = position }).Primary;
            }

            // -----------------------------------------------------------------------------------
            // DataReader and DataWriter.
            // -----------------------------------------------------------------------------------

            public sealed class ReaderState
            {
                public List<byte> Bytes { get; } = new();

                public int Offset { get; set; }

                public long Source { get; set; }

                public uint ByteOrder { get; set; }       // LittleEndian 0, BigEndian 1

                public uint Encoding { get; set; }        // Utf8 0, Utf16LE 1, Utf16BE 2

                public uint Options { get; set; }

                public int Unconsumed => Bytes.Count - Offset;
            }

            public sealed class WriterState
            {
                public List<byte> Bytes { get; } = new();

                public long Target { get; set; }

                public uint ByteOrder { get; set; }

                public uint Encoding { get; set; }
            }

            /// <summary>Takes <paramref name="n"/> bytes, in reading order (big-endian values reversed), or null when that many are not loaded.</summary>
            private static byte[]? Take(ReaderState r, int n, bool ordered = true)
            {
                if (r.Unconsumed < n)
                {
                    return null;
                }

                byte[] bytes = r.Bytes.GetRange(r.Offset, n).ToArray();
                r.Offset += n;
                if (ordered && r.ByteOrder == 1)
                {
                    Array.Reverse(bytes);
                }

                return bytes;
            }

            /// <summary>A Read* member that takes <paramref name="n"/> bytes and returns them, or fails E_BOUNDS when they are not loaded.</summary>
            private static Member ReadValue(int n, bool ordered = true) => c =>
            {
                if (Take(c.State<ReaderState>(), n, ordered) is not { } bytes)
                {
                    c.Fail(E_BOUNDS);
                    return;
                }

                c.RetBytes(bytes);
            };

            private static byte[] GuidOrder(byte[] bytes, uint byteOrder)
            {
                if (byteOrder == 1)
                {
                    Array.Reverse(bytes, 0, 4);
                    Array.Reverse(bytes, 4, 2);
                    Array.Reverse(bytes, 6, 2);
                }

                return bytes;
            }

            private static System.Text.Encoding StringEncoding(uint encoding) => TextEncoding((int)encoding);

            private static int CodeUnitBytes(uint encoding) => encoding == 0 ? 1 : 2;

            private long NewReader(long source, IEnumerable<byte> preloaded)
            {
                ComClass cls = Class("Windows.Storage.Streams.DataReader", [
                    I(
                        Meta("Windows.Storage.Streams.IDataReader"),
                        ("get_UnconsumedBufferLength", c => c.RetU32((uint)c.State<ReaderState>().Unconsumed)),
                        ("get_UnicodeEncoding", c => c.RetU32(c.State<ReaderState>().Encoding)),
                        ("put_UnicodeEncoding", c =>
                        {
                            c.State<ReaderState>().Encoding = (uint)c.P(0);
                            c.Ok();
                        }),
                        ("get_ByteOrder", c => c.RetU32(c.State<ReaderState>().ByteOrder)),
                        ("put_ByteOrder", c =>
                        {
                            c.State<ReaderState>().ByteOrder = (uint)c.P(0);
                            c.Ok();
                        }),
                        ("get_InputStreamOptions", c => c.RetU32(c.State<ReaderState>().Options)),
                        ("put_InputStreamOptions", c =>
                        {
                            c.State<ReaderState>().Options = (uint)c.P(0);
                            c.Ok();
                        }),
                        ("ReadByte", ReadValue(1)),
                        ("ReadBytes", c =>
                        {
                            (uint length, long data) = c.Array(0);
                            if (Take(c.State<ReaderState>(), (int)length, ordered: false) is not { } bytes)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            if (length > 0)
                            {
                                _e.WriteMemory(data, bytes);
                            }

                            c.Ok();
                        }),
                        ("ReadBuffer", c =>
                        {
                            if (Take(c.State<ReaderState>(), (int)c.P(0), ordered: false) is not { } bytes)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            c.RetObj(NewBuffer(bytes));
                        }),
                        ("ReadBoolean", c =>
                        {
                            if (Take(c.State<ReaderState>(), 1) is not { } bytes)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            c.RetBool(bytes[0] != 0);
                        }),
                        ("ReadGuid", c =>
                        {
                            ReaderState r = c.State<ReaderState>();
                            if (Take(r, 16, ordered: false) is not { } bytes)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            c.RetBytes(GuidOrder(bytes, r.ByteOrder));
                        }),
                        ("ReadInt16", ReadValue(2)),
                        ("ReadInt32", ReadValue(4)),
                        ("ReadInt64", ReadValue(8)),
                        ("ReadUInt16", ReadValue(2)),
                        ("ReadUInt32", ReadValue(4)),
                        ("ReadUInt64", ReadValue(8)),
                        ("ReadSingle", ReadValue(4)),
                        ("ReadDouble", ReadValue(8)),
                        ("ReadString", c =>
                        {
                            ReaderState r = c.State<ReaderState>();
                            if (Take(r, (int)c.P(0) * CodeUnitBytes(r.Encoding), ordered: false) is not { } bytes)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            c.RetStr(StringEncoding(r.Encoding).GetString(bytes));
                        }),
                        ("ReadDateTime", ReadValue(8)),
                        ("ReadTimeSpan", ReadValue(8)),
                        ("LoadAsync", c =>
                        {
                            ReaderState r = c.State<ReaderState>();
                            uint loaded = 0;
                            if (StateOf<StreamState>(r.Source) is { } s)
                            {
                                if (s.Closed)
                                {
                                    c.Fail(RO_E_CLOSED);
                                    return;
                                }

                                byte[] bytes = new byte[(int)Math.Min((uint)c.P(0), int.MaxValue / 2)];
                                s.Shared.Data.Position = Math.Min(s.Position, s.Shared.Data.Length);
                                int read = s.Position >= s.Shared.Data.Length ? 0 : ReadFully(s.Shared.Data, bytes);
                                s.Position += read;

                                // Consumed bytes are dropped as new ones come in, so a reader looping over a large
                                // stream does not hold all of it.
                                r.Bytes.RemoveRange(0, r.Offset);
                                r.Offset = 0;
                                r.Bytes.AddRange(bytes.AsSpan(0, read).ToArray());
                                loaded = (uint)read;
                            }
                            else if (r.Source != 0)
                            {
                                Note("DataReader.LoadAsync: from a stream the game implemented itself, which cannot be read here");
                            }

                            c.RetObj(Operation(Sig.UInt32, o => o.RetU32(loaded)));
                        }),
                        ("DetachBuffer", c =>
                        {
                            ReaderState r = c.State<ReaderState>();
                            byte[] rest = r.Bytes.GetRange(r.Offset, r.Unconsumed).ToArray();
                            r.Bytes.Clear();
                            r.Offset = 0;
                            c.RetObj(NewBuffer(rest));
                        }),
                        ("DetachStream", c =>
                        {
                            ReaderState r = c.State<ReaderState>();
                            long source = r.Source;
                            r.Source = 0;
                            c.RetObj(source);
                        })),
                    I(Closable, ("Close", c =>
                    {
                        // Closing a reader closes the stream it owns.
                        ReaderState r = c.State<ReaderState>();
                        if (StateOf<StreamState>(r.Source) is { Closed: false } s)
                        {
                            s.Closed = true;
                            if (--s.Shared.Open == 0)
                            {
                                s.Shared.Data.Dispose();
                            }
                        }

                        c.Ok();
                    })),
                ]);

                ReaderState state = new() { Source = source };
                state.Bytes.AddRange(preloaded);
                return New(cls, state).Primary;
            }

            private Member WriteValue(Func<RtCall, byte[]> bytes, bool ordered = true) => c =>
            {
                WriterState w = c.State<WriterState>();
                byte[] value = bytes(c);
                if (ordered && w.ByteOrder == 1)
                {
                    Array.Reverse(value);
                }

                w.Bytes.AddRange(value);
                c.Ok();
            };

            private long NewWriter(long target)
            {
                ComClass cls = Class("Windows.Storage.Streams.DataWriter", [
                    I(
                        Meta("Windows.Storage.Streams.IDataWriter"),
                        ("get_UnstoredBufferLength", c => c.RetU32((uint)c.State<WriterState>().Bytes.Count)),
                        ("get_UnicodeEncoding", c => c.RetU32(c.State<WriterState>().Encoding)),
                        ("put_UnicodeEncoding", c =>
                        {
                            c.State<WriterState>().Encoding = (uint)c.P(0);
                            c.Ok();
                        }),
                        ("get_ByteOrder", c => c.RetU32(c.State<WriterState>().ByteOrder)),
                        ("put_ByteOrder", c =>
                        {
                            c.State<WriterState>().ByteOrder = (uint)c.P(0);
                            c.Ok();
                        }),
                        ("WriteByte", WriteValue(c => [(byte)c.P(0)])),
                        ("WriteBytes", WriteValue(c => ArrayBytes(c, 0), ordered: false)),
                        ("WriteBuffer", c =>
                        {
                            if (BufferBytes(c.P(0)) is not { } bytes)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            c.State<WriterState>().Bytes.AddRange(bytes);
                            c.Ok();
                        }),
                        ("WriteBuffer/2", c =>
                        {
                            uint start = (uint)c.P(1), count = (uint)c.P(2);
                            if (BufferBytes(c.P(0)) is not { } bytes || (ulong)start + count > (ulong)bytes.Length)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            c.State<WriterState>().Bytes.AddRange(bytes.AsSpan((int)start, (int)count).ToArray());
                            c.Ok();
                        }),
                        ("WriteBoolean", WriteValue(c => [(c.P(0) & 0xFF) != 0 ? (byte)1 : (byte)0])),
                        ("WriteGuid", c =>
                        {
                            WriterState w = c.State<WriterState>();
                            w.Bytes.AddRange(GuidOrder(c.PBytes(0, 4), w.ByteOrder));
                            c.Ok();
                        }),
                        ("WriteInt16", WriteValue(c => BitConverter.GetBytes((short)c.P(0)))),
                        ("WriteInt32", WriteValue(c => BitConverter.GetBytes((int)c.P(0)))),
                        ("WriteInt64", WriteValue(c => BitConverter.GetBytes(c.P64(0)))),
                        ("WriteUInt16", WriteValue(c => BitConverter.GetBytes((ushort)c.P(0)))),
                        ("WriteUInt32", WriteValue(c => BitConverter.GetBytes((uint)c.P(0)))),
                        ("WriteUInt64", WriteValue(c => BitConverter.GetBytes((ulong)c.P64(0)))),
                        ("WriteSingle", WriteValue(_ => BitConverter.GetBytes(VfpSingle(0)))),     // hard-float: s0
                        ("WriteDouble", WriteValue(_ => BitConverter.GetBytes(VfpDouble(0)))),     // d0
                        ("WriteDateTime", WriteValue(c => BitConverter.GetBytes(c.P64(0)))),
                        ("WriteTimeSpan", WriteValue(c => BitConverter.GetBytes(c.P64(0)))),
                        ("WriteString", c =>
                        {
                            WriterState w = c.State<WriterState>();
                            byte[] bytes = StringEncoding(w.Encoding).GetBytes(c.Str(0));
                            w.Bytes.AddRange(bytes);
                            c.RetU32((uint)bytes.Length);
                        }),
                        ("MeasureString", c => c.RetU32((uint)StringEncoding(c.State<WriterState>().Encoding).GetByteCount(c.Str(0)))),
                        ("StoreAsync", c =>
                        {
                            WriterState w = c.State<WriterState>();
                            if (StateOf<StreamState>(w.Target) is not { } s)
                            {
                                if (w.Target != 0)
                                {
                                    Note("DataWriter.StoreAsync: to a stream the game implemented itself, which cannot be written here");
                                }

                                c.Fail(E_ILLEGAL_METHOD_CALL);
                                return;
                            }

                            if (s.Closed)
                            {
                                c.Fail(RO_E_CLOSED);
                                return;
                            }

                            byte[] bytes = w.Bytes.ToArray();
                            w.Bytes.Clear();
                            WriteAt(s, bytes);
                            c.RetObj(Operation(Sig.UInt32, o => o.RetU32((uint)bytes.Length)));
                        }),
                        ("FlushAsync", c =>
                        {
                            if (StateOf<StreamState>(c.State<WriterState>().Target) is { Closed: false } s)
                            {
                                s.Shared.Data.Flush();
                            }

                            c.RetObj(OperationOfBool(true));
                        }),
                        ("DetachBuffer", c =>
                        {
                            WriterState w = c.State<WriterState>();
                            byte[] bytes = w.Bytes.ToArray();
                            w.Bytes.Clear();
                            c.RetObj(NewBuffer(bytes));
                        }),
                        ("DetachStream", c =>
                        {
                            WriterState w = c.State<WriterState>();
                            long target = w.Target;
                            w.Target = 0;
                            c.RetObj(target);
                        })),
                    I(Closable, ("Close", c =>
                    {
                        if (StateOf<StreamState>(c.State<WriterState>().Target) is { Closed: false } s)
                        {
                            s.Closed = true;
                            if (--s.Shared.Open == 0)
                            {
                                s.Shared.Data.Dispose();
                            }
                        }

                        c.Ok();
                    })),
                ]);

                return New(cls, new WriterState { Target = target }).Primary;
            }

            /// <summary>A copy between two streams of this runtime's, at their positions: the bytes moved, or null when either is not one of ours.</summary>
            private ulong? CopyStreams(long input, long output, ulong? count, bool close)
            {
                if (StateOf<StreamState>(input) is not { CanRead: true, Closed: false } from ||
                    StateOf<StreamState>(output) is not { CanWrite: true, Closed: false } to)
                {
                    return null;
                }

                from.Shared.Data.Position = Math.Min(from.Position, from.Shared.Data.Length);
                long available = Math.Max(0, from.Shared.Data.Length - from.Position);
                byte[] bytes = new byte[(int)Math.Min(count ?? (ulong)available, (ulong)available)];
                int read = ReadFully(from.Shared.Data, bytes);
                from.Position += read;
                WriteAt(to, bytes[..read]);
                if (close)
                {
                    foreach (StreamState s in new[] { from, to })
                    {
                        s.Closed = true;
                        if (--s.Shared.Open == 0)
                        {
                            s.Shared.Data.Dispose();
                        }
                    }
                }

                return (ulong)read;
            }

            private Member Copy(bool hasCount, bool close) => c =>
            {
                ulong? moved = CopyStreams(c.P(0), c.P(1), hasCount ? (ulong)c.P64(2) : null, close);
                c.RetObj(moved is { } n
                    ? ProgressOperation(Sig.UInt64, Sig.UInt64, r => r.RetU64(n))
                    : ProgressOperation(Sig.UInt64, Sig.UInt64, _ => { }, E_INVALIDARG));
            };

            private void RegisterStreams()
            {
                Factory(
                    "Windows.Storage.Streams.Buffer",
                    I(Meta("Windows.Storage.Streams.IBufferFactory"), ("Create", c => c.RetObj(NewBuffer((uint)c.P(0))))));

                Factory(
                    "Windows.Storage.Streams.DataReader",
                    I(
                        Meta("Windows.Storage.Streams.IDataReaderFactory"),
                        ("CreateDataReader", c =>
                        {
                            if (c.P(0) == 0)
                            {
                                c.Fail(E_POINTER);
                                return;
                            }

                            if (StateOf<StreamState>(c.P(0)) is null)
                            {
                                Note("DataReader: over a stream the game implemented itself; LoadAsync will load nothing");
                            }

                            c.RetObj(NewReader(c.P(0), []));
                        })),
                    I(
                        Meta("Windows.Storage.Streams.IDataReaderStatics"),
                        ("FromBuffer", c =>
                        {
                            if (BufferBytes(c.P(0)) is not { } bytes)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            c.RetObj(NewReader(0, bytes));
                        })));

                Factory(
                    "Windows.Storage.Streams.DataWriter",
                    I(ActivationFactory, ("ActivateInstance", c => c.RetObj(NewWriter(0)))),
                    I(
                        Meta("Windows.Storage.Streams.IDataWriterFactory"),
                        ("CreateDataWriter", c =>
                        {
                            if (c.P(0) == 0)
                            {
                                c.Fail(E_POINTER);
                                return;
                            }

                            c.RetObj(NewWriter(c.P(0)));
                        })));

                Factory(
                    "Windows.Storage.Streams.InMemoryRandomAccessStream",
                    I(ActivationFactory, ("ActivateInstance", c => c.RetObj(
                        RandomAccessStream(new SharedStream(new MemoryStream()), canRead: true, canWrite: true, string.Empty, "Windows.Storage.Streams.InMemoryRandomAccessStream").Primary))));

                Factory(
                    "Windows.Storage.Streams.RandomAccessStream",
                    I(
                        Meta("Windows.Storage.Streams.IRandomAccessStreamStatics"),
                        ("CopyAsync", Copy(hasCount: false, close: false)),
                        ("CopyAsync/2", Copy(hasCount: true, close: false)),
                        ("CopyAndCloseAsync", Copy(hasCount: false, close: true))));

                Factory(
                    "Windows.Storage.Streams.RandomAccessStreamReference",
                    I(
                        Meta("Windows.Storage.Streams.IRandomAccessStreamReferenceStatics"),
                        ("CreateFromFile", c =>
                        {
                            if (StateOf<ItemState>(c.P(0)) is not { IsFolder: false } file)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            c.RetObj(StreamReference(() => OpenRead(file, withContentType: true)));
                        }),
                        ("CreateFromUri", c =>
                        {
                            long uri = c.P(0);
                            c.RetObj(StreamReference(() => FileAtApplicationUri(uri) is { Item: { } item }
                                ? OpenRead(item, withContentType: true)
                                : FailedOperation(StreamWithContentTypeSig, UriText(uri) is { } text && text.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                    ? unchecked((int)0x80072EFD)    // WININET_E_CANNOT_CONNECT: this runtime is offline
                                    : FileNotFound)));
                        }),
                        ("CreateFromStream", c =>
                        {
                            if (StateOf<StreamState>(c.P(0)) is not { } stream)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            c.RetObj(StreamReference(() =>
                            {
                                stream.Shared.Open++;
                                ComObject clone = RandomAccessStream(stream.Shared, stream.CanRead, stream.CanWrite, stream.ContentType);
                                return OperationOfObject(StreamWithContentTypeSig, PointerFor(clone, "Windows.Storage.Streams.IRandomAccessStreamWithContentType"));
                            }));
                        })));
            }

            // -----------------------------------------------------------------------------------
            // Media Foundation's view of these streams (MediaByteStream.cs).
            // -----------------------------------------------------------------------------------

            /// <summary>A host binding to the stream behind <paramref name="pointer"/>, holding the data open; null when it is not one of these.</summary>
            public HostStreamBinding? BindStream(long pointer)
            {
                if (StateOf<StreamState>(pointer) is not { } s)
                {
                    return null;
                }

                s.Shared.Open++;
                bool released = false;
                return new HostStreamBinding
                {
                    Data = s.Shared.Data,
                    GetPosition = () => s.Position,
                    SetPosition = p => s.Position = p,
                    CanRead = s.CanRead,
                    CanWrite = s.CanWrite,
                    ContentType = s.ContentType,
                    IsClosed = () => s.Closed,
                    Source = pointer,
                    Release = () =>
                    {
                        if (!released)
                        {
                            released = true;
                            if (--s.Shared.Open == 0)
                            {
                                s.Shared.Data.Dispose();
                            }
                        }
                    },
                };
            }

            /// <summary>
            /// A new random-access stream over <paramref name="data"/> (a stream of these when the
            /// binding came from one, sharing its data), at <paramref name="position"/>, as interface
            /// <paramref name="iid"/>; 0 when it has no such interface.
            /// </summary>
            public long StreamOver(HostStreamBinding binding, Guid iid)
            {
                SharedStream shared;
                string contentType = binding.ContentType ?? string.Empty;
                if (StateOf<StreamState>(binding.Source) is { } source)
                {
                    shared = source.Shared;
                    shared.Open++;
                }
                else
                {
                    shared = new SharedStream(binding.Data);
                }

                ComObject stream = RandomAccessStream(shared, binding.CanRead, binding.CanWrite, contentType, position: binding.GetPosition());
                if (iid == HostStubs.IidUnknown || iid == HostStubs.IidInspectable)
                {
                    return stream.Primary;
                }

                if (stream.Pointers.TryGetValue(iid, out long pointer))
                {
                    return pointer;
                }

                // Nothing asked for that this stream is; it is dropped unseen.
                if (--shared.Open == 0)
                {
                    shared.Data.Dispose();
                }

                return 0;
            }

            private long StreamReference(Func<long> open)
            {
                ComClass cls = Class("Windows.Storage.Streams.RandomAccessStreamReference", [
                    I(Meta("Windows.Storage.Streams.IRandomAccessStreamReference"), ("OpenReadAsync", c => c.RetObj(((Func<long>)c.Self.State!)()))),
                ]);
                return New(cls, open).Primary;
            }
        }
    }
}
