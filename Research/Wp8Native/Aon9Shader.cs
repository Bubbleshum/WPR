using System.Buffers.Binary;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>One run of constant-buffer registers copied into Direct3D 9 constant registers.</summary>
    /// <param name="Buffer">Constant buffer slot.</param>
    /// <param name="Start">First 16-byte register within that buffer.</param>
    /// <param name="Count">Registers copied.</param>
    /// <param name="Target">First Direct3D 9 constant register (cN) they land in.</param>
    public readonly record struct Aon9ConstantMap(int Buffer, int Start, int Count, int Target);

    /// <summary>A Direct3D 9 sampler register fed by a Direct3D 11 sampler and shader resource slot.</summary>
    public readonly record struct Aon9SamplerMap(int Register, int Sampler, int Texture);

    /// <summary>
    /// A WP8 shader as the host GPU can run it: the Direct3D 9 bytecode every feature-level 9_x
    /// shader carries beside its DXBC, with a constant table injected so an effect can feed it.
    /// </summary>
    /// <remarks>
    /// <para>A shader compiled for 4_0_level_9_x keeps an "Aon9" chunk: the equivalent vs_2_0/ps_2_x
    /// program plus tables saying which constant-buffer registers feed which cN registers, which
    /// sampler feeds which sN, and which registers the runtime fills itself. FNA3D's MojoShader
    /// already compiles Direct3D 9 bytecode for every driver, so this is the whole route from the
    /// game's shaders to the GPU - no shader translator. Every Modern Combat 4 shader (213) and every
    /// GTA San Andreas one (517) has the chunk.</para>
    /// <para>The bytecode has no CTAB, and MojoShader binds effect parameters to registers by the
    /// CTAB's names, so one is written in: a float4 array <c>vs_c</c>/<c>ps_c</c> covering the
    /// mapped registers, and <c>s0</c>, <c>s1</c>... for the samplers.</para>
    /// <para>Chunk layout: [0] size, [4] Direct3D 9 version token, [8] bytecode size, [12] bytecode
    /// offset, then five (count, offset) uint16 pairs - constant-buffer maps (8 bytes each + 4
    /// padding), loop registers, unused, samplers (4 bytes), runtime constants (4 bytes). A vertex
    /// shader's c0 is a runtime constant, the position fix-up, which is answered with zeros here
    /// (the host's rasteriser needs no half-pixel correction).</para>
    /// </remarks>
    public sealed class Aon9Shader
    {
        public required bool IsPixel { get; init; }

        /// <summary>Direct3D 9 bytecode with the injected constant table.</summary>
        public required byte[] Bytecode { get; init; }

        /// <summary>Float4 registers <c>vs_c</c>/<c>ps_c</c> spans (c0 upwards); zero when none.</summary>
        public required int ConstantRegisters { get; init; }

        public required IReadOnlyList<Aon9ConstantMap> ConstantMaps { get; init; }

        public required IReadOnlyList<Aon9SamplerMap> Samplers { get; init; }

        /// <summary>
        /// For a vertex shader: input semantic (name, index) to input register. Register N is
        /// declared as TEXCOORDN in the Direct3D 9 program, which is how a vertex element reaches it.
        /// </summary>
        public required IReadOnlyDictionary<(string Semantic, int Index), int> InputRegisters { get; init; }

        /// <summary>The effect parameter holding this shader's constants.</summary>
        public string ConstantName => IsPixel ? "ps_c" : "vs_c";

        public static Aon9Shader? Parse(byte[] dxbc, out string? error)
        {
            error = null;
            try
            {
                Dictionary<string, ReadOnlyMemory<byte>> chunks = Chunks(dxbc);
                if (!chunks.TryGetValue("Aon9", out ReadOnlyMemory<byte> aon9Memory))
                {
                    error = "no Aon9 chunk (not a feature-level 9 shader)";
                    return null;
                }

                byte[] aon9 = aon9Memory.ToArray();
                uint version = U32(aon9, 4);
                int codeSize = (int)U32(aon9, 8);
                int codeOffset = (int)U32(aon9, 12);
                bool isPixel = (version >> 16) == 0xFFFF;

                (int Count, int Offset) Pair(int index) => (U16(aon9, 16 + (index * 4)), U16(aon9, 18 + (index * 4)));

                List<Aon9ConstantMap> maps = [];
                (int cbCount, int cbOffset) = Pair(0);
                for (int i = 0; i < cbCount; i++)
                {
                    int at = cbOffset + (i * 12);
                    maps.Add(new Aon9ConstantMap(U16(aon9, at), U16(aon9, at + 2), U16(aon9, at + 4), U16(aon9, at + 6)));
                }

                List<Aon9SamplerMap> samplers = [];
                (int sCount, int sOffset) = Pair(3);
                for (int i = 0; i < sCount; i++)
                {
                    int at = sOffset + (i * 4);
                    samplers.Add(new Aon9SamplerMap(aon9[at], aon9[at + 1], aon9[at + 2]));
                }

                int registers = maps.Count == 0 ? 0 : maps.Max(m => m.Target + m.Count);
                (int rtCount, int rtOffset) = Pair(4);
                for (int i = 0; i < rtCount; i++)
                {
                    registers = Math.Max(registers, U16(aon9, rtOffset + (i * 4)) + 1);
                }

                byte[] code = aon9.AsSpan(codeOffset, codeSize).ToArray();
                Dictionary<(string, int), int> inputs = chunks.TryGetValue("ISGN", out ReadOnlyMemory<byte> isgn)
                    ? InputSignature(isgn.Span)
                    : [];

                return new Aon9Shader
                {
                    IsPixel = isPixel,
                    Bytecode = WithConstantTable(code, isPixel, registers, samplers),
                    ConstantRegisters = registers,
                    ConstantMaps = maps,
                    Samplers = samplers,
                    InputRegisters = inputs,
                };
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

        private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);

        /// <summary>DXBC: "DXBC", 16-byte hash, version, total size, chunk count, chunk offsets.</summary>
        private static Dictionary<string, ReadOnlyMemory<byte>> Chunks(byte[] dxbc)
        {
            if (dxbc.Length < 32 || Encoding.ASCII.GetString(dxbc, 0, 4) != "DXBC")
            {
                throw new InvalidDataException("not a DXBC container");
            }

            Dictionary<string, ReadOnlyMemory<byte>> chunks = new(StringComparer.Ordinal);
            int count = (int)U32(dxbc, 28);
            for (int i = 0; i < count; i++)
            {
                int offset = (int)U32(dxbc, 32 + (i * 4));
                string fourcc = Encoding.ASCII.GetString(dxbc, offset, 4);
                int size = (int)U32(dxbc, offset + 4);
                chunks[fourcc] = new ReadOnlyMemory<byte>(dxbc, offset + 8, size);
            }

            return chunks;
        }

        /// <summary>ISGN: count, 8, then 24-byte elements { name offset, semantic index, system value, type, register, mask... }.</summary>
        private static Dictionary<(string, int), int> InputSignature(ReadOnlySpan<byte> isgn)
        {
            Dictionary<(string, int), int> inputs = [];
            int count = (int)U32(isgn, 0);
            for (int i = 0; i < count; i++)
            {
                int at = 8 + (i * 24);
                int nameOffset = (int)U32(isgn, at);
                int end = isgn[nameOffset..].IndexOf((byte)0);
                string name = Encoding.ASCII.GetString(isgn.Slice(nameOffset, end)).ToUpperInvariant();
                inputs[(name, (int)U32(isgn, at + 4))] = (int)U32(isgn, at + 16);
            }

            return inputs;
        }

        /// <summary>
        /// Inserts a CTAB comment after the version token: <c>vs_c</c>/<c>ps_c</c> as float4[registers]
        /// at c0, and a sampler2D <c>sN</c> per sampler register.
        /// </summary>
        private static byte[] WithConstantTable(byte[] code, bool isPixel, int registers, IReadOnlyList<Aon9SamplerMap> samplers)
        {
            uint version = U32(code, 0);
            List<(string Name, int Set, int Index, int Count, bool Sampler, int Elements)> constants = [];
            if (registers > 0)
            {
                constants.Add((isPixel ? "ps_c" : "vs_c", 2, 0, registers, false, registers));
            }

            foreach (Aon9SamplerMap sampler in samplers.DistinctBy(s => s.Register))
            {
                constants.Add(($"s{sampler.Register}", 3, sampler.Register, 1, true, 1));
            }

            // Header (28) | constant infos (20 each) | type infos (16 each) | strings.
            const int HeaderSize = 28;
            int infoOffset = HeaderSize;
            int typeOffset = infoOffset + (constants.Count * 20);
            int stringOffset = typeOffset + (constants.Count * 16);
            List<byte> strings = [];
            int AddString(string text)
            {
                int offset = stringOffset + strings.Count;
                strings.AddRange(Encoding.ASCII.GetBytes(text));
                strings.Add(0);
                return offset;
            }

            int creator = AddString("WPR");
            int target = AddString(isPixel ? "ps_2_0" : "vs_2_0");
            int[] names = constants.Select(c => AddString(c.Name)).ToArray();
            while (strings.Count % 4 != 0)
            {
                strings.Add(0);
            }

            byte[] table = new byte[stringOffset + strings.Count];
            Span<byte> t = table;
            BinaryPrimitives.WriteUInt32LittleEndian(t[0..], HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(t[4..], (uint)creator);
            BinaryPrimitives.WriteUInt32LittleEndian(t[8..], version);
            BinaryPrimitives.WriteUInt32LittleEndian(t[12..], (uint)constants.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(t[16..], (uint)infoOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(t[20..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(t[24..], (uint)target);
            for (int i = 0; i < constants.Count; i++)
            {
                var c = constants[i];
                Span<byte> info = t[(infoOffset + (i * 20))..];
                BinaryPrimitives.WriteUInt32LittleEndian(info[0..], (uint)names[i]);
                BinaryPrimitives.WriteUInt16LittleEndian(info[4..], (ushort)c.Set);
                BinaryPrimitives.WriteUInt16LittleEndian(info[6..], (ushort)c.Index);
                BinaryPrimitives.WriteUInt16LittleEndian(info[8..], (ushort)c.Count);
                BinaryPrimitives.WriteUInt32LittleEndian(info[12..], (uint)(typeOffset + (i * 16)));
                BinaryPrimitives.WriteUInt32LittleEndian(info[16..], 0);

                // D3DXSHADER_TYPEINFO: Class, Type, Rows, Columns, Elements, StructMembers, MemberInfo.
                Span<byte> type = t[(typeOffset + (i * 16))..];
                BinaryPrimitives.WriteUInt16LittleEndian(type[0..], (ushort)(c.Sampler ? 4 : 1));   // OBJECT : VECTOR
                BinaryPrimitives.WriteUInt16LittleEndian(type[2..], (ushort)(c.Sampler ? 12 : 3));  // SAMPLER2D : FLOAT
                BinaryPrimitives.WriteUInt16LittleEndian(type[4..], 1);
                BinaryPrimitives.WriteUInt16LittleEndian(type[6..], (ushort)(c.Sampler ? 1 : 4));
                BinaryPrimitives.WriteUInt16LittleEndian(type[8..], (ushort)c.Elements);
            }

            strings.CopyTo(table, stringOffset);

            // Comment token: 0xFFFE, length in dwords (the "CTAB" fourcc plus the table) in bits 16-30.
            int dwords = 1 + (table.Length / 4);
            byte[] result = new byte[code.Length + 4 + (dwords * 4)];
            Array.Copy(code, 0, result, 0, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 0xFFFEu | ((uint)dwords << 16));
            Encoding.ASCII.GetBytes("CTAB").CopyTo(result, 8);
            table.CopyTo(result, 12);
            Array.Copy(code, 4, result, 12 + table.Length, code.Length - 4);
            return result;
        }
    }

    /// <summary>
    /// Writes the smallest effect (fx_2_0) MojoShader accepts around one vertex and one pixel
    /// shader: their constant arrays and samplers as parameters, one technique, one pass whose
    /// only states are the two shaders - so applying it changes nothing else on the device.
    /// </summary>
    /// <remarks>
    /// Layout (mojoshader_effects.c): 0xFEFF0901, offset to the tables (from the byte after it),
    /// then a data area of type/value/string blobs, then param count, technique count, 0, object
    /// count, params, techniques, then small-object count, large-object count and the shaders.
    /// Offset 0 of the data area is a zero dword, which reads as "no string".
    /// </remarks>
    public static class Aon9Effect
    {
        public static byte[] Build(Aon9Shader vertex, Aon9Shader pixel)
        {
            List<byte> data = [0, 0, 0, 0];
            int Blob(params uint[] words)
            {
                int offset = data.Count;
                foreach (uint word in words)
                {
                    data.AddRange(BitConverter.GetBytes(word));
                }

                return offset;
            }

            int Str(string text)
            {
                int offset = data.Count;
                byte[] bytes = Encoding.ASCII.GetBytes(text + "\0");
                data.AddRange(BitConverter.GetBytes((uint)bytes.Length));
                data.AddRange(bytes);
                while (data.Count % 4 != 0)
                {
                    data.Add(0);
                }

                return offset;
            }

            List<(int Type, int Value)> parameters = [];
            foreach (Aon9Shader shader in new[] { vertex, pixel })
            {
                if (shader.ConstantRegisters > 0)
                {
                    int name = Str(shader.ConstantName);
                    // type FLOAT, class VECTOR, name, semantic, elements, columns, rows
                    int type = Blob(3, 1, (uint)name, 0, (uint)shader.ConstantRegisters, 4, 1);
                    int value = Blob(new uint[shader.ConstantRegisters * 4]);
                    parameters.Add((type, value));
                }
            }

            foreach (Aon9SamplerMap sampler in pixel.Samplers.DistinctBy(s => s.Register))
            {
                int name = Str($"s{sampler.Register}");
                parameters.Add((Blob(12, 4, (uint)name, 0, 0), Blob(0)));   // SAMPLER2D, OBJECT, no states
            }

            int technique = Str("T");
            int pass = Str("P");
            int vsType = Blob(16, 4, 0, 0, 0);   // VERTEXSHADER object
            int vsValue = Blob(1);               // object 1
            int psType = Blob(15, 4, 0, 0, 0);   // PIXELSHADER object
            int psValue = Blob(2);               // object 2

            List<byte> tables = [];
            void Word(uint value) => tables.AddRange(BitConverter.GetBytes(value));

            Word((uint)parameters.Count);
            Word(1);
            Word(0);
            Word(3);
            foreach ((int type, int value) in parameters)
            {
                Word((uint)type);
                Word((uint)value);
                Word(0);   // flags
                Word(0);   // annotations
            }

            Word((uint)technique);
            Word(0);
            Word(1);
            Word((uint)pass);
            Word(0);
            Word(2);
            Word(146); Word(0); Word((uint)vsType); Word((uint)vsValue);   // MOJOSHADER_RS_VERTEXSHADER
            Word(147); Word(0); Word((uint)psType); Word((uint)psValue);   // MOJOSHADER_RS_PIXELSHADER

            Word(2);   // small objects
            Word(0);   // large objects
            foreach ((int index, byte[] code) in new[] { (1, vertex.Bytecode), (2, pixel.Bytecode) })
            {
                Word((uint)index);
                Word((uint)code.Length);
                tables.AddRange(code);
                while (tables.Count % 4 != 0)
                {
                    tables.Add(0);
                }
            }

            List<byte> effect = [];
            effect.AddRange(BitConverter.GetBytes(0xFEFF0901u));
            effect.AddRange(BitConverter.GetBytes((uint)data.Count));
            effect.AddRange(data);
            effect.AddRange(tables);
            return [.. effect];
        }
    }
}
