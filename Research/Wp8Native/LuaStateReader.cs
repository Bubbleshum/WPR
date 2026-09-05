namespace WPR.Wp8Native;

/// <summary>
/// Reads a Lua 5.1 call stack straight out of guest memory.
/// </summary>
/// <remarks>
/// The game reports its own Lua errors with "(call stack not available)" - its traceback
/// handler cannot see past the C function that raised the error, so a script failure arrives
/// with the message and nothing else. But the interpreter's state is all in guest memory,
/// and Lua 5.1's layout is fixed: <c>lua_State</c> holds the <c>CallInfo</c> array, each
/// entry names the function it is running, and a Lua function's <c>Proto</c> carries the
/// chunk source, the line range it was defined over, and its constant table. That is enough
/// to name the script even when line numbers were stripped, because the constants are the
/// same ones the disassembler prints.
///
/// Layouts are for a 32-bit build with <c>lua_Number</c> as <b>float</b>, which this title's
/// bytecode header says it is - a <c>TValue</c> is therefore 8 bytes, not the 16 of a double
/// build. Every pointer is sanity-checked before it is followed: this runs at the moment of an
/// error, on a state that may be exactly as broken as the error says.
/// </remarks>
public static class LuaStateReader
{
    // lua_State
    private const int StateTop = 8;
    private const int StateCi = 20;
    private const int StateSavedPc = 24;
    private const int StateBaseCi = 40;

    // CallInfo, 24 bytes
    private const int CiBase = 0;
    private const int CiFunc = 4;
    private const int CiSavedPc = 12;
    private const int CiSize = 24;

    // TValue, 8 bytes: value then tag
    private const int TValueSize = 8;
    private const int TagString = 4;
    private const int TagFunction = 6;

    // Closure header, then LClosure.p
    private const int ClosureIsC = 6;
    private const int ClosureProto = 16;

    // Proto
    private const int ProtoK = 8;
    private const int ProtoCode = 12;
    private const int ProtoLineInfo = 20;
    private const int ProtoSource = 32;
    private const int ProtoSizeK = 40;
    private const int ProtoSizeCode = 44;
    private const int ProtoSizeLineInfo = 48;
    private const int ProtoLineDefined = 60;
    private const int ProtoLastLineDefined = 64;

    // TString: header, reserved, hash, len, then the characters
    private const int StringLength = 12;
    private const int StringChars = 16;

    private const int MaxFrames = 24;

    // Other tags
    private const int TagNil = 0;
    private const int TagBoolean = 1;
    private const int TagNumber = 3;
    private const int TagTable = 5;

    // Table: header, flags, lsizenode, metatable, array, node, lastfree, gclist, sizearray
    private const int TableLsizenode = 9;
    private const int TableArray = 12;
    private const int TableNode = 16;
    private const int TableSizeArray = 28;

    // Node: TValue i_val (8), then TKey = TValue fields (8) + next (4)
    private const int NodeSize = 20;
    private const int NodeKeyValue = 8;
    private const int NodeKeyTag = 12;

    /// <summary>
    /// The call stack of the state at <paramref name="state"/>, innermost first.
    /// </summary>
    public static IReadOnlyList<string> Traceback(ArmEmulator emulator, long state)
    {
        var lines = new List<string>();

        if (!Plausible(state))
        {
            lines.Add($"lua_State 0x{state:X8} is not a heap pointer");
            return lines;
        }

        long ci = emulator.ReadUInt32(state + StateCi);
        long baseCi = emulator.ReadUInt32(state + StateBaseCi);
        long topPc = emulator.ReadUInt32(state + StateSavedPc);

        if (!Plausible(ci) || !Plausible(baseCi) || ci < baseCi || (ci - baseCi) % CiSize != 0)
        {
            lines.Add($"lua_State 0x{state:X8}: ci 0x{ci:X8} / base_ci 0x{baseCi:X8} do not look like a CallInfo array");
            return lines;
        }

        int depth = 0;
        for (long entry = ci; entry >= baseCi && depth < MaxFrames; entry -= CiSize, depth++)
        {
            long func = emulator.ReadUInt32(entry + CiFunc);
            long savedPc = depth == 0 ? topPc : emulator.ReadUInt32(entry + CiSavedPc);
            lines.Add($"#{depth} " + DescribeFrame(emulator, func, savedPc));

            // The frame's first locals are its arguments - for a method, self and then the
            // parameters. That is the difference between "releaseAssets was called" and
            // "releaseAssets was called for a group that was never loaded".
            long frameBase = emulator.ReadUInt32(entry + CiBase);
            if (Plausible(frameBase))
            {
                for (int slot = 0; slot < 4; slot++)
                {
                    lines.Add($"      arg{slot} = {DescribeValue(emulator, frameBase + (slot * TValueSize), 0)}");
                }
            }
        }

        if (depth == MaxFrames)
        {
            lines.Add("   ...");
        }

        return lines;
    }

    private static string DescribeFrame(ArmEmulator emulator, long func, long savedPc)
    {
        if (!Plausible(func))
        {
            return $"(func slot 0x{func:X8} unreadable)";
        }

        long value = emulator.ReadUInt32(func);
        long tag = emulator.ReadUInt32(func + 4);

        if (tag != TagFunction || !Plausible(value))
        {
            return $"(not a function: tag {tag}, value 0x{value:X8})";
        }

        if (emulator.ReadMemory(value + ClosureIsC, 1)[0] != 0)
        {
            return "[C function]";
        }

        long proto = emulator.ReadUInt32(value + ClosureProto);
        if (!Plausible(proto))
        {
            return $"Lua function with unreadable Proto 0x{proto:X8}";
        }

        string source = ReadString(emulator, emulator.ReadUInt32(proto + ProtoSource));
        int lineDefined = (int)emulator.ReadUInt32(proto + ProtoLineDefined);
        int lastLine = (int)emulator.ReadUInt32(proto + ProtoLastLineDefined);

        string line = CurrentLine(emulator, proto, savedPc);
        string constants = Constants(emulator, proto);

        return $"{source}:{lineDefined}-{lastLine}{line}  constants: {constants}";
    }

    private static string CurrentLine(ArmEmulator emulator, long proto, long savedPc)
    {
        long code = emulator.ReadUInt32(proto + ProtoCode);
        int sizeCode = (int)emulator.ReadUInt32(proto + ProtoSizeCode);
        int sizeLineInfo = (int)emulator.ReadUInt32(proto + ProtoSizeLineInfo);
        long lineInfo = emulator.ReadUInt32(proto + ProtoLineInfo);

        if (!Plausible(code) || savedPc < code || sizeCode <= 0)
        {
            return string.Empty;
        }

        long index = ((savedPc - code) / 4) - 1;
        if (index < 0 || index >= sizeCode)
        {
            return $" pc={index}";
        }

        if (sizeLineInfo > index && Plausible(lineInfo))
        {
            int lineNumber = (int)emulator.ReadUInt32(lineInfo + (index * 4));
            return $" line {lineNumber} (pc {index})";
        }

        return $" pc {index} (no line info)";
    }

    private static string Constants(ArmEmulator emulator, long proto)
    {
        long k = emulator.ReadUInt32(proto + ProtoK);
        int sizeK = (int)emulator.ReadUInt32(proto + ProtoSizeK);
        if (!Plausible(k) || sizeK <= 0 || sizeK > 4096)
        {
            return "(none)";
        }

        var names = new List<string>();
        for (int i = 0; i < sizeK && names.Count < 8; i++)
        {
            long slot = k + (i * TValueSize);
            if (emulator.ReadUInt32(slot + 4) != TagString)
            {
                continue;
            }

            names.Add($"'{ReadString(emulator, emulator.ReadUInt32(slot))}'");
        }

        return names.Count == 0 ? "(no strings)" : string.Join(" ", names);
    }

    /// <summary>One stack slot, with a table's keys listed one level deep.</summary>
    private static string DescribeValue(ArmEmulator emulator, long slot, int nesting)
    {
        long value = emulator.ReadUInt32(slot);
        long tag = emulator.ReadUInt32(slot + 4);

        switch (tag)
        {
            case TagNil:
                return "nil";
            case TagBoolean:
                return value != 0 ? "true" : "false";
            case TagNumber:
                return BitConverter.Int32BitsToSingle((int)value).ToString("0.###");
            case TagString:
                return $"\"{ReadString(emulator, value)}\"";
            case TagFunction:
                return "function";
            case TagTable:
                return nesting > 0 ? "table" : DescribeTable(emulator, value);
            default:
                return $"(tag {tag})";
        }
    }

    private static string DescribeTable(ArmEmulator emulator, long table)
    {
        if (!Plausible(table))
        {
            return $"table @0x{table:X8}";
        }

        int sizeArray = (int)emulator.ReadUInt32(table + TableSizeArray);
        int lsizenode = emulator.ReadMemory(table + TableLsizenode, 1)[0];
        long node = emulator.ReadUInt32(table + TableNode);
        int nodes = lsizenode < 24 ? 1 << lsizenode : 0;

        var keys = new List<string>();
        if (Plausible(node))
        {
            for (int i = 0; i < nodes && keys.Count < 16; i++)
            {
                long at = node + (i * NodeSize);
                long keyTag = emulator.ReadUInt32(at + NodeKeyTag);
                if (keyTag == TagNil)
                {
                    continue;
                }

                string key = DescribeValue(emulator, at + NodeKeyValue, 1);
                string val = DescribeValue(emulator, at, 1);
                keys.Add($"{key}={val}");
            }
        }

        string arrayPart = sizeArray > 0 ? $"[{sizeArray} array]" : string.Empty;
        string hashPart = keys.Count == 0 ? (nodes > 1 ? "{}" : string.Empty) : "{" + string.Join(", ", keys) + (keys.Count == 16 ? ", ..." : "") + "}";
        return $"table{arrayPart}{hashPart}";
    }

    private static string ReadString(ArmEmulator emulator, long tstring)
    {
        if (!Plausible(tstring))
        {
            return "?";
        }

        int length = (int)emulator.ReadUInt32(tstring + StringLength);
        if (length <= 0 || length > 200)
        {
            return length == 0 ? string.Empty : $"(string of {length})";
        }

        byte[] bytes = emulator.ReadMemory(tstring + StringChars, length);
        return System.Text.Encoding.Latin1.GetString(bytes);
    }

    /// <summary>A pointer into the image, heap or stack - anything else is garbage.</summary>
    private static bool Plausible(long pointer)
        => pointer >= 0x00400000 && pointer < 0xA0000000 && (pointer & 1) == 0;
}
