using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Allocates and reads WinRT strings inside emulated memory.
    /// </summary>
    /// <remarks>
    /// An HSTRING is an opaque handle, so its representation is ours to choose. A pointer
    /// to <c>[length:UINT32][UTF-16 chars, null terminated]</c> lets
    /// <c>WindowsGetStringRawBuffer</c> hand back an interior pointer without copying,
    /// which is what callers expect of it.
    ///
    /// Both the import stubs and the WinRT objects need to make strings - a property
    /// getter returning a path is doing exactly what <c>WindowsCreateString</c> does - so
    /// this lives on its own rather than inside either of them.
    /// </remarks>
    public sealed class HStringHeap
    {
        private readonly ArmEmulator _emulator;

        /// <summary>
        /// Every handle this heap has issued.
        /// </summary>
        /// <remarks>
        /// The image will pass back things that are not strings at all - a discovery object
        /// standing in for an unimplemented property gets handed over as though it were one.
        /// Reading a length out of that yields its vtable pointer, a nonsense length that
        /// turns the next copy or scan loop into a hang. Anything not issued here is
        /// treated as the empty string instead.
        /// </remarks>
        private readonly HashSet<long> _issued = new();

        private long _empty;

        public HStringHeap(ArmEmulator emulator) => _emulator = emulator;

        /// <summary>Longer than this is a garbage length, not a real one.</summary>
        public const int SaneLengthLimit = 64 * 1024 * 1024;

        public long Create(string text) => Create(Encoding.Unicode.GetBytes(text));

        public long Create(byte[] utf16)
        {
            int lengthInChars = utf16.Length / 2;
            long handle = _emulator.AllocateHeap(4 + utf16.Length + 2);
            if (handle == 0)
            {
                return 0;
            }

            _emulator.WriteUInt32(handle, (uint)lengthInChars);
            if (utf16.Length > 0)
            {
                _emulator.WriteMemory(handle + 4, utf16);
            }

            _emulator.WriteMemory(handle + 4 + utf16.Length, [0, 0]);
            _issued.Add(handle);
            return handle;
        }

        public bool IsKnown(long handle) => _issued.Contains(handle);

        public byte[] Read(long handle)
        {
            if (!_issued.Contains(handle))
            {
                return [];
            }

            int lengthInChars = (int)_emulator.ReadUInt32(handle);
            return lengthInChars is <= 0 or > SaneLengthLimit
                ? []
                : _emulator.ReadMemory(handle + 4, lengthInChars * 2);
        }

        public string ReadText(long handle) => Encoding.Unicode.GetString(Read(handle));

        /// <summary>Number of UTF-16 code units in a handle, zero for anything unrecognised.</summary>
        public uint LengthOf(long handle) => _issued.Contains(handle) ? _emulator.ReadUInt32(handle) : 0;

        /// <summary>
        /// Pointer to the characters of a handle. A null or unrecognised handle still owes
        /// the caller a readable, null-terminated buffer, so one is kept for the purpose.
        /// </summary>
        public long BufferOf(long handle)
        {
            if (_issued.Contains(handle))
            {
                return handle + 4;
            }

            if (_empty == 0)
            {
                _empty = Create([]);
            }

            return _empty + 4;
        }
    }
}
