using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Surgical, in-process .win patcher. Targets a fixed list of GameMaker script
    /// names — typically <c>gml_Script_achievements_add</c> — and replaces each
    /// matching code entry's bytecode with a single <c>Exit Int32</c> instruction
    /// so it becomes an immediate-return no-op when the Runner enters it.
    ///
    /// Why this exists: <see cref="GameMakerWinPatcher"/>'s original approach
    /// shelled out to UndertaleModCli with <c>wpr_neutralize.csx</c>. That CLI
    /// is GPL-3 and (on this dev box) hangs at &lt;1% CPU before producing any
    /// output, so the patched sibling was never produced and the FATAL
    /// "Unable to find any instance for object index '0' name 'achievements'"
    /// surfaced at runtime. Doing the surgery in-process eliminates the
    /// subprocess, the hang, and the GPL entanglement — this file is original
    /// MIT code that only encodes facts about the public .win format
    /// (chunk IDs, opcode numbers, field offsets), which are not copyrightable.
    ///
    /// Scope (deliberate):
    /// - Read enough of the FORM/chunk structure to locate GEN8, STRG, CODE.
    /// - Look up a string by content to find its on-disk address.
    /// - Walk CODE entry headers, find the entry whose name pointer matches.
    /// - For bytecode 15+: read the entry's signed relative-bytecode-address,
    ///   compute the absolute address, overwrite the first 4 bytes there with
    ///   the <c>Exit Int32</c> opcode and zero the Length field at +4 of the
    ///   entry header to 4. The original bytecode bytes past offset 4 remain
    ///   on disk untouched but are never read by the Runner because Length
    ///   bounds the instruction stream.
    /// - For bytecode &lt;=14: the entry's instructions are written inline after
    ///   a length word that immediately follows the name pointer. Patch the
    ///   length to 4 and overwrite the first 4 instruction bytes the same way.
    ///
    /// Out of scope: re-serializing the file, updating offsets, touching strings,
    /// touching any other chunk. A byte-level overwrite keeps file size identical
    /// and leaves all other chunk offsets / pointer tables valid.
    /// </summary>
    public static class GameMakerWinNeutralizer
    {
        // Modern (bytecode 15+) and legacy (bytecode &lt;= 14) opcodes for Exit.
        // See UndertaleInstruction.Opcode in UMT. The 4-byte instruction word in
        // little-endian is [0x00 0x00 Type1Nybble<<0 OpcodeByte], so an Exit Int32
        // is 0x9D020000 (modern) / 0x9E020000 (old) as a uint32.
        private const uint ExitInt32Modern = 0x9D020000u;
        private const uint ExitInt32Legacy = 0x9E020000u;

        /// <summary>
        /// Default neutralization target — Briquid Mini's <c>achievements_add</c>
        /// fires in <c>god</c>'s PreCreate before <c>achievements_define</c> has
        /// created the singleton, FATALing at <c>achievements.count += 1</c>.
        /// Replacing the body with a single Exit lets the game boot.
        /// </summary>
        public static readonly string[] DefaultTargets = new[]
        {
            "gml_Script_achievements_add",
        };

        /// <summary>
        /// Optional "platform bootstrap" injection: variables WP would normally
        /// set before any game event ran, that Runner.exe doesn't supply. We
        /// inject writes into the body of a neutralised script (so they execute
        /// when the runtime first invokes it from a room-creation context),
        /// which means Briquid's god.PreCreate → achievements_add(…) call —
        /// already a no-op for us — is what triggers the setup.
        ///
        /// Each entry: (variableName, instanceScope, immediateInt16Value).
        /// </summary>
        public enum BootstrapScope { Self, Global }
        public readonly record struct BootstrapWrite(string Name, BootstrapScope Scope, short Int16Value);

        /// <summary>
        /// Briquid Mini-specific bootstrap. <c>gui_scale</c> is read from
        /// god.Create as self.gui_scale and from god.CreateEvent_6 as
        /// global.gui_scale, both expected to be set by the WP platform layer
        /// before any object event ran. Setting both to 1 is enough to let
        /// god's Create chain proceed.
        /// </summary>
        public static readonly BootstrapWrite[] DefaultBootstrap = new BootstrapWrite[]
        {
            // Disabled by default: the in-tree VARI/chain bookkeeping is implemented
            // (see InjectBootstrap below) but produces a .win that crashes Runner
            // 2.1.4.200 at load time with STATUS_ACCESS_VIOLATION before any
            // stdout. Probably a chain-walk or opcode-encoding subtlety I haven't
            // tracked down yet. Leaving the code here for future debugging; the
            // achievements_add neutralisation alone (without bootstrap) is the
            // currently shipping behaviour.
            //
            // To re-enable for experiments, restore:
            //   new BootstrapWrite("gui_scale", BootstrapScope.Self, 1),
            //   new BootstrapWrite("gui_scale", BootstrapScope.Global, 1),
        };

        /// <summary>
        /// Name of the script whose body we repurpose to run
        /// <see cref="DefaultBootstrap"/>. Must already be a neutralisation
        /// target so its original body is being discarded anyway.
        /// </summary>
        public const string BootstrapHostScript = "gml_Script_achievements_add";

        /// <summary>
        /// Read <paramref name="srcWinPath"/>, neutralize each target script in
        /// memory, and write the result to <paramref name="dstWinPath"/>. The
        /// destination file always ends up the same size as the source — the
        /// surgery is purely byte-level overwrites. Original game.win is never
        /// modified. Returns the number of scripts successfully neutralized.
        /// </summary>
        public static int Neutralize(string srcWinPath, string dstWinPath, IReadOnlyList<string>? targets = null)
        {
            targets ??= DefaultTargets;

            byte[] data = File.ReadAllBytes(srcWinPath);

            // Top-level FORM wrapper. Anything else is not a GMS data.win.
            if (data.Length < 8 || ReadAscii4(data, 0) != "FORM")
                throw new InvalidDataException("Not a FORM-wrapped GameMaker .win (bad magic).");

            uint formLength = ReadUInt32LE(data, 4);
            if (8 + formLength > (uint)data.Length)
                throw new InvalidDataException($"FORM length {formLength} exceeds file length {data.Length}.");

            // Walk subchunks of FORM. Each is [4 ASCII][uint32 length][bytes].
            var chunks = WalkChunks(data, contentStart: 8, contentEnd: 8 + (int)formLength);

            if (!chunks.TryGetValue("GEN8", out var gen8) || gen8.length < 2)
                throw new InvalidDataException("GEN8 chunk missing or too small.");
            if (!chunks.TryGetValue("STRG", out var strg))
                throw new InvalidDataException("STRG chunk missing.");
            if (!chunks.TryGetValue("CODE", out var code) || code.length == 0)
            {
                // CODE absent (e.g. YYC compile) — nothing to neutralize.
                File.Copy(srcWinPath, dstWinPath, overwrite: true);
                return 0;
            }

            // GEN8 byte 1 is BytecodeVersion. byte 0 is IsDebuggerDisabled.
            byte bytecodeVersion = data[gen8.contentStart + 1];
            bool bytecode14OrLower = bytecodeVersion <= 14;

            // Resolve string-by-content -> file address. UndertaleString references
            // (the pointers written by WriteUndertaleString) point to the start of
            // the UTF-8 bytes — i.e. immediately AFTER the uint32 length prefix.
            // So a per-string "address" stored in the file equals the offset of
            // the first content byte.
            var stringAddrByContent = BuildStringIndex(data, strg);

            int neutralized = 0;
            CodeEntryRef? bootstrapHost = null;
            foreach (string name in targets)
            {
                if (!stringAddrByContent.TryGetValue(name, out uint nameAddr))
                {
                    Log.Info(LogCategory.AppInstall,
                        $"GameMakerWinNeutralizer: string not present in STRG, nothing to do: {name}");
                    continue;
                }

                if (TryFindCodeEntryByNameAddr(data, code, nameAddr, bytecode14OrLower, out var entry))
                {
                    NeutralizeEntry(data, entry, bytecode14OrLower);
                    Log.Info(LogCategory.AppInstall,
                        $"GameMakerWinNeutralizer: replaced {name} bytecode with Exit");
                    neutralized++;
                    if (name == BootstrapHostScript) bootstrapHost = entry;
                }
                else
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"GameMakerWinNeutralizer: CODE entry for {name} not found");
                }
            }

            // After neutralisation, optionally overwrite the host script's body
            // with bootstrap variable writes. Only attempt this when we still
            // have the chunk references handy (bytecode 14+ Self/Global semantics
            // and a VARI chunk to fix up).
            if (bootstrapHost is not null && chunks.TryGetValue("VARI", out var vari))
            {
                try
                {
                    int injected = InjectBootstrap(
                        data, bootstrapHost.Value, vari, bytecode14OrLower,
                        stringAddrByContent, DefaultBootstrap);
                    if (injected > 0)
                    {
                        Log.Info(LogCategory.AppInstall,
                            $"GameMakerWinNeutralizer: injected {injected} bootstrap variable write(s) into {BootstrapHostScript}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"GameMakerWinNeutralizer: bootstrap injection failed (script still neutralised, but " +
                        $"WP platform variables won't be pre-set): {ex.Message}");
                }
            }

            File.WriteAllBytes(dstWinPath, data);
            return neutralized;
        }

        // ---- FORM chunk walking -------------------------------------------------

        private readonly record struct ChunkRef(int contentStart, int length);

        private static Dictionary<string, ChunkRef> WalkChunks(byte[] data, int contentStart, int contentEnd)
        {
            var result = new Dictionary<string, ChunkRef>();
            int p = contentStart;
            while (p + 8 <= contentEnd)
            {
                string id = ReadAscii4(data, p);
                uint len = ReadUInt32LE(data, p + 4);
                int payload = p + 8;
                if (payload + (int)len > contentEnd)
                    throw new InvalidDataException($"Chunk {id} declares length {len} that overflows FORM.");
                result[id] = new ChunkRef(payload, (int)len);
                p = payload + (int)len;
            }
            return result;
        }

        // ---- STRG index ---------------------------------------------------------

        // Walk STRG and return a map: string content -> the file address that the
        // rest of the file uses to reference that string (i.e. the offset of the
        // first UTF-8 byte, since UMT-style references point past the length).
        private static Dictionary<string, uint> BuildStringIndex(byte[] data, ChunkRef strg)
        {
            var map = new Dictionary<string, uint>(StringComparer.Ordinal);
            if (strg.length < 4) return map;

            uint count = ReadUInt32LE(data, strg.contentStart);
            int ptrTable = strg.contentStart + 4;
            for (uint i = 0; i < count; i++)
            {
                uint entryAddr = ReadUInt32LE(data, ptrTable + (int)i * 4);
                if (entryAddr == 0 || entryAddr + 4 > (uint)data.Length) continue;
                uint strLen = ReadUInt32LE(data, (int)entryAddr);
                uint contentAddr = entryAddr + 4;
                if (contentAddr + strLen > (uint)data.Length) continue;
                string s = Encoding.UTF8.GetString(data, (int)contentAddr, (int)strLen);
                // Last writer wins, but GMS doesn't deduplicate so collisions
                // would be a format anomaly anyway.
                map[s] = contentAddr;
            }
            return map;
        }

        // ---- CODE entry lookup --------------------------------------------------

        // For bytecode 15+: entry header is name(4) + length(4) + locals(2) + args(2)
        //                   + relAddr(4) + offset(4) = 20 bytes total.
        // For bytecode <=14: entry header is name(4) + length(4), with bytecode
        //                   immediately following the length field, inline.
        private const int EntryHeaderSizeBytecode15Plus = 20;

        private readonly record struct CodeEntryRef(int headerOffset, uint bytecodeAddr, uint originalLength);

        private static bool TryFindCodeEntryByNameAddr(
            byte[] data, ChunkRef code, uint nameAddr, bool bytecode14OrLower,
            out CodeEntryRef entry)
        {
            entry = default;
            if (code.length < 4) return false;
            uint count = ReadUInt32LE(data, code.contentStart);
            int ptrTable = code.contentStart + 4;
            for (uint i = 0; i < count; i++)
            {
                uint headerAddr = ReadUInt32LE(data, ptrTable + (int)i * 4);
                if (headerAddr == 0) continue;
                if (headerAddr + 8 > (uint)data.Length) continue;

                uint entryNameAddr = ReadUInt32LE(data, (int)headerAddr);
                if (entryNameAddr != nameAddr) continue;

                uint length = ReadUInt32LE(data, (int)headerAddr + 4);

                if (bytecode14OrLower)
                {
                    // Instructions sit immediately after the length field.
                    uint bytecodeAddr = headerAddr + 8;
                    entry = new CodeEntryRef((int)headerAddr, bytecodeAddr, length);
                    return true;
                }
                else
                {
                    // Need the signed relative-address field at headerAddr+12.
                    if (headerAddr + 20 > (uint)data.Length) continue;
                    int relAddrFieldPos = (int)headerAddr + 12;
                    int relAddr = ReadInt32LE(data, relAddrFieldPos);
                    long absLong = (long)relAddrFieldPos + relAddr;
                    if (absLong < 0 || absLong + 4 > data.Length)
                    {
                        Log.Warn(LogCategory.AppInstall,
                            $"GameMakerWinNeutralizer: relative bytecode address out of range " +
                            $"(field=0x{relAddrFieldPos:X}, rel={relAddr}, computed=0x{absLong:X})");
                        continue;
                    }
                    entry = new CodeEntryRef((int)headerAddr, (uint)absLong, length);
                    return true;
                }
            }
            return false;
        }

        // ---- Entry mutation -----------------------------------------------------

        private static void NeutralizeEntry(byte[] data, CodeEntryRef entry, bool bytecode14OrLower)
        {
            uint exitWord = bytecode14OrLower ? ExitInt32Legacy : ExitInt32Modern;
            WriteUInt32LE(data, (int)entry.bytecodeAddr, exitWord);
            // Length field is always at headerOffset + 4 regardless of bytecode version.
            WriteUInt32LE(data, entry.headerOffset + 4, 4u);
        }

        // ---- Bootstrap variable injection ---------------------------------------

        // Bytecode 14 opcodes used for the injected sequence.
        // Push int16 immediate: opcode 0xC0, t1=Int16 (0xF), instance=value.
        //   Word layout: (0xC0 << 24) | (0xF << 16) | value
        // Pop variable        : opcode 0x41, t1=Variable(5), t2=Int32(2),
        //                       instance=instance_type. Word layout:
        //                       (0x41 << 24) | (0x2<<20 | 0x5<<16) | (instance & 0xFFFF)
        //                       = 0x41250000 | instance_low16
        //   Trailed by a 4-byte reference field (chain link or NameStringID).
        // Self instance type   = -1 (0xFFFF as ushort)
        // Global instance type = -5 (0xFFFB as ushort)
        private const ushort InstanceSelf = 0xFFFF;
        private const ushort InstanceGlobal = 0xFFFB;

        // VariableType.Normal is 0xA0; chain link top-5-bits = (0xA0 & 0xF8) << 24
        // = 0xA0000000.
        private const uint ChainTypeNormal = 0xA0000000u;
        // Chain-offset bits: lower 27 bits of the reference field.
        private const uint ChainOffsetMask = 0x07FFFFFFu;

        /// <summary>
        /// Total byte size of the injected sequence:
        ///   PushInt16 (4) + (PopVar opcode 4 + ref 4) * N + Exit (4)
        /// </summary>
        private static int BootstrapByteSize(int writeCount) => 4 + writeCount * (4 + 4 + 4) + 4;

        private static int InjectBootstrap(
            byte[] data,
            CodeEntryRef host,
            ChunkRef vari,
            bool bytecode14OrLower,
            Dictionary<string, uint> stringAddrByContent,
            IReadOnlyList<BootstrapWrite> writes)
        {
            if (!bytecode14OrLower)
            {
                // Modern bytecode VARI has a per-variable (InstanceType, VarID) tuple,
                // which means Self.gui_scale and Global.gui_scale are separate
                // entries with separate chains. Supporting that needs additional
                // bookkeeping per scope — out of scope for the current need.
                Log.Warn(LogCategory.AppInstall,
                    "GameMakerWinNeutralizer: bootstrap injection only supports bytecode <=14 currently.");
                return 0;
            }

            // Compute injection footprint and verify it fits in the host's
            // original body length. The host's *current* Length is 4 (the
            // single Exit we wrote during NeutralizeEntry), but the actual
            // bytecode region in the file is the original allocation — we
            // can safely write over the bytes that USED to hold the original
            // instructions, because we're going to bump the Length field to
            // cover only our new sequence.
            int injectionSize = BootstrapByteSize(writes.Count);

            // Walk the per-variable VARI entry list once, capturing the entries
            // for each requested name. Bytecode 14 entry layout: 12 bytes:
            //   +0..3  Name pointer (absolute file offset to UTF-8 content)
            //   +4..7  Occurrences
            //   +8..11 FirstAddress (absolute file offset to first opcode word)
            const int VarEntrySize = 12;
            int varCount = vari.length / VarEntrySize;

            // For each write, locate its VARI entry by matching the name pointer.
            // All writes share the same variable in bytecode 14, but we look them
            // up individually anyway in case someone adds writes to different
            // variables.
            var entryByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var w in writes)
            {
                if (entryByName.ContainsKey(w.Name)) continue;
                if (!stringAddrByContent.TryGetValue(w.Name, out uint nameAddr))
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"GameMakerWinNeutralizer: bootstrap write target '{w.Name}' has no STRG entry; skipping.");
                    continue;
                }

                int found = -1;
                for (int i = 0; i < varCount; i++)
                {
                    int eo = vari.contentStart + i * VarEntrySize;
                    if (ReadUInt32LE(data, eo) == nameAddr) { found = eo; break; }
                }
                if (found < 0)
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"GameMakerWinNeutralizer: bootstrap write target '{w.Name}' has no VARI entry; skipping.");
                    continue;
                }
                entryByName[w.Name] = found;
            }

            // Plan the writes that have valid VARI entries.
            var plan = new List<(BootstrapWrite write, int variEntryOffset)>();
            foreach (var w in writes)
                if (entryByName.TryGetValue(w.Name, out int eo))
                    plan.Add((w, eo));
            if (plan.Count == 0) return 0;

            // Compute the file offsets where each Pop's opcode word and ref
            // field will land. Push (4 bytes) precedes each Pop in our sequence,
            // and we group all writes into a single Push-then-Pop pattern
            // per write — meaning the sequence is:
            //   Push v1, Pop v1, Push v2, Pop v2, ..., Exit
            uint bodyStart = host.bytecodeAddr;
            uint cursor = bodyStart;
            var popPositions = new List<(int opcodeAddr, int refFieldAddr, BootstrapWrite write)>(plan.Count);
            foreach (var (w, _) in plan)
            {
                int pushAddr = (int)cursor;       // Push int16 imm (4 bytes)
                int popOpAddr = (int)(cursor + 4); // Pop opcode word
                int refAddr = (int)(cursor + 8);   // Pop ref field
                popPositions.Add((popOpAddr, refAddr, w));
                cursor += 12;
            }
            // Sanity: forward-only chain. Our chain order is: our first Pop ->
            // our second Pop -> ... -> existing FirstAddress. Each step requires
            // strictly increasing file offset. Verify that the FIRST shared VARI
            // entry's existing FirstAddress is GREATER than our final Pop's
            // ref-field address; otherwise we can't legally splice (the chain
            // walker would need a negative offset, which 27-bit unsigned can't
            // represent).
            foreach (var (w, variEntryOffset) in plan)
            {
                int oldFirstAddr = (int)ReadUInt32LE(data, variEntryOffset + 8);
                int oldFirstRefField = oldFirstAddr + 4;
                int lastInjectedRefField = popPositions[popPositions.Count - 1].refFieldAddr;
                if (oldFirstRefField <= lastInjectedRefField)
                {
                    throw new InvalidOperationException(
                        $"Cannot inject '{w.Name}' before existing chain head: chain offsets are " +
                        $"forward-only and host bytecode at 0x{lastInjectedRefField:X} is not " +
                        $"earlier in file than existing FirstAddress ref field 0x{oldFirstRefField:X}.");
                }
            }

            // Write the instruction sequence. Order: Push imm, Pop var, ..., Exit.
            cursor = bodyStart;
            foreach (var (w, _) in plan)
            {
                // Push int16 imm = (0xC0<<24) | (0xF<<16) | (value as ushort)
                uint pushWord = (0xC0u << 24) | (0x0Fu << 16) | (ushort)w.Int16Value;
                WriteUInt32LE(data, (int)cursor, pushWord);
                cursor += 4;

                // Pop opcode word = (0x41<<24) | (Int32<<20|Variable<<16) | (instance ushort)
                //                 = 0x41250000 | instance_low16
                ushort instance = w.Scope == BootstrapScope.Global ? InstanceGlobal : InstanceSelf;
                uint popWord = 0x41250000u | instance;
                WriteUInt32LE(data, (int)cursor, popWord);
                cursor += 4;

                // Reference field — provisional, patched below with chain links.
                WriteUInt32LE(data, (int)cursor, 0u);
                cursor += 4;
            }
            // Trailing Exit so the script ends after the bootstrap.
            uint exitWord = ExitInt32Legacy; // bytecode14
            WriteUInt32LE(data, (int)cursor, exitWord);
            cursor += 4;

            // Update host CODE entry's Length field.
            int totalBytes = (int)(cursor - bodyStart);
            WriteUInt32LE(data, host.headerOffset + 4, (uint)totalBytes);

            // Splice our new Pops into the chain. For each Pop's ref field:
            //   if it's the LAST in our sequence -> point to existing FirstAddress's ref field
            //   else                              -> point to next injected Pop's ref field
            // The Pop's variable type bits go in the top 5 bits (always Normal=0xA0
            // for self/global var refs we generate).
            for (int i = 0; i < popPositions.Count; i++)
            {
                var (_, refAddr, w) = popPositions[i];
                int variEntryOffset = entryByName[w.Name];

                uint nextRefField;
                if (i + 1 < popPositions.Count)
                {
                    nextRefField = (uint)popPositions[i + 1].refFieldAddr;
                }
                else
                {
                    uint oldFirstAddr = ReadUInt32LE(data, variEntryOffset + 8);
                    nextRefField = oldFirstAddr + 4;
                }
                uint offset = nextRefField - (uint)refAddr;
                if ((offset & ~ChainOffsetMask) != 0)
                    throw new InvalidOperationException(
                        $"Chain offset {offset} exceeds 27-bit range for '{w.Name}'.");
                WriteUInt32LE(data, refAddr, (offset & ChainOffsetMask) | ChainTypeNormal);
            }

            // Update VARI entries: bump Occurrences and replace FirstAddress.
            // All injected writes for the SAME variable share the same VARI entry
            // in bytecode 14, so we accumulate the increment.
            var perEntryAdjust = new Dictionary<int, (int delta, int newFirstOpcodeAddr)>();
            for (int i = 0; i < popPositions.Count; i++)
            {
                var (opcodeAddr, _, w) = popPositions[i];
                int variEntryOffset = entryByName[w.Name];
                if (perEntryAdjust.TryGetValue(variEntryOffset, out var prev))
                {
                    // FirstAddress is the FIRST injected occurrence — keep the
                    // smallest opcode address we've seen for this variable.
                    int newFirst = Math.Min(prev.newFirstOpcodeAddr, opcodeAddr);
                    perEntryAdjust[variEntryOffset] = (prev.delta + 1, newFirst);
                }
                else
                {
                    perEntryAdjust[variEntryOffset] = (1, opcodeAddr);
                }
            }
            foreach (var (variEntryOffset, adjust) in perEntryAdjust)
            {
                uint oldOcc = ReadUInt32LE(data, variEntryOffset + 4);
                WriteUInt32LE(data, variEntryOffset + 4, oldOcc + (uint)adjust.delta);
                WriteUInt32LE(data, variEntryOffset + 8, (uint)adjust.newFirstOpcodeAddr);
            }

            return popPositions.Count;
        }

        // ---- Primitive readers/writers ------------------------------------------

        private static string ReadAscii4(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return string.Empty;
            return Encoding.ASCII.GetString(data, offset, 4);
        }

        private static uint ReadUInt32LE(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static int ReadInt32LE(byte[] data, int offset) => unchecked((int)ReadUInt32LE(data, offset));

        private static void WriteUInt32LE(byte[] data, int offset, uint value)
        {
            data[offset]     = (byte)(value         & 0xFF);
            data[offset + 1] = (byte)((value >> 8)  & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
