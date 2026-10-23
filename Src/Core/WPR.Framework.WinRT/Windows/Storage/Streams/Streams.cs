using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using WPR.WinRT;

namespace Windows.Storage.Streams
{
    /// <summary>Shim for <c>Windows.Storage.Streams.InputStreamOptions</c>.</summary>
    [Flags]
    public enum InputStreamOptions : uint
    {
        None = 0,
        Partial = 1,
        ReadAhead = 2,
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.IBuffer</c>.</summary>
    public interface IBuffer
    {
        uint Capacity { get; }

        uint Length { get; }

        /// <summary>WinRT names the setter put_Length, which is what game IL calls.</summary>
        void put_Length(uint value);
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.IInputStream</c>.</summary>
    public interface IInputStream : IDisposable
    {
        IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options);
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.IOutputStream</c>.</summary>
    public interface IOutputStream : IDisposable
    {
        IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer);

        IAsyncOperation<bool> FlushAsync();
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.IRandomAccessStream</c>.</summary>
    public interface IRandomAccessStream : IInputStream, IOutputStream
    {
        ulong Size { get; }

        ulong Position { get; }

        bool CanRead { get; }

        bool CanWrite { get; }

        void put_Size(ulong value);

        void Seek(ulong position);

        IInputStream GetInputStreamAt(ulong position);

        IOutputStream GetOutputStreamAt(ulong position);

        IRandomAccessStream CloneStream();
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.IContentTypeProvider</c>.</summary>
    public interface IContentTypeProvider
    {
        string ContentType { get; }
    }

    /// <summary>Shim for <c>Windows.Storage.Streams.IRandomAccessStreamWithContentType</c>.</summary>
    public interface IRandomAccessStreamWithContentType : IRandomAccessStream, IContentTypeProvider
    {
    }

    /// <summary>
    /// A WinRT stream over a host <see cref="Stream"/>: what <c>StorageFile.Open*Async</c>,
    /// <c>AsInputStream</c> and friends hand back. The input/output views
    /// (<c>GetInputStreamAt</c>/<c>GetOutputStreamAt</c>) share the stream and keep their own
    /// position, as WinRT's did.
    /// </summary>
    public sealed class RandomAccessStream : IRandomAccessStreamWithContentType
    {
        private readonly Stream _stream;
        private readonly bool _ownsStream;

        internal RandomAccessStream(Stream stream, string contentType = "application/octet-stream", bool ownsStream = true)
        {
            _stream = stream;
            _ownsStream = ownsStream;
            ContentType = contentType;
        }

        /// <summary>The host stream behind this one, for <c>AsStreamForRead</c>/<c>AsStreamForWrite</c>.</summary>
        internal Stream Inner => _stream;

        public string ContentType { get; }

        public ulong Size => (ulong)_stream.Length;

        public ulong Position => (ulong)_stream.Position;

        public bool CanRead => _stream.CanRead;

        public bool CanWrite => _stream.CanWrite;

        public void put_Size(ulong value) => _stream.SetLength((long)value);

        public void Seek(ulong position) => _stream.Position = (long)position;

        public IInputStream GetInputStreamAt(ulong position) => new View(_stream, (long)position);

        public IOutputStream GetOutputStreamAt(ulong position) => new View(_stream, (long)position);

        public IRandomAccessStream CloneStream() => new RandomAccessStream(_stream, ContentType, ownsStream: false);

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options) =>
            StreamIo.Read(_stream, -1, buffer, count);

        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) => StreamIo.Write(_stream, -1, buffer);

        public IAsyncOperation<bool> FlushAsync()
        {
            _stream.Flush();
            return WinRtAsync.FromResult(true);
        }

        public void Dispose()
        {
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }

        /// <summary>An input or output view at its own position over a shared stream.</summary>
        private sealed class View : IInputStream, IOutputStream
        {
            private readonly Stream _stream;
            private long _position;

            public View(Stream stream, long position)
            {
                _stream = stream;
                _position = position;
            }

            public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
            {
                lock (_stream)
                {
                    var op = StreamIo.Read(_stream, _position, buffer, count);
                    _position += buffer.Length;
                    return op;
                }
            }

            public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer)
            {
                lock (_stream)
                {
                    var op = StreamIo.Write(_stream, _position, buffer);
                    _position += buffer.Length;
                    return op;
                }
            }

            public IAsyncOperation<bool> FlushAsync()
            {
                _stream.Flush();
                return WinRtAsync.FromResult(true);
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Synchronous reads and writes, answered as completed WinRT operations.</summary>
    internal static class StreamIo
    {
        public static IAsyncOperationWithProgress<IBuffer, uint> Read(Stream stream, long at, IBuffer buffer, uint count)
        {
            try
            {
                var target = (global::System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBuffer)buffer;
                int wanted = (int)Math.Min(count, target.Capacity);
                if (at >= 0)
                {
                    stream.Position = at;
                }

                int total = 0;
                while (total < wanted)
                {
                    int n = stream.Read(target.Data, total, wanted - total);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                }

                target.put_Length((uint)total);
                return new TaskAsyncOperationWithProgress<IBuffer, uint>(Task.FromResult<IBuffer>(target));
            }
            catch (Exception ex)
            {
                return new TaskAsyncOperationWithProgress<IBuffer, uint>(Task.FromException<IBuffer>(ex));
            }
        }

        public static IAsyncOperationWithProgress<uint, uint> Write(Stream stream, long at, IBuffer buffer)
        {
            try
            {
                var source = (global::System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBuffer)buffer;
                if (at >= 0)
                {
                    stream.Position = at;
                }

                stream.Write(source.Data, 0, (int)source.Length);
                return new TaskAsyncOperationWithProgress<uint, uint>(Task.FromResult(source.Length));
            }
            catch (Exception ex)
            {
                return new TaskAsyncOperationWithProgress<uint, uint>(Task.FromException<uint>(ex));
            }
        }
    }
}

namespace System.Runtime.InteropServices.WindowsRuntime
{
    using global::Windows.Storage.Streams;

    /// <summary>Shim for <c>System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBuffer</c>: an <see cref="IBuffer"/> over a byte array.</summary>
    public sealed class WindowsRuntimeBuffer : IBuffer
    {
        private uint _length;

        internal WindowsRuntimeBuffer(byte[] data, uint length)
        {
            Data = data;
            _length = length;
        }

        internal byte[] Data { get; }

        public uint Capacity => (uint)Data.Length;

        public uint Length => _length;

        public void put_Length(uint value)
        {
            if (value > Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _length = value;
        }

        public static IBuffer Create(int capacity) => new WindowsRuntimeBuffer(new byte[capacity], 0);

        public static IBuffer Create(byte[] data, int offset, int length, int capacity)
        {
            var bytes = new byte[capacity];
            Array.Copy(data, offset, bytes, 0, length);
            return new WindowsRuntimeBuffer(bytes, (uint)length);
        }
    }

    /// <summary>Shim for <c>System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions</c>.</summary>
    public static class WindowsRuntimeBufferExtensions
    {
        /// <summary>Wraps the array itself, as .NET's did (writes through the buffer land in it).</summary>
        public static IBuffer AsBuffer(this byte[] source) => new WindowsRuntimeBuffer(source, (uint)source.Length);

        public static IBuffer AsBuffer(this byte[] source, int offset, int length)
        {
            if (offset == 0)
            {
                return new WindowsRuntimeBuffer(source, (uint)length);
            }

            var copy = new byte[length];
            Array.Copy(source, offset, copy, 0, length);
            return new WindowsRuntimeBuffer(copy, (uint)length);
        }

        public static byte[] ToArray(this IBuffer source)
        {
            var b = (WindowsRuntimeBuffer)source;
            var result = new byte[b.Length];
            Array.Copy(b.Data, result, result.Length);
            return result;
        }

        public static byte[] ToArray(this IBuffer source, uint sourceIndex, int count)
        {
            var b = (WindowsRuntimeBuffer)source;
            var result = new byte[count];
            Array.Copy(b.Data, sourceIndex, result, 0, count);
            return result;
        }

        public static System.IO.Stream AsStream(this IBuffer source)
        {
            var b = (WindowsRuntimeBuffer)source;
            return new System.IO.MemoryStream(b.Data, 0, (int)b.Length, writable: true);
        }

        public static void CopyTo(this byte[] source, IBuffer destination) =>
            CopyTo(source, 0, destination, 0, source.Length);

        public static void CopyTo(this byte[] source, int sourceIndex, IBuffer destination, uint destinationIndex, int count)
        {
            var b = (WindowsRuntimeBuffer)destination;
            Array.Copy(source, sourceIndex, b.Data, destinationIndex, count);
            if (destinationIndex + count > b.Length)
            {
                b.put_Length((uint)(destinationIndex + count));
            }
        }
    }
}

namespace System.IO
{
    using System.Threading.Tasks;
    using global::Windows.Storage;
    using global::Windows.Storage.Streams;

    /// <summary>Shim for <c>System.IO.WindowsRuntimeStreamExtensions</c>.</summary>
    public static class WindowsRuntimeStreamExtensions
    {
        public static Stream AsStreamForRead(this IInputStream windowsRuntimeStream) => Unwrap(windowsRuntimeStream);

        public static Stream AsStreamForRead(this IInputStream windowsRuntimeStream, int bufferSize) => Unwrap(windowsRuntimeStream);

        public static Stream AsStreamForWrite(this IOutputStream windowsRuntimeStream) => Unwrap(windowsRuntimeStream);

        public static Stream AsStreamForWrite(this IOutputStream windowsRuntimeStream, int bufferSize) => Unwrap(windowsRuntimeStream);

        public static Stream AsStream(this IRandomAccessStream windowsRuntimeStream) => Unwrap(windowsRuntimeStream);

        public static IInputStream AsInputStream(this Stream stream) => new RandomAccessStream(stream, ownsStream: false);

        public static IOutputStream AsOutputStream(this Stream stream) => new RandomAccessStream(stream, ownsStream: false);

        public static IRandomAccessStream AsRandomAccessStream(this Stream stream) => new RandomAccessStream(stream, ownsStream: false);

        private static Stream Unwrap(object stream) => stream is RandomAccessStream r
            ? r.Inner
            : throw new NotSupportedException("Only WPR's own WinRT streams can be viewed as a .NET stream.");
    }

    /// <summary>Shim for <c>System.IO.WindowsRuntimeStorageExtensions</c>.</summary>
    public static class WindowsRuntimeStorageExtensions
    {
        public static Task<Stream> OpenStreamForReadAsync(this IStorageFile windowsRuntimeFile) => Open(windowsRuntimeFile.Path, FileMode.Open, FileAccess.Read);

        public static Task<Stream> OpenStreamForWriteAsync(this IStorageFile windowsRuntimeFile) => Open(windowsRuntimeFile.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite);

        public static Task<Stream> OpenStreamForReadAsync(this IStorageFolder rootDirectory, string relativePath) =>
            Open(Path.Combine(rootDirectory.Path, relativePath.Replace('\\', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Read);

        public static Task<Stream> OpenStreamForWriteAsync(this IStorageFolder rootDirectory, string relativePath, CreationCollisionOption creationCollisionOption)
        {
            string path = Path.Combine(rootDirectory.Path, relativePath.Replace('\\', Path.DirectorySeparatorChar));
            FileMode mode = creationCollisionOption switch
            {
                CreationCollisionOption.ReplaceExisting => FileMode.Create,
                CreationCollisionOption.FailIfExists => FileMode.CreateNew,
                _ => FileMode.OpenOrCreate,
            };
            return Open(path, mode, FileAccess.ReadWrite);
        }

        private static Task<Stream> Open(string path, FileMode mode, FileAccess access)
        {
            try
            {
                if (access != FileAccess.Read)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                }

                return Task.FromResult<Stream>(new FileStream(path, mode, access, FileShare.ReadWrite | FileShare.Delete));
            }
            catch (Exception ex)
            {
                return Task.FromException<Stream>(ex);
            }
        }
    }
}
