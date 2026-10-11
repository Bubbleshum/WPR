using System.Buffers.Binary;
using System.Text;

namespace Wp8ContractTests;

/// <summary>
/// A tiny ARMNT PE32 writer for loader tests that no installed title can drive: TLS callbacks,
/// a forwarder to a DLL loaded at GetProcAddress time, a DLL with static TLS loaded at runtime.
/// One RWX ".text" section at RVA 0x1000 holds code and data; file offsets equal RVAs.
/// </summary>
/// <remarks>
/// Code is hand-assembled Thumb-2, in the shapes the loader cares about: <see cref="LogFunction"/>
/// appends <c>tag | reason &lt;&lt; 16</c> to a log page at a fixed address (so every module writes
/// to one log, in call order), and <see cref="TlsReader"/> / <see cref="TlsWriter"/> reach the
/// module's TLS block exactly as MSVC compiles <c>__declspec(thread)</c>: TPIDRURW, TEB+0x2C, the
/// slot at <c>_tls_index</c>.
/// </remarks>
public sealed class SyntheticPe
{
    public const long LogPage = 0x30000000;
    private const uint TextRva = 0x1000;

    private readonly List<byte> _text = new();
    private readonly List<uint> _relocations = new();
    private readonly List<(string Name, uint Rva, string? Forwarder)> _exports = new();
    private readonly List<(string Dll, string Name)> _imports = new();

    public SyntheticPe(uint imageBase, bool dll)
    {
        ImageBase = imageBase;
        IsDll = dll;
    }

    public uint ImageBase { get; }

    public bool IsDll { get; }

    public uint EntryRva { get; set; }

    /// <summary>The RVA of the next byte.</summary>
    public uint Here => TextRva + (uint)_text.Count;

    public uint Align(int alignment)
    {
        while (_text.Count % alignment != 0)
        {
            _text.Add(0);
        }

        return Here;
    }

    public uint Bytes(params byte[] bytes)
    {
        uint at = Here;
        _text.AddRange(bytes);
        return at;
    }

    public uint Word(uint value) => Bytes(BitConverter.GetBytes(value));

    /// <summary>A word holding the VA of <paramref name="rva"/>, with a HIGHLOW base relocation.</summary>
    public uint Va(uint rva)
    {
        _relocations.Add(Here);
        return Word(ImageBase + rva);
    }

    private uint Halfwords(params ushort[] code)
    {
        uint at = Here;
        foreach (ushort h in code)
        {
            _text.AddRange(BitConverter.GetBytes(h));
        }

        return at;
    }

    /// <summary>f(hinst, reason, reserved): log[++count] = tag | reason &lt;&lt; 16; return TRUE. Thumb address RVA.</summary>
    public uint LogFunction(ushort tag)
    {
        uint at = Align(4);
        Halfwords(
            0x4B05,   // ldr  r3, [pc, #20]   -> log page
            0x681A,   // ldr  r2, [r3]
            0x3201,   // adds r2, #1
            0x601A,   // str  r2, [r3]
            0x0092,   // lsls r2, r2, #2
            0x4804,   // ldr  r0, [pc, #16]   -> tag
            0x0409,   // lsls r1, r1, #16
            0x4308,   // orrs r0, r1
            0x5098,   // str  r0, [r3, r2]
            0x2001,   // movs r0, #1
            0x4770,   // bx   lr
            0xBF00);  // nop
        Word((uint)LogPage);
        Word(tag);
        return at + 1;
    }

    /// <summary>u32 f(void): word 0 of this module's TLS block on the calling thread.</summary>
    public uint TlsReader(uint tlsIndexRva)
    {
        uint at = Align(4);
        Halfwords(
            0xEE1D, 0x0F50,   // mrc  p15, 0, r0, c13, c0, 2   (TEB)
            0x6AC0,           // ldr  r0, [r0, #0x2C]          (ThreadLocalStoragePointer)
            0x4903,           // ldr  r1, [pc, #12]            -> &_tls_index
            0x6809,           // ldr  r1, [r1]
            0x0089,           // lsls r1, r1, #2
            0x5840,           // ldr  r0, [r0, r1]
            0x6800,           // ldr  r0, [r0]
            0x4770,           // bx   lr
            0xBF00);
        Va(tlsIndexRva);
        return at + 1;
    }

    /// <summary>u32 f(u32 value): stores value in word 0 of this module's TLS block, returns it.</summary>
    public uint TlsWriter(uint tlsIndexRva)
    {
        uint at = Align(4);
        Halfwords(
            0xEE1D, 0x3F50,   // mrc  p15, 0, r3, c13, c0, 2
            0x6ADB,           // ldr  r3, [r3, #0x2C]
            0x4903,           // ldr  r1, [pc, #12]
            0x6809,           // ldr  r1, [r1]
            0x0089,           // lsls r1, r1, #2
            0x585B,           // ldr  r3, [r3, r1]
            0x6018,           // str  r0, [r3]
            0x4770,           // bx   lr
            0xBF00);
        Va(tlsIndexRva);
        return at + 1;
    }

    /// <summary>bx lr.</summary>
    public uint Return()
    {
        uint at = Align(4);
        Halfwords(0x4770, 0xBF00);
        return at + 1;
    }

    public void Export(string name, uint rva) => _exports.Add((name, rva, null));

    public void ExportForwarder(string name, string target) => _exports.Add((name, 0, target));

    public void Import(string dll, string name) => _imports.Add((dll, name));

    /// <summary>
    /// The TLS directory: a slot for _tls_index, the template, the callback array. Returns the
    /// _tls_index RVA; call before writing the code that reads it.
    /// </summary>
    public uint TlsIndexSlot()
    {
        Align(4);
        return Word(0xDEADBEEF);
    }

    private (uint Index, byte[] Template, uint ZeroFill, uint[] Callbacks)? _tls;

    public void Tls(uint indexRva, byte[] template, uint zeroFill, params uint[] callbacks) => _tls = (indexRva, template, zeroFill, callbacks);

    public byte[] Build()
    {
        uint tlsDirectory = 0;
        if (_tls is { } tls)
        {
            uint start = Align(4);
            Bytes(tls.Template);
            uint end = Here;
            uint callbacks = Align(4);
            foreach (uint callback in tls.Callbacks)
            {
                Va(callback);
            }

            Word(0);
            tlsDirectory = Align(4);
            Va(start);
            Va(end);
            Va(tls.Index);
            if (tls.Callbacks.Length > 0)
            {
                Va(callbacks);
            }
            else
            {
                Word(0);
            }

            Word(tls.ZeroFill);
            Word(0);
        }

        (uint exportRva, uint exportSize) = BuildExports();
        uint importRva = BuildImports();

        // Base relocations: one block per page, HIGHLOW entries, padded to a word.
        uint relocRva = Align(4);
        foreach (IGrouping<uint, uint> page in _relocations.GroupBy(r => r & ~0xFFFu))
        {
            List<ushort> entries = page.Select(r => (ushort)(0x3000 | (r & 0xFFF))).ToList();
            if (entries.Count % 2 == 1)
            {
                entries.Add(0);
            }

            Word(page.Key);
            Word((uint)(8 + (entries.Count * 2)));
            foreach (ushort entry in entries)
            {
                Bytes(BitConverter.GetBytes(entry));
            }
        }

        uint relocSize = Here - relocRva;
        uint textSize = (uint)_text.Count;
        uint textMapped = (textSize + 0xFFF) & ~0xFFFu;

        byte[] file = new byte[TextRva + textMapped];
        Span<byte> f = file;
        f[0] = (byte)'M';
        f[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(f[0x3C..], 0x40);
        int pe = 0x40;
        "PE\0\0"u8.CopyTo(f[pe..]);
        BinaryPrimitives.WriteUInt16LittleEndian(f[(pe + 4)..], 0x01C4);
        BinaryPrimitives.WriteUInt16LittleEndian(f[(pe + 6)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(f[(pe + 20)..], 224);
        BinaryPrimitives.WriteUInt16LittleEndian(f[(pe + 22)..], (ushort)(0x0102 | (IsDll ? 0x2000 : 0)));
        int opt = pe + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(f[opt..], 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(opt + 16)..], EntryRva);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(opt + 28)..], ImageBase);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(opt + 32)..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(opt + 36)..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(opt + 56)..], TextRva + textMapped);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(opt + 60)..], 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(f[(opt + 68)..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(opt + 92)..], 16);
        int dd = opt + 96;
        void Directory(int index, uint rva, uint size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(dd + (index * 8)), rva);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(dd + (index * 8) + 4), size);
        }

        Directory(0, exportRva, exportSize);
        Directory(1, importRva, importRva == 0 ? 0 : 20u);
        Directory(5, relocSize == 0 ? 0 : relocRva, relocSize);
        Directory(9, tlsDirectory, tlsDirectory == 0 ? 0 : 24u);

        int section = opt + 224;
        ".text"u8.CopyTo(f[section..]);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(section + 8)..], textSize);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(section + 12)..], TextRva);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(section + 16)..], textMapped);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(section + 20)..], TextRva);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(section + 36)..], 0xE0000020);

        _text.ToArray().CopyTo(file, (int)TextRva);
        return file;
    }

    private (uint Rva, uint Size) BuildExports()
    {
        if (_exports.Count == 0)
        {
            return (0, 0);
        }

        List<(string Name, uint Rva, string? Forwarder)> sorted = _exports.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        uint directory = Align(4);
        byte[] header = new byte[40];
        uint headerAt = Bytes(header);
        uint functions = Here;
        foreach (var _ in sorted)
        {
            Word(0);
        }

        uint names = Here;
        foreach (var _ in sorted)
        {
            Word(0);
        }

        uint ordinals = Here;
        for (int i = 0; i < sorted.Count; i++)
        {
            Bytes(BitConverter.GetBytes((ushort)i));
        }

        for (int i = 0; i < sorted.Count; i++)
        {
            uint nameAt = Bytes([.. Encoding.ASCII.GetBytes(sorted[i].Name), 0]);
            Patch(names + (uint)(i * 4), nameAt);
            uint target = sorted[i].Forwarder is { } forwarder ? Bytes([.. Encoding.ASCII.GetBytes(forwarder), 0]) : sorted[i].Rva;
            Patch(functions + (uint)(i * 4), target);
        }

        uint size = Here - directory;
        Patch(headerAt + 16, 1);
        Patch(headerAt + 20, (uint)sorted.Count);
        Patch(headerAt + 24, (uint)sorted.Count);
        Patch(headerAt + 28, functions);
        Patch(headerAt + 32, names);
        Patch(headerAt + 36, ordinals);
        return (directory, size);
    }

    private uint BuildImports()
    {
        if (_imports.Count == 0)
        {
            return 0;
        }

        List<IGrouping<string, (string Dll, string Name)>> byDll = _imports.GroupBy(i => i.Dll).ToList();
        uint descriptors = Align(4);
        Bytes(new byte[20 * (byDll.Count + 1)]);
        for (int d = 0; d < byDll.Count; d++)
        {
            List<uint> hints = new();
            foreach ((string _, string function) in byDll[d])
            {
                Align(2);
                hints.Add(Bytes([0, 0, .. Encoding.ASCII.GetBytes(function), 0]));
            }

            uint lookup = Align(4);
            foreach (uint hint in hints)
            {
                Word(hint);
            }

            Word(0);
            uint iat = Here;
            foreach (uint hint in hints)
            {
                Word(hint);
            }

            Word(0);
            uint name = Bytes([.. Encoding.ASCII.GetBytes(byDll[d].Key), 0]);
            uint at = descriptors + (uint)(d * 20);
            Patch(at, lookup);
            Patch(at + 12, name);
            Patch(at + 16, iat);
        }

        return descriptors;
    }

    private void Patch(uint rva, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++)
        {
            _text[(int)(rva - TextRva) + i] = bytes[i];
        }
    }
}
