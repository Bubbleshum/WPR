using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using WPR.WinRT;

namespace Windows.Storage.Streams
{
    /// <summary>Shim for <c>Windows.Storage.Streams.UnicodeEncoding</c>.</summary>
    public enum UnicodeEncoding
    {
        Utf8 = 0,
        Utf16LE = 1,
        Utf16BE = 2,
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.ByteOrder</c>.</summary>
    public enum ByteOrder
    {
        LittleEndian = 0,
        BigEndian = 1,
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.DataReaderLoadOperation</c>: the bytes loaded.</summary>
    public sealed class DataReaderLoadOperation : WprAsyncUInt32
    {
        internal DataReaderLoadOperation(Task<uint> task) : base(task)
        {
        }
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.DataWriterStoreOperation</c>: the bytes stored.</summary>
    public sealed class DataWriterStoreOperation : WprAsyncUInt32
    {
        internal DataWriterStoreOperation(Task<uint> task) : base(task)
        {
        }
    }

    /// <summary>The two named <c>IAsyncOperation&lt;uint&gt;</c> classes WinRT's data reader and writer return.</summary>
    public abstract class WprAsyncUInt32 : IAsyncOperation<uint>
    {
        private readonly TaskAsyncOperation<uint> _inner;

        private protected WprAsyncUInt32(Task<uint> task) => _inner = new TaskAsyncOperation<uint>(task);

        public uint Id => _inner.Id;

        public AsyncStatus Status => _inner.Status;

        public Exception? ErrorCode => _inner.ErrorCode;

        public void Cancel() => _inner.Cancel();

        public void Close() => _inner.Close();

        public AsyncOperationCompletedHandler<uint>? get_Completed() => _inner.get_Completed();

        public void put_Completed(AsyncOperationCompletedHandler<uint>? handler) =>
            _inner.put_Completed(handler == null ? null : (_, status) => handler(this, status));

        public uint GetResults() => _inner.GetResults();
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.DataReader</c>: reads typed values from loaded bytes.</summary>
    public sealed class DataReader : IDisposable
    {
        private IInputStream? _stream;
        private byte[] _data;
        private int _position;

        public DataReader(IInputStream inputStream)
        {
            _stream = inputStream;
            _data = Array.Empty<byte>();
        }

        private DataReader(byte[] data)
        {
            _data = data;
        }

        public static DataReader FromBuffer(IBuffer buffer) => new(buffer.ToArray());

        public UnicodeEncoding UnicodeEncoding { get; private set; }

        public void put_UnicodeEncoding(UnicodeEncoding value) => UnicodeEncoding = value;

        public ByteOrder ByteOrder { get; private set; }

        public void put_ByteOrder(ByteOrder value) => ByteOrder = value;

        public InputStreamOptions InputStreamOptions { get; private set; }

        public void put_InputStreamOptions(InputStreamOptions value) => InputStreamOptions = value;

        public uint UnconsumedBufferLength => (uint)(_data.Length - _position);

        /// <summary>Reads up to <paramref name="count"/> more bytes; fewer only at the end of the stream (or with Partial).</summary>
        public DataReaderLoadOperation LoadAsync(uint count)
        {
            IInputStream stream = _stream ?? throw new InvalidOperationException("This reader has no stream.");
            return new DataReaderLoadOperation(Task.Run(async () =>
            {
                uint loaded = 0;
                while (loaded < count)
                {
                    IBuffer chunk = await stream.ReadAsync(WindowsRuntimeBuffer.Create((int)(count - loaded)), count - loaded, InputStreamOptions)
                        .AsTask().ConfigureAwait(false);
                    if (chunk.Length == 0) break;
                    Append(chunk.ToArray());
                    loaded += chunk.Length;
                    if ((InputStreamOptions & InputStreamOptions.Partial) != 0) break;
                }

                return loaded;
            }));
        }

        private void Append(byte[] more)
        {
            var merged = new byte[_data.Length - _position + more.Length];
            Array.Copy(_data, _position, merged, 0, _data.Length - _position);
            Array.Copy(more, 0, merged, _data.Length - _position, more.Length);
            _data = merged;
            _position = 0;
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _data.Length - _position)
            {
                throw new Exception("The operation attempted to access data outside the valid range.") { HResult = unchecked((int)0x8000000B) };
            }

            var span = new ReadOnlySpan<byte>(_data, _position, count);
            _position += count;
            return span;
        }

        private ReadOnlySpan<byte> Ordered(int count)
        {
            ReadOnlySpan<byte> raw = Take(count);
            if ((ByteOrder == ByteOrder.BigEndian) == BitConverter.IsLittleEndian)
            {
                byte[] flipped = raw.ToArray();
                Array.Reverse(flipped);
                return flipped;
            }

            return raw;
        }

        public byte ReadByte() => Take(1)[0];

        public void ReadBytes(byte[] value) => Take(value.Length).CopyTo(value);

        public IBuffer ReadBuffer(uint length) => Take((int)length).ToArray().AsBuffer();

        public bool ReadBoolean() => ReadByte() != 0;

        public short ReadInt16() => BitConverter.ToInt16(Ordered(2));

        public ushort ReadUInt16() => BitConverter.ToUInt16(Ordered(2));

        public int ReadInt32() => BitConverter.ToInt32(Ordered(4));

        public uint ReadUInt32() => BitConverter.ToUInt32(Ordered(4));

        public long ReadInt64() => BitConverter.ToInt64(Ordered(8));

        public ulong ReadUInt64() => BitConverter.ToUInt64(Ordered(8));

        public float ReadSingle() => BitConverter.ToSingle(Ordered(4));

        public double ReadDouble() => BitConverter.ToDouble(Ordered(8));

        public Guid ReadGuid() => new(Take(16));

        /// <summary><paramref name="codeUnitCount"/> is in bytes for UTF-8, in UTF-16 units otherwise, as WinRT's.</summary>
        public string ReadString(uint codeUnitCount) => UnicodeEncoding switch
        {
            UnicodeEncoding.Utf16LE => Encoding.Unicode.GetString(Take((int)codeUnitCount * 2)),
            UnicodeEncoding.Utf16BE => Encoding.BigEndianUnicode.GetString(Take((int)codeUnitCount * 2)),
            _ => Encoding.UTF8.GetString(Take((int)codeUnitCount)),
        };

        public IInputStream? DetachStream()
        {
            IInputStream? stream = _stream;
            _stream = null;
            return stream;
        }

        public IBuffer DetachBuffer()
        {
            IBuffer rest = _data.AsSpan(_position).ToArray().AsBuffer();
            _data = Array.Empty<byte>();
            _position = 0;
            return rest;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.DataWriter</c>: collects typed values, then stores them to the stream.</summary>
    public sealed class DataWriter : IDisposable
    {
        private IOutputStream? _stream;
        private readonly MemoryStream _pending = new();

        public DataWriter()
        {
        }

        public DataWriter(IOutputStream outputStream) => _stream = outputStream;

        public UnicodeEncoding UnicodeEncoding { get; private set; }

        public void put_UnicodeEncoding(UnicodeEncoding value) => UnicodeEncoding = value;

        public ByteOrder ByteOrder { get; private set; }

        public void put_ByteOrder(ByteOrder value) => ByteOrder = value;

        public uint UnstoredBufferLength => (uint)_pending.Length;

        private void Ordered(byte[] bytes)
        {
            if ((ByteOrder == ByteOrder.BigEndian) == BitConverter.IsLittleEndian) Array.Reverse(bytes);
            _pending.Write(bytes);
        }

        public void WriteByte(byte value) => _pending.WriteByte(value);

        public void WriteBytes(byte[] value) => _pending.Write(value);

        public void WriteBuffer(IBuffer buffer) => _pending.Write(buffer.ToArray());

        public void WriteBuffer(IBuffer buffer, uint start, uint count) => _pending.Write(buffer.ToArray(start, (int)count));

        public void WriteBoolean(bool value) => _pending.WriteByte(value ? (byte)1 : (byte)0);

        public void WriteInt16(short value) => Ordered(BitConverter.GetBytes(value));

        public void WriteUInt16(ushort value) => Ordered(BitConverter.GetBytes(value));

        public void WriteInt32(int value) => Ordered(BitConverter.GetBytes(value));

        public void WriteUInt32(uint value) => Ordered(BitConverter.GetBytes(value));

        public void WriteInt64(long value) => Ordered(BitConverter.GetBytes(value));

        public void WriteUInt64(ulong value) => Ordered(BitConverter.GetBytes(value));

        public void WriteSingle(float value) => Ordered(BitConverter.GetBytes(value));

        public void WriteDouble(double value) => Ordered(BitConverter.GetBytes(value));

        public void WriteGuid(Guid value) => _pending.Write(value.ToByteArray());

        /// <summary>Returns the code units written: bytes for UTF-8, UTF-16 units otherwise.</summary>
        public uint WriteString(string value)
        {
            byte[] bytes = UnicodeEncoding switch
            {
                UnicodeEncoding.Utf16LE => Encoding.Unicode.GetBytes(value),
                UnicodeEncoding.Utf16BE => Encoding.BigEndianUnicode.GetBytes(value),
                _ => Encoding.UTF8.GetBytes(value),
            };
            _pending.Write(bytes);
            return UnicodeEncoding == UnicodeEncoding.Utf8 ? (uint)bytes.Length : (uint)value.Length;
        }

        public uint MeasureString(string value) =>
            UnicodeEncoding == UnicodeEncoding.Utf8 ? (uint)Encoding.UTF8.GetByteCount(value) : (uint)value.Length;

        public DataWriterStoreOperation StoreAsync()
        {
            IOutputStream stream = _stream ?? throw new InvalidOperationException("This writer has no stream.");
            byte[] bytes = _pending.ToArray();
            _pending.SetLength(0);
            return new DataWriterStoreOperation(Task.Run(async () =>
                await stream.WriteAsync(bytes.AsBuffer()).AsTask().ConfigureAwait(false)));
        }

        public IAsyncOperation<bool> FlushAsync() =>
            _stream?.FlushAsync() ?? WinRtAsync.FromResult(true);

        public IBuffer DetachBuffer()
        {
            IBuffer bytes = _pending.ToArray().AsBuffer();
            _pending.SetLength(0);
            return bytes;
        }

        public IOutputStream? DetachStream()
        {
            IOutputStream? stream = _stream;
            _stream = null;
            return stream;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
