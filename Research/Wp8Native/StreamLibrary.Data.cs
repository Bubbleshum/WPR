using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The iostream and locale imports that are variables rather than functions: the standard
    /// streams, the facets' <c>locale::id</c> objects, vtables, <c>_BADOFF</c>.
    /// </summary>
    /// <remarks>
    /// An IAT slot for one of these holds the object's address, and the game reads or writes
    /// through it - so a trap there is not "unimplemented" but actively wrong: the game uses
    /// trap-page code as data (<c>_BADOFF</c> read back as two instructions; an <c>id</c> written
    /// into the trap page). <see cref="ArmEmulator"/> leaves these slots to this class, which
    /// fills them as soon as the host exists, before any guest code runs.
    /// </remarks>
    public sealed partial class StreamLibrary
    {
        private static readonly string[] StandardNames = ["cout", "cin", "cerr", "clog"];

        private static readonly HashSet<string> StreamVtableNames = new(StringComparer.Ordinal)
        {
            "??_7ios_base@std@@6B@",
            "??_7?$basic_ios@DU?$char_traits@D@std@@@std@@6B@",
            "??_7?$basic_ios@_WU?$char_traits@_W@std@@@std@@6B@",
            "??_7?$basic_ostream@DU?$char_traits@D@std@@@std@@6B@",
            "??_7?$basic_ostream@_WU?$char_traits@_W@std@@@std@@6B@",
            "??_7?$basic_istream@DU?$char_traits@D@std@@@std@@6B@",
            "??_7?$basic_istream@_WU?$char_traits@_W@std@@@std@@6B@",
            "??_7?$basic_iostream@DU?$char_traits@D@std@@@std@@6B@",
            "??_7?$basic_iostream@_WU?$char_traits@_W@std@@@std@@6B@",
            "??_7?$basic_streambuf@DU?$char_traits@D@std@@@std@@6B@",
            "??_7?$basic_streambuf@_WU?$char_traits@_W@std@@@std@@6B@",
        };

        private static readonly HashSet<string> LocaleVtableNames = new(StringComparer.Ordinal)
        {
            "??_7facet@locale@std@@6B@", "??_7_Facet_base@std@@6B@", "??_7_Locimp@locale@std@@6B@",
            "??_7codecvt_base@std@@6B@", "??_7ctype_base@std@@6B@",
            "??_7?$codecvt@DDH@std@@6B@", "??_7?$codecvt@_WDH@std@@6B@",
            "??_7?$ctype@D@std@@6B@", "??_7?$ctype@_W@std@@6B@",
            "??_7?$numpunct@D@std@@6B@", "??_7?$numpunct@_W@std@@6B@",
            "??_7?$collate@D@std@@6B@", "??_7?$collate@_W@std@@6B@",
        };

        private readonly Dictionary<string, long> _streamVtables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _standardStreams = new(StringComparer.Ordinal);
        private readonly Dictionary<long, HostBuffer> _hostBuffers = new();

        /// <summary>A streambuf of ours behind a standard stream: where its text goes.</summary>
        private sealed class HostBuffer(string stream, bool output)
        {
            public string Stream { get; } = stream;

            public bool Output { get; } = output;

            public StringBuilder Line { get; } = new();
        }

        /// <summary>Lines the game wrote to std::cout, cerr, clog and their wide twins.</summary>
        public List<string> StandardOutput { get; } = new();

        /// <summary>Whether an import names one of the variables this class provides.</summary>
        public static bool IsDataImport(string name)
        {
            foreach (string standard in StandardNames)
            {
                if (name.StartsWith($"?{standard}@std@@3", StringComparison.Ordinal) ||
                    name.StartsWith($"?w{standard}@std@@3", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return name is "?_BADOFF@std@@3_JB" or "?_Fpz@std@@3_JB" ||
                   (name.StartsWith("?id@?$", StringComparison.Ordinal) && name.EndsWith("@2V0locale@2@A", StringComparison.Ordinal)) ||
                   name.StartsWith("?id@collate@", StringComparison.Ordinal) ||
                   name.StartsWith("?table_size@?$ctype@D@std@@2", StringComparison.Ordinal) ||
                   StreamVtableNames.Contains(name) || LocaleVtableNames.Contains(name);
        }

        /// <summary>Builds the object a data import names (<see cref="IsDataImport"/>); 0 if it is not one.</summary>
        public long BuildDataImport(string name)
        {
            // One variable per name, as one MSVCP110 export is one object: two modules importing
            // ctype<char>::id must see the same id, or a facet one registers the other never finds.
            if (_dataImports.TryGetValue(name, out long built))
            {
                return built;
            }

            built = BuildDataImportOnce(name);
            if (built != 0)
            {
                _dataImports[name] = built;
            }

            return built;
        }

        private readonly Dictionary<string, long> _dataImports = new(StringComparer.Ordinal);

        private long BuildDataImportOnce(string name)
        {
            foreach (string standard in StandardNames)
            {
                if (name.StartsWith($"?{standard}@std@@3", StringComparison.Ordinal))
                {
                    return StandardStream(_narrow, standard);
                }

                if (name.StartsWith($"?w{standard}@std@@3", StringComparison.Ordinal))
                {
                    return StandardStream(_wide, standard);
                }
            }

            if (name is "?_BADOFF@std@@3_JB" or "?_Fpz@std@@3_JB")
            {
                // extern const streamoff _BADOFF = -1 (and the zero fpos_t, _Fpz).
                long cell = _emulator.AllocateHeap(8);
                WriteInt64(cell, name.Contains("_BADOFF", StringComparison.Ordinal) ? -1 : 0);
                return cell;
            }

            if (name.StartsWith("?table_size@", StringComparison.Ordinal))
            {
                long cell = _emulator.AllocateHeap(4);
                Write32(cell, 256);
                return cell;
            }

            if (name.StartsWith("?id@", StringComparison.Ordinal))
            {
                // locale::id { size_t _Id; }: zero until id::operator size_t assigns it.
                long cell = _emulator.AllocateHeap(4);
                Write32(cell, 0);
                return cell;
            }

            if (LocaleVtableNames.Contains(name))
            {
                return Locales.VtableForImport(name);
            }

            if (StreamVtableNames.Contains(name))
            {
                bool wide = name.Contains("@_WU", StringComparison.Ordinal);
                Kind k = wide ? _wide : _narrow;
                return name switch
                {
                    "??_7ios_base@std@@6B@" => StreamVtable(_narrow, "ios_base", 0),
                    _ when name.Contains("basic_iostream", StringComparison.Ordinal) => StreamVtable(k, "basic_iostream", 0x18),
                    _ when name.Contains("basic_istream", StringComparison.Ordinal) => StreamVtable(k, "basic_istream", 0x10),
                    _ when name.Contains("basic_ostream", StringComparison.Ordinal) => StreamVtable(k, "basic_ostream", 0x08),
                    _ when name.Contains("basic_streambuf", StringComparison.Ordinal) => StreambufVtable(k),
                    _ => StreamVtable(k, "basic_ios", 0),
                };
            }

            return 0;
        }

        /// <summary>Fills the IAT slots <see cref="ArmEmulator"/> left to this class.</summary>
        private void FillDataImports()
        {
            foreach ((string name, long slot) in _emulator.StreamDataImports)
            {
                long value = BuildDataImport(name);
                Write32(slot, value);
                _emulator.DataImportCells.Add($"{name} -> {(value == 0 ? "NULL (not built)" : $"0x{value:X8}")}");
            }
        }

        /// <summary>
        /// The vtable of a stream class, which lives in its basic_ios subobject: slot 0 the scalar
        /// deleting destructor (called with the ios as this: destroy it, then free the complete
        /// object, <paramref name="iosOffset"/> before it), then _Add_vtordisp1 and 2.
        /// </summary>
        private long StreamVtable(Kind k, string cls, int iosOffset)
        {
            string key = $"{cls}<{k.Name}>";
            if (_streamVtables.TryGetValue(key, out long table))
            {
                return table;
            }

            table = _emulator.AllocateHeap(12);
            long dtor = _emulator.RegisterVtableMethod($"{key}::dtor", () =>
            {
                long ios = _frame.Arg(0);
                bool free = (_frame.Arg(1) & 1) != 0;
                long returnTo = _emulator.ReturnAddress;
                DestroyIosBase(ios, () =>
                {
                    if (free)
                    {
                        _emulator.FreeHeap(ios - iosOffset);
                    }

                    Finish(ios, returnTo);
                });
            });
            long vtordisp1 = _emulator.RegisterVtableMethod($"{key}::_Add_vtordisp1", () => Done(0));
            long vtordisp2 = _emulator.RegisterVtableMethod($"{key}::_Add_vtordisp2", () => Done(0));
            Write32(table, ArmEmulator.ThumbEntry(dtor));
            Write32(table + 4, ArmEmulator.ThumbEntry(vtordisp1));
            Write32(table + 8, ArmEmulator.ThumbEntry(vtordisp2));
            _streamVtables[key] = table;
            return table;
        }

        // ------------------------------------------------------------------------------
        // The standard streams
        // ------------------------------------------------------------------------------

        /// <summary>
        /// std::cout / cin / cerr / clog (or the wide ones): real streams over a buffer of ours.
        /// Output goes to the per-game log a line at a time - the honest destination for a phone
        /// app's stdout; input is always at its end, as an app's stdin is. cerr is unitbuf and,
        /// with cin, tied to cout, as the standard requires.
        /// </summary>
        private long StandardStream(Kind k, string name)
        {
            string key = (k.Wide ? "w" : string.Empty) + name;
            if (_standardStreams.TryGetValue(key, out long existing))
            {
                return existing;
            }

            bool input = name == "cin";
            int iosOffset = input ? 0x10 : 0x08;
            long self = _emulator.AllocateHeap(iosOffset + IosSize);
            _emulator.WriteMemory(self, new byte[iosOffset + IosSize]);
            _standardStreams[key] = self;
            Write32(self, VbTable(iosOffset));
            long ios = self + iosOffset;
            Write32(ios, StreamVtable(k, input ? "basic_istream" : "basic_ostream", iosOffset));
            InitIos(k, ios, HostStreambuf(k, input ? "stdin" : name == "cout" ? "stdout" : "stderr", output: !input), standard: true);

            // Held open by the library itself (ios_base::Init), so a destructor never closes it.
            _stdOpens[ios] = 1 << 30;
            if (name == "cerr")
            {
                Write32(ios + Flags, Read32(ios + Flags) | UnitBuf);
            }

            if (name is "cerr" or "cin")
            {
                Write32(ios + Tie, StandardStream(k, "cout"));
            }

            Note($"std::{key} at 0x{self:X8}");
            return self;
        }

        private long _hostVtableNarrow, _hostVtableWide;

        private long HostStreambuf(Kind k, string stream, bool output)
        {
            long sb = _emulator.AllocateHeap(StreambufSize);
            InitializeStreambuf(k, sb);
            ref long vtable = ref (k.Wide ? ref _hostVtableWide : ref _hostVtableNarrow);
            if (vtable == 0)
            {
                vtable = HostVtable(k);
            }

            Write32(sb, vtable);
            _hostBuffers[sb] = new HostBuffer(stream, output);
            return sb;
        }

        /// <summary>
        /// The standard buffers' vtable. It has no put or get area, so every character reaches
        /// overflow or underflow: output is appended to the line being built, input is at its end.
        /// </summary>
        private long HostVtable(Kind k)
        {
            long table = _emulator.AllocateHeap(StreambufSlots.Length * 4);
            for (int i = 0; i < StreambufSlots.Length; i++)
            {
                string slot = StreambufSlots[i];
                long trap = _emulator.RegisterVtableMethod($"std stream buffer<{k.Name}>::{slot}", () => HostVirtual(k, slot));
                Write32(table + (i * 4), ArmEmulator.ThumbEntry(trap));
            }

            return table;
        }

        private void HostVirtual(Kind k, string slot)
        {
            long sb = _frame.Arg(0);
            bool output = IsHostOutput(sb);
            switch (slot)
            {
                case "overflow":
                {
                    int c = CharResult(k, _frame.Arg(1));
                    if (c == -1)
                    {
                        Done(0); // not_eof(eof)
                        return;
                    }

                    if (!output)
                    {
                        Done(k.EofRegister);
                        return;
                    }

                    HostWrite(k, sb, [c]);
                    Done(c);
                    return;
                }

                case "xsputn":
                {
                    long count = Arg64();
                    if (!output || count <= 0)
                    {
                        _frame.Return64(0);
                        return;
                    }

                    HostWrite(k, sb, ReadChars(k, _frame.Arg(1), (int)Math.Min(count, 1 << 20)));
                    _frame.Return64(Math.Min(count, 1 << 20));
                    return;
                }

                case "sync":
                    HostSync(sb);
                    Done(0);
                    return;
                case "showmanyc":
                    _frame.Return64(output ? 0 : -1);
                    return;
                case "xsgetn":
                    _frame.Return64(0);
                    return;
                case "pbackfail":
                case "underflow":
                case "uflow":
                    Done(k.EofRegister);
                    return;
                case "seekoff":
                case "seekpos":
                    WriteFpos(_frame.Arg(1), -1);
                    Done(_frame.Arg(1));
                    return;
                default:
                    Done(slot == "setbuf" || slot == "dtor" ? sb : 0);
                    return;
            }
        }

        private bool IsHostOutput(long sb) => _hostBuffers.TryGetValue(sb, out HostBuffer? buffer) && buffer.Output;

        private void HostWrite(Kind k, long sb, int[] chars)
        {
            HostBuffer buffer = _hostBuffers[sb];
            foreach (int c in chars)
            {
                if (c == '\n')
                {
                    Emit(buffer);
                    continue;
                }

                if (buffer.Line.Length < 4096)
                {
                    buffer.Line.Append(k.Wide ? (char)c : (char)(c & 0xFF));
                }
            }
        }

        private void HostSync(long sb)
        {
            if (_hostBuffers.TryGetValue(sb, out HostBuffer? buffer) && buffer.Line.Length > 0)
            {
                Emit(buffer);
            }
        }

        private void Emit(HostBuffer buffer)
        {
            string line = buffer.Line.ToString();
            buffer.Line.Clear();
            System.Diagnostics.Trace.WriteLine($"[wpr-wp8-{buffer.Stream}] {line}");
            if (StandardOutput.Count < 500)
            {
                StandardOutput.Add($"{buffer.Stream}: {line}");
            }
        }
    }
}
