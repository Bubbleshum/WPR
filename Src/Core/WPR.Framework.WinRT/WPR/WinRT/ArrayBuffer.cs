using System;
using Windows.Storage.Streams;

namespace WPR.WinRT
{
    /// <summary>
    /// The byte array behind every <see cref="IBuffer"/> WPR hands out:
    /// <see cref="System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBuffer"/> (what
    /// <c>AsBuffer</c> and the stream reads return) and <see cref="Windows.Storage.Streams.Buffer"/>
    /// (what a game constructs itself). The stream and extension methods read and write
    /// <see cref="Data"/> directly, so both kinds work wherever an <see cref="IBuffer"/> goes.
    /// </summary>
    /// <remarks>Public only because the two public buffer classes derive from it; its constructor is internal.</remarks>
    public abstract class ArrayBuffer : IBuffer
    {
        private uint _length;

        internal ArrayBuffer(byte[] data, uint length)
        {
            Data = data;
            _length = length;
        }

        internal byte[] Data { get; }

        public uint Capacity => (uint)Data.Length;

        public uint Length => _length;

        /// <summary>WinRT names the setter put_Length, which is what game IL calls.</summary>
        public void put_Length(uint value)
        {
            if (value > Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _length = value;
        }

        /// <summary>The array behind <paramref name="buffer"/>; a buffer WPR did not make (a game's own IBuffer) is refused.</summary>
        internal static ArrayBuffer Of(IBuffer buffer) =>
            buffer as ArrayBuffer
            ?? throw new NotSupportedException($"{buffer?.GetType().FullName ?? "null"} is not a buffer WPR made; only WPR's IBuffer implementations can be read or written");
    }
}
