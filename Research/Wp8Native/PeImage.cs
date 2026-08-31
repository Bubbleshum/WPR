using System.Buffers.Binary;

namespace WPR.Wp8Native
{
    /// <summary>
    /// One section header from the PE section table.
    /// </summary>
    public readonly record struct PeSection(
        string Name,
        uint VirtualSize,
        uint VirtualAddress,
        uint RawSize,
        uint RawPointer)
    {
        /// <summary>Bytes the loader reserves for this section once mapped.</summary>
        public uint MappedSize => Math.Max(VirtualSize, RawSize);
    }

    /// <summary>
    /// A single entry in the import address table, and the RVA of the IAT slot that
    /// holds its address. The slot is what gets overwritten with a trap address.
    /// </summary>
    public sealed record ImportedFunction(string Dll, string Name, uint IatSlotRva)
    {
        public string FullName => $"{Dll}!{Name}";
    }

    /// <summary>
    /// Minimal PE32 reader, scoped to what a loader needs: where to map the image,
    /// where to start, and every function the image imports.
    /// </summary>
    /// <remarks>
    /// Written against WP8 "Modern Native" binaries, which are always PE32 /
    /// <see cref="MachineArmNt"/>. It does not handle PE32+, bound imports,
    /// or delay-loaded imports because no WP8 app package uses them.
    /// </remarks>
    public sealed class PeImage
    {
        /// <summary>IMAGE_FILE_MACHINE_ARMNT - ARMv7 in Thumb-2 encoding.</summary>
        public const ushort MachineArmNt = 0x01C4;

        private readonly byte[] _raw;
        private readonly PeSection[] _sections;

        public ushort Machine { get; }
        public uint EntryPointRva { get; }
        public uint ImageBase { get; }
        public uint SizeOfImage { get; }
        public uint SizeOfHeaders { get; }
        public ushort Subsystem { get; }

        /// <summary>True when the image carries a CLR directory, i.e. it is a .NET assembly.</summary>
        public bool IsManaged { get; }

        /// <summary>RVA of the .pdata table of RUNTIME_FUNCTION entries.</summary>
        public uint ExceptionDirectoryRva { get; }

        public uint ExceptionDirectorySize { get; }

        public IReadOnlyList<PeSection> Sections => _sections;
        public IReadOnlyList<ImportedFunction> Imports { get; }

        /// <summary>
        /// The entry point address once mapped. The low bit is the ARM Thumb-state flag,
        /// so this value is passed to the emulator as-is rather than being masked off.
        /// </summary>
        public uint EntryPoint => ImageBase + EntryPointRva;

        public bool EntryIsThumb => (EntryPointRva & 1) != 0;

        private PeImage(byte[] raw)
        {
            _raw = raw;

            int peOffset = (int)U32(0x3C);
            if (U16(peOffset) != 0x4550) // "PE\0\0"
            {
                throw new BadImageFormatException("Not a PE file - missing PE signature.");
            }

            Machine = U16(peOffset + 4);
            ushort sectionCount = U16(peOffset + 6);
            ushort optionalHeaderSize = U16(peOffset + 20);

            int opt = peOffset + 24;
            ushort magic = U16(opt);
            if (magic != 0x10B)
            {
                throw new BadImageFormatException($"Expected PE32 (0x10B), got 0x{magic:X}.");
            }

            EntryPointRva = U32(opt + 16);
            ImageBase     = U32(opt + 28);
            SizeOfImage   = U32(opt + 56);
            SizeOfHeaders = U32(opt + 60);
            Subsystem     = U16(opt + 68);

            int dataDirectories = opt + 96;
            uint importDirRva = U32(dataDirectories + 1 * 8);

            // Directory 3 is the exception directory: on ARM, the .pdata table of
            // RUNTIME_FUNCTION entries that unwinding is driven from.
            ExceptionDirectoryRva = U32(dataDirectories + 3 * 8);
            ExceptionDirectorySize = U32(dataDirectories + 3 * 8 + 4);
            IsManaged = U32(dataDirectories + 14 * 8) != 0;

            _sections = new PeSection[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                int so = peOffset + 24 + optionalHeaderSize + (i * 40);
                _sections[i] = new PeSection(
                    Name: ReadFixedName(so),
                    VirtualSize: U32(so + 8),
                    VirtualAddress: U32(so + 12),
                    RawSize: U32(so + 16),
                    RawPointer: U32(so + 20));
            }

            Imports = importDirRva == 0 ? Array.Empty<ImportedFunction>() : ReadImports(importDirRva);
        }

        public static PeImage Load(string path) => new(File.ReadAllBytes(path));

        /// <summary>Raw file bytes, for copying headers and section data into emulator memory.</summary>
        public ReadOnlySpan<byte> Raw => _raw;

        /// <summary>
        /// Translates a relative virtual address to a file offset, or null when the RVA
        /// falls outside every section (e.g. it lands in uninitialised .bss space).
        /// </summary>
        public int? RvaToOffset(uint rva)
        {
            foreach (PeSection s in _sections)
            {
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.MappedSize)
                {
                    return (int)(s.RawPointer + (rva - s.VirtualAddress));
                }
            }

            return null;
        }

        private ImportedFunction[] ReadImports(uint importDirRva)
        {
            List<ImportedFunction> imports = new();

            int descriptor = RvaToOffset(importDirRva)
                ?? throw new BadImageFormatException("Import directory RVA is not inside any section.");

            while (true)
            {
                uint originalFirstThunk = U32(descriptor);
                uint nameRva            = U32(descriptor + 12);
                uint firstThunk         = U32(descriptor + 16);

                // The descriptor array is terminated by an all-zero entry.
                if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                {
                    break;
                }

                string dll = ReadCString(RvaToOffset(nameRva)!.Value);

                // Prefer the original thunk array: it always holds names, whereas FirstThunk
                // is what the real loader overwrites with resolved addresses.
                uint nameThunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
                int thunk = RvaToOffset(nameThunkRva)!.Value;
                uint slotRva = firstThunk;

                while (true)
                {
                    uint entry = U32(thunk);
                    if (entry == 0)
                    {
                        break;
                    }

                    // High bit set means the function is imported by ordinal, not by name.
                    string name = (entry & 0x80000000) != 0
                        ? $"#{entry & 0xFFFF}"
                        : ReadCString(RvaToOffset(entry)!.Value + 2); // skip the 2-byte hint

                    imports.Add(new ImportedFunction(dll, name, slotRva));

                    thunk += 4;
                    slotRva += 4;
                }

                descriptor += 20;
            }

            return imports.ToArray();
        }

        private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_raw.AsSpan(offset));

        private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(offset));

        private string ReadFixedName(int offset)
        {
            int length = 0;
            while (length < 8 && _raw[offset + length] != 0)
            {
                length++;
            }

            return System.Text.Encoding.ASCII.GetString(_raw, offset, length);
        }

        private string ReadCString(int offset)
        {
            int end = offset;
            while (_raw[end] != 0)
            {
                end++;
            }

            return System.Text.Encoding.ASCII.GetString(_raw, offset, end - offset);
        }
    }
}
