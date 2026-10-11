using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// MSVCP110's <c>std::locale</c>: the locale object, its <c>_Locimp</c>, the <c>id</c> and
    /// facet machinery that <c>use_facet</c> inlines into every game, the standard facets of the
    /// "C" locale, <c>_Locinfo</c>, <c>_Yarn</c> and the C-locale conversion helpers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the "C" locale exists</b>, which is what a WP8 app gets unless it asks for another.
    /// A <c>_Locinfo</c> for any other name records <c>"*"</c> as its new name, which is the
    /// marker VC11's <c>locale(const char*)</c> tests before throwing <c>runtime_error("bad locale
    /// name")</c> - the documented failure.
    /// </para>
    /// <para>
    /// <b>Layouts.</b> <c>use_facet</c> is inlined, so the games read these structures directly.
    /// Measured on Terraria's inlined <c>locale::_Getfacet</c> (0x457BA8): <c>_Locimp</c> keeps
    /// <c>_Facetvec</c> at +8, <c>_Facetcount</c> at +0xC and <c>_Xparent</c> (a byte) at +0x14,
    /// after facet's vfptr and <c>_Refs</c> (+4); <c>_Catmask</c> is +0x10 and the <c>_Name</c>
    /// yarn +0x18. A <c>locale</c> is the one pointer to its <c>_Locimp</c>. Its use_facet
    /// (0x492E3C) then shows the rest of the contract: <c>id</c> is a data import read through
    /// <c>id::operator size_t</c>, a missing facet comes from <c>_Getcat(&amp;psave, &amp;loc)</c>
    /// with -1 meaning bad_cast, and the new facet's <c>_Incref</c> is vtable slot 1.
    /// </para>
    /// <para>
    /// <b>Facet vtables</b> are slot 0 the scalar deleting destructor, 1 <c>_Incref</c>, 2
    /// <c>_Decref</c>, then the class's virtuals in declaration order - except that MSVC places a
    /// set of overloaded virtuals together and in reverse declaration order, so
    /// <c>ctype&lt;char&gt;::do_tolower(char*, const char*)</c> comes before
    /// <c>do_tolower(char)</c>. That order is only observable through a game class derived from a
    /// standard facet; no installed title has one, so it is stated from MSVC's rule rather than
    /// measured. The non-virtual members (<c>tolower</c>, <c>in</c>, <c>always_noconv</c>, ...)
    /// dispatch through the object's own vtable, as the library's do, so a game's override is
    /// honoured.
    /// </para>
    /// <para>
    /// <b>Lazy facets.</b> As in VC11, the "C" <c>_Locimp</c> starts with no facets: use_facet
    /// falls back to <c>_Getcat</c>, which builds one. VC11's <c>_Locimp</c> copy of the classic
    /// locale also runs <c>_Makeloc</c> to fill the vector; here the copy is a plain copy, so a
    /// copied classic locale is lazily populated exactly like the classic one. That is visible
    /// only to code reading <c>_Facetvec</c> directly.
    /// </para>
    /// </remarks>
    public sealed class LocaleLibrary
    {
        // locale::category bits, and the LC_* index _Getcat answers.
        private const int CatCtype = 2, CatAll = 0x3F;
        private const int LcCollate = 1, LcCtype = 2, LcNumeric = 4, LcTime = 5;

        // facet
        private const int Refs = 4;

        // _Locimp
        private const int Facetvec = 8, Facetcount = 0xC, Catmask = 0x10, Xparent = 0x14, NameYarn = 0x18, LocimpSize = 0x20;

        // ctype<char>: _Ctypevec { _Page, _Table, _Delfl, _LocaleName } at +8
        private const int CtypeTable = 0x0C, CtypeDelfl = 0x10, CtypeName = 0x14, CtypeSize = 0x18;

        // ctype masks (the CRT's C1_* bits, which is what VC's ctype_base uses)
        private const int Upper = 0x1, Lower = 0x2, Digit = 0x4, Space = 0x8, Punct = 0x10, Cntrl = 0x20, Blank = 0x40, Xdigit = 0x80, Alpha = 0x100;

        // codecvt_base::result
        private const int Ok = 0, Partial = 1, Error = 2, Noconv = 3;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Dictionary<string, long> _vtables = new(StringComparer.Ordinal);
        private readonly HashSet<long> _hostVtables = new();
        private readonly Dictionary<string, long> _strings = new(StringComparer.Ordinal);
        private long _global;
        private long _classic;
        private long _classicLocale;
        private long _classicTable;
        private long _lconv;
        private int _idCount;

        public LocaleLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        /// <summary>What the locale code did, for the report.</summary>
        public List<string> Log { get; } = new();

        private void Note(string line)
        {
            if (Log.Count < 100)
            {
                Log.Add(line);
            }
        }

        public void RegisterInto(Dictionary<string, Action> h)
        {
            // --- locale ---
            h["?_Init@locale@std@@CAPAV_Locimp@12@_N@Z"] = () => _frame.Return(Init((_frame.Arg(0) & 0xFF) != 0));
            h["?_Getgloballocale@locale@std@@CAPAV_Locimp@12@XZ"] = () => _frame.Return(Global());
            h["?_Setgloballocale@locale@std@@CAXPAX@Z"] = () => { _global = _frame.Arg(0); _frame.Return(0); };
            h["?classic@locale@std@@SAABV12@XZ"] = () => { Init(false); _frame.Return(_classicLocale); };
            h["?empty@locale@std@@SA?AV12@XZ"] = () =>
            {
                Init(false);
                long result = _frame.Arg(0);
                _emulator.WriteUInt32(result, (uint)NewLocimp(transparent: true));
                _frame.Return(result);
            };

            // static locale global(const locale&): the result by hidden pointer in r0, the argument in r1.
            h["?global@locale@std@@SA?AV12@ABV12@@Z"] = () =>
            {
                long result = _frame.Arg(0);
                long incoming = _emulator.ReadUInt32(_frame.Arg(1));
                long old = Global();
                Incref(old);
                _emulator.WriteUInt32(result, (uint)old);
                if (incoming != old && incoming != 0)
                {
                    Incref(incoming);
                    _global = incoming;
                    long returnTo = _emulator.ReturnAddress;
                    ReleaseLocimp(old, () => Finish(result, returnTo));
                    return;
                }

                _frame.Return(result);
            };

            // size_t id::operator size_t(): the id is a data import, assigned on first use.
            h["??Bid@locale@std@@QAAIXZ"] = () => _frame.Return(IdOf(_frame.Arg(0)));
            h["??0id@locale@std@@QAA@I@Z"] = () =>
            {
                _emulator.WriteUInt32(_frame.Arg(0), (uint)_frame.Arg(1));
                _frame.Return(_frame.Arg(0));
            };

            // --- _Locimp ---
            h["?_New_Locimp@_Locimp@locale@std@@CAPAV123@ABV123@@Z"] = () =>
            {
                long returnTo = _emulator.ReturnAddress;
                CopyLocimp(_frame.Arg(0), copy => Finish(copy, returnTo));
            };
            h["?_New_Locimp@_Locimp@locale@std@@CAPAV123@_N@Z"] = () => _frame.Return(NewLocimp((_frame.Arg(0) & 0xFF) != 0));
            h["?_Addfac@_Locimp@locale@std@@AAAXPAVfacet@23@I@Z"] = () => AddFacet(_frame.Arg(0), _frame.Arg(1), _frame.Arg(2));
            h["?_Locimp_Addfac@_Locimp@locale@std@@CAXPAV123@PAVfacet@23@I@Z"] = () => AddFacet(_frame.Arg(0), _frame.Arg(1), _frame.Arg(2));

            // --- facet ---
            h["?_Incref@facet@locale@std@@UAAXXZ"] = () => { Incref(_frame.Arg(0)); _frame.Return(0); };
            h["?_Decref@facet@locale@std@@UAAPAV_Facet_base@3@XZ"] = () => _frame.Return(Decref(_frame.Arg(0)));
            h["??0facet@locale@std@@IAA@I@Z"] = () =>
            {
                long self = _frame.Arg(0);
                _emulator.WriteUInt32(self, (uint)Vtable("facet"));
                _emulator.WriteUInt32(self + Refs, (uint)_frame.Arg(1));
                _frame.Return(self);
            };
            h["??1facet@locale@std@@MAA@XZ"] = () => _frame.Return(0);
            h["??1facet@locale@std@@UAA@XZ"] = () => _frame.Return(0);
            h["??1_Facet_base@std@@UAA@XZ"] = () => _frame.Return(0);

            RegisterCtype(h);
            RegisterCodecvt(h);
            RegisterOtherFacets(h);
            RegisterLocinfo(h);
            RegisterConversions(h);
        }

        // ------------------------------------------------------------------------------
        // Data imports
        // ------------------------------------------------------------------------------

        /// <summary>The vtable a <c>??_7</c> data import names, or 0 when this library has none.</summary>
        public long VtableForImport(string import) => import switch
        {
            "??_7facet@locale@std@@6B@" => Vtable("facet"),
            "??_7_Facet_base@std@@6B@" => Vtable("_Facet_base"),
            "??_7_Locimp@locale@std@@6B@" => Vtable("_Locimp"),
            "??_7codecvt_base@std@@6B@" => Vtable("codecvt_base"),
            "??_7ctype_base@std@@6B@" => Vtable("facet"),
            "??_7?$codecvt@DDH@std@@6B@" => Vtable("codecvt<char>"),
            "??_7?$codecvt@_WDH@std@@6B@" => Vtable("codecvt<wchar_t>"),
            "??_7?$ctype@D@std@@6B@" => Vtable("ctype<char>"),
            "??_7?$ctype@_W@std@@6B@" => Vtable("ctype<wchar_t>"),
            "??_7?$numpunct@D@std@@6B@" => Vtable("numpunct<char>"),
            "??_7?$numpunct@_W@std@@6B@" => Vtable("numpunct<wchar_t>"),
            "??_7?$collate@D@std@@6B@" => Vtable("collate<char>"),
            "??_7?$collate@_W@std@@6B@" => Vtable("collate<wchar_t>"),
            _ => 0,
        };

        // ------------------------------------------------------------------------------
        // locale and _Locimp
        // ------------------------------------------------------------------------------

        /// <summary>
        /// locale::_Init: makes the global "C" _Locimp on first use (and the classic locale to
        /// match), optionally taking a reference for the caller.
        /// </summary>
        public long Init(bool incref)
        {
            if (_global == 0)
            {
                _global = NewLocimp(transparent: false);
                _emulator.WriteUInt32(_global + Catmask, CatAll);
                SetYarn(_global + NameYarn, "C");
                _classic = _global;
                Incref(_classic);
                _classicLocale = Allocate(4);
                _emulator.WriteUInt32(_classicLocale, (uint)_classic);
                Note($"global \"C\" locale at 0x{_global:X8}");
            }

            if (incref)
            {
                Incref(_global);
            }

            return _global;
        }

        /// <summary>locale::_Getgloballocale: the global _Locimp, made on first use.</summary>
        public long Global() => _global != 0 ? _global : Init(false);

        /// <summary>A heap <c>locale</c> object holding a reference to the global _Locimp: what <c>new locale</c> makes.</summary>
        public long NewLocaleObject()
        {
            long locale = Allocate(4);
            _emulator.WriteUInt32(locale, (uint)Init(true));
            return locale;
        }

        /// <summary>Copies a <c>locale</c> (its _Locimp pointer and a reference) into <paramref name="destination"/>.</summary>
        public void CopyLocale(long source, long destination)
        {
            long imp = source == 0 ? 0 : _emulator.ReadUInt32(source);
            if (imp == 0)
            {
                imp = Global();
            }

            Incref(imp);
            _emulator.WriteUInt32(destination, (uint)imp);
        }

        /// <summary>Points <paramref name="destination"/> at <paramref name="source"/>'s _Locimp, releasing the one it held.</summary>
        public void AssignLocale(long destination, long source, Action then)
        {
            long incoming = _emulator.ReadUInt32(source);
            long outgoing = _emulator.ReadUInt32(destination);
            Incref(incoming);
            _emulator.WriteUInt32(destination, (uint)incoming);
            ReleaseLocimp(outgoing, then);
        }

        /// <summary><c>delete</c> of a heap locale object: release its _Locimp, then free it.</summary>
        public void DeleteLocaleObject(long locale, Action then)
        {
            if (locale == 0)
            {
                then();
                return;
            }

            long imp = _emulator.ReadUInt32(locale);
            ReleaseLocimp(imp, () =>
            {
                _emulator.FreeHeap(locale);
                then();
            });
        }

        private long NewLocimp(bool transparent)
        {
            long imp = Allocate(LocimpSize);
            _emulator.WriteUInt32(imp, (uint)Vtable("_Locimp"));
            _emulator.WriteUInt32(imp + Refs, 1);
            _emulator.WriteMemory(imp + Xparent, [transparent ? (byte)1 : (byte)0]);
            SetYarn(imp + NameYarn, "*");
            return imp;
        }

        /// <summary>
        /// _Locimp's copy constructor: the facet vector copied and each facet referenced. A facet
        /// the game made is referenced through its own vtable.
        /// </summary>
        private void CopyLocimp(long source, Action<long> then)
        {
            long imp = Allocate(LocimpSize);
            _emulator.WriteUInt32(imp, (uint)Vtable("_Locimp"));
            _emulator.WriteUInt32(imp + Refs, 1);
            _emulator.WriteUInt32(imp + Catmask, _emulator.ReadUInt32(source + Catmask));
            _emulator.WriteMemory(imp + Xparent, _emulator.ReadMemory(source + Xparent, 1));
            SetYarn(imp + NameYarn, YarnText(source + NameYarn));

            int count = (int)_emulator.ReadUInt32(source + Facetcount);
            long sourceVector = _emulator.ReadUInt32(source + Facetvec);
            if (count <= 0 || sourceVector == 0)
            {
                then(imp);
                return;
            }

            long vector = Allocate(count * 4);
            _emulator.WriteMemory(vector, _emulator.ReadMemory(sourceVector, count * 4));
            _emulator.WriteUInt32(imp + Facetvec, (uint)vector);
            _emulator.WriteUInt32(imp + Facetcount, (uint)count);

            void Next(int i)
            {
                for (; i < count; i++)
                {
                    long facet = _emulator.ReadUInt32(vector + (i * 4));
                    if (facet == 0)
                    {
                        continue;
                    }

                    if (IsHostObject(facet))
                    {
                        Incref(facet);
                        continue;
                    }

                    int at = i;
                    CallGuest(Slot(facet, 1), [facet], _ => Next(at + 1));
                    return;
                }

                then(imp);
            }

            Next(0);
        }

        /// <summary>
        /// _Locimp::_Addfac: grow the vector to cover the id (at least 40 slots, as VC11 does),
        /// reference the new facet and release the one it replaces.
        /// </summary>
        private void AddFacet(long imp, long facet, long id)
        {
            long returnTo = _emulator.ReturnAddress;
            int count = (int)_emulator.ReadUInt32(imp + Facetcount);
            long vector = _emulator.ReadUInt32(imp + Facetvec);
            if (id >= count || vector == 0)
            {
                int grown = (int)Math.Max(id + 1, 40);
                long bigger = Allocate(grown * 4);
                if (vector != 0 && count > 0)
                {
                    _emulator.WriteMemory(bigger, _emulator.ReadMemory(vector, count * 4));
                    _emulator.FreeHeap(vector);
                }

                vector = bigger;
                _emulator.WriteUInt32(imp + Facetvec, (uint)vector);
                _emulator.WriteUInt32(imp + Facetcount, (uint)grown);
            }

            long slot = vector + (id * 4);
            void Store()
            {
                long old = _emulator.ReadUInt32(slot);
                _emulator.WriteUInt32(slot, (uint)facet);
                ReleaseFacet(old, () => Finish(0, returnTo));
            }

            if (facet == 0 || IsHostObject(facet))
            {
                Incref(facet);
                Store();
                return;
            }

            CallGuest(Slot(facet, 1), [facet], _ => Store());
        }

        /// <summary>_Decref a _Locimp and delete it once the last reference goes.</summary>
        public void ReleaseLocimp(long imp, Action then)
        {
            if (imp == 0 || Decref(imp) == 0)
            {
                then();
                return;
            }

            DestroyLocimp(imp, () =>
            {
                _emulator.FreeHeap(imp);
                then();
            });
        }

        /// <summary>~_Locimp: release every facet (game facets through their own vtables), free the vector and the name.</summary>
        private void DestroyLocimp(long imp, Action then)
        {
            if (imp == _global || imp == _classic)
            {
                // The global and classic locales hold references of their own and are never
                // destroyed; this is reached only if a game over-releases them.
                Note($"refused to destroy the {(imp == _classic ? "classic" : "global")} _Locimp");
                then();
                return;
            }

            int count = (int)_emulator.ReadUInt32(imp + Facetcount);
            long vector = _emulator.ReadUInt32(imp + Facetvec);

            void Next(int i)
            {
                if (vector == 0 || i >= count)
                {
                    if (vector != 0)
                    {
                        _emulator.FreeHeap(vector);
                    }

                    _emulator.WriteUInt32(imp + Facetvec, 0);
                    _emulator.WriteUInt32(imp + Facetcount, 0);
                    FreeYarn(imp + NameYarn);
                    then();
                    return;
                }

                long facet = _emulator.ReadUInt32(vector + (i * 4));
                ReleaseFacet(facet, () => Next(i + 1));
            }

            Next(0);
        }

        /// <summary><c>delete facet-&gt;_Decref()</c>: through the facet's own vtable when the game made it.</summary>
        private void ReleaseFacet(long facet, Action then)
        {
            if (facet == 0)
            {
                then();
                return;
            }

            if (IsHostObject(facet))
            {
                if (Decref(facet) != 0)
                {
                    DestroyHostFacet(facet);
                    _emulator.FreeHeap(facet);
                }

                then();
                return;
            }

            CallGuest(Slot(facet, 2), [facet], dead =>
            {
                if (dead == 0)
                {
                    then();
                    return;
                }

                CallGuest(Slot(dead, 0), [dead, 1], _ => then());
            });
        }

        private long IdOf(long id)
        {
            long value = _emulator.ReadUInt32(id);
            if (value == 0)
            {
                value = ++_idCount;
                _emulator.WriteUInt32(id, (uint)value);
            }

            return value;
        }

        // ------------------------------------------------------------------------------
        // facet references
        // ------------------------------------------------------------------------------

        /// <summary>facet::_Incref: a reference unless the count is saturated.</summary>
        private void Incref(long facet)
        {
            if (facet == 0)
            {
                return;
            }

            uint refs = _emulator.ReadUInt32(facet + Refs);
            if (refs != uint.MaxValue)
            {
                _emulator.WriteUInt32(facet + Refs, refs + 1);
            }
        }

        /// <summary>facet::_Decref: drop a reference; answers the facet when none is left, else 0.</summary>
        private long Decref(long facet)
        {
            if (facet == 0)
            {
                return 0;
            }

            uint refs = _emulator.ReadUInt32(facet + Refs);
            if (refs > 0 && refs != uint.MaxValue)
            {
                refs--;
                _emulator.WriteUInt32(facet + Refs, refs);
            }

            return refs == 0 ? facet : 0;
        }

        private bool IsHostObject(long facet) => _hostVtables.Contains(_emulator.ReadUInt32(facet));

        /// <summary>The destructor body of one of this library's facets (not freeing the object itself).</summary>
        private void DestroyHostFacet(long facet)
        {
            long vtable = _emulator.ReadUInt32(facet);
            if (vtable == _vtables.GetValueOrDefault("ctype<char>"))
            {
                TidyCtype(facet);
            }
        }

        // ------------------------------------------------------------------------------
        // vtables
        // ------------------------------------------------------------------------------

        private long Vtable(string name)
        {
            if (_vtables.TryGetValue(name, out long existing))
            {
                return existing;
            }

            List<(string Slot, Action Body)> slots =
            [
                ("dtor", DeletingDestructor),
                ("_Incref", () => { Incref(_frame.Arg(0)); _frame.Return(0); }),
                ("_Decref", () => _frame.Return(Decref(_frame.Arg(0)))),
            ];

            switch (name)
            {
                case "codecvt_base":
                    slots.Add(("do_always_noconv", () => _frame.Return(0)));
                    slots.Add(("do_max_length", () => _frame.Return(1)));
                    slots.Add(("do_encoding", () => _frame.Return(1)));
                    break;
                case "codecvt<char>":
                    slots.Add(("do_always_noconv", () => _frame.Return(1)));
                    slots.Add(("do_max_length", () => _frame.Return(1)));
                    slots.Add(("do_encoding", () => _frame.Return(1)));
                    slots.Add(("do_in", NarrowCodecvtNoconv));
                    slots.Add(("do_out", NarrowCodecvtNoconv));
                    slots.Add(("do_unshift", NarrowCodecvtUnshift));
                    slots.Add(("do_length", () => CodecvtLength(wide: false)));
                    break;
                case "codecvt<wchar_t>":
                    slots.Add(("do_always_noconv", () => _frame.Return(0)));
                    slots.Add(("do_max_length", () => _frame.Return(5)));
                    slots.Add(("do_encoding", () => _frame.Return(0)));
                    slots.Add(("do_in", WideCodecvtIn));
                    slots.Add(("do_out", WideCodecvtOut));
                    slots.Add(("do_unshift", WideCodecvtUnshift));
                    slots.Add(("do_length", () => CodecvtLength(wide: true)));
                    break;
                case "ctype<char>":
                    slots.Add(("do_tolower(range)", () => CaseRange(wide: false, upper: false)));
                    slots.Add(("do_tolower", () => _frame.Return(ToLowerC((int)(_frame.Arg(1) & 0xFF)))));
                    slots.Add(("do_toupper(range)", () => CaseRange(wide: false, upper: true)));
                    slots.Add(("do_toupper", () => _frame.Return(ToUpperC((int)(_frame.Arg(1) & 0xFF)))));
                    slots.Add(("do_widen(range)", () => CopyRange(1, 1, _frame.Arg(1), _frame.Arg(2), _frame.Arg(3), 0)));
                    slots.Add(("do_widen", () => _frame.Return(_frame.Arg(1) & 0xFF)));
                    slots.Add(("do_narrow(range)", () => CopyRange(1, 1, _frame.Arg(1), _frame.Arg(2), _frame.Arg(4), 0)));
                    slots.Add(("do_narrow", () => _frame.Return(_frame.Arg(1) & 0xFF)));
                    break;
                case "ctype<wchar_t>":
                    slots.Add(("do_is(range)", WideIsRange));
                    slots.Add(("do_is", () => _frame.Return((WideMask((int)(_frame.Arg(2) & 0xFFFF)) & (int)(_frame.Arg(1) & 0xFFFF)) != 0 ? 1 : 0)));
                    slots.Add(("do_scan_is", () => WideScan(match: true)));
                    slots.Add(("do_scan_not", () => WideScan(match: false)));
                    slots.Add(("do_tolower(range)", () => CaseRange(wide: true, upper: false)));
                    slots.Add(("do_tolower", () => _frame.Return(ToLowerC((int)(_frame.Arg(1) & 0xFFFF)))));
                    slots.Add(("do_toupper(range)", () => CaseRange(wide: true, upper: true)));
                    slots.Add(("do_toupper", () => _frame.Return(ToUpperC((int)(_frame.Arg(1) & 0xFFFF)))));
                    slots.Add(("do_widen(range)", () => CopyRange(1, 2, _frame.Arg(1), _frame.Arg(2), _frame.Arg(3), 0)));
                    slots.Add(("do_widen", () => _frame.Return(_frame.Arg(1) & 0xFF)));
                    slots.Add(("do_narrow(range)", () => CopyRange(2, 1, _frame.Arg(1), _frame.Arg(2), _frame.Arg(4), (int)(_frame.Arg(3) & 0xFF))));
                    slots.Add(("do_narrow", () =>
                    {
                        int wc = (int)(_frame.Arg(1) & 0xFFFF);
                        _frame.Return(wc < 0x100 ? wc : _frame.Arg(2) & 0xFF);
                    }));
                    break;
                case "numpunct<char>":
                case "numpunct<wchar_t>":
                    bool wideP = name.Contains("wchar_t", StringComparison.Ordinal);
                    slots.Add(("do_decimal_point", () => _frame.Return('.')));
                    slots.Add(("do_thousands_sep", () => _frame.Return(',')));
                    slots.Add(("do_grouping", () => ReturnString(_frame.Arg(1), "", wide: false)));
                    slots.Add(("do_falsename", () => ReturnString(_frame.Arg(1), "false", wideP)));
                    slots.Add(("do_truename", () => ReturnString(_frame.Arg(1), "true", wideP)));
                    break;
                case "collate<char>":
                case "collate<wchar_t>":
                    int size = name.Contains("wchar_t", StringComparison.Ordinal) ? 2 : 1;
                    slots.Add(("do_compare", () => _frame.Return(unchecked((uint)Collate(size, _frame.Arg(1), _frame.Arg(2), _frame.Arg(3), _frame.Arg(4))))));
                    slots.Add(("do_transform", () => Transform(size)));
                    slots.Add(("do_hash", () => _frame.Return(Hash(size, _frame.Arg(1), _frame.Arg(2)))));
                    break;
                case "time_put<char>":
                    slots.Add(("do_put", TimeDoPut));
                    break;
            }

            long table = Allocate(slots.Count * 4);
            for (int i = 0; i < slots.Count; i++)
            {
                long trap = _emulator.RegisterVtableMethod($"{name}::{slots[i].Slot}", slots[i].Body);
                _emulator.WriteUInt32(table + (i * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _vtables[name] = table;
            _hostVtables.Add(table);
            return table;
        }

        /// <summary>
        /// The scalar deleting destructor every facet vtable here shares: run the class's
        /// destructor body, then free the object if bit 0 of the flag asks for it.
        /// </summary>
        private void DeletingDestructor()
        {
            long self = _frame.Arg(0);
            bool free = (_frame.Arg(1) & 1) != 0;
            long returnTo = _emulator.ReturnAddress;
            if (_emulator.ReadUInt32(self) == _vtables.GetValueOrDefault("_Locimp"))
            {
                DestroyLocimp(self, () =>
                {
                    if (free)
                    {
                        _emulator.FreeHeap(self);
                    }

                    Finish(self, returnTo);
                });
                return;
            }

            DestroyHostFacet(self);
            if (free)
            {
                _emulator.FreeHeap(self);
            }

            _frame.Return(self);
        }

        private static long Slot(long self, int slot, ArmEmulator emulator) => emulator.ReadUInt32(emulator.ReadUInt32(self) + (slot * 4));

        private long Slot(long self, int slot) => Slot(self, slot, _emulator);

        /// <summary>A non-virtual member that only forwards to a virtual: tail-call the object's own slot.</summary>
        private void TailCall(int slot) => _emulator.ContinueAt(Slot(_frame.Arg(0), slot));

        private void CallGuest(long function, long[] arguments, Action<long> then)
            => _emulator.CallEmulated("locale callback", function, arguments, onReturn: () => then(_frame.Arg(0)), recycle: true);

        private void Finish(long value, long returnTo)
        {
            _frame.Return(value);
            _emulator.ContinueAt(returnTo);
        }

        private long Allocate(long size)
        {
            long p = _emulator.AllocateHeap(size);
            _emulator.WriteMemory(p, new byte[size]);
            return p;
        }

        /// <summary>A NUL-terminated host string in guest memory, made once per text.</summary>
        private long StaticString(string text, bool wide)
        {
            string key = (wide ? "W:" : "N:") + text;
            if (!_strings.TryGetValue(key, out long p))
            {
                byte[] bytes = wide ? Encoding.Unicode.GetBytes(text + "\0") : Encoding.Latin1.GetBytes(text + "\0");
                p = _emulator.AllocateHeap(bytes.Length);
                _emulator.WriteMemory(p, bytes);
                _strings[key] = p;
            }

            return p;
        }

        // ------------------------------------------------------------------------------
        // _Yarn
        // ------------------------------------------------------------------------------

        /// <summary>_Yarn&lt;char&gt; = text: a malloc'd copy, the old one freed. Yarn is { T* _Myptr; T _Nul; }.</summary>
        private void SetYarn(long yarn, string? text, bool wide = false)
        {
            FreeYarn(yarn);
            if (text is null)
            {
                return;
            }

            byte[] bytes = wide ? Encoding.Unicode.GetBytes(text + "\0") : Encoding.Latin1.GetBytes(text + "\0");
            long p = _emulator.AllocateHeap(bytes.Length);
            _emulator.WriteMemory(p, bytes);
            _emulator.WriteUInt32(yarn, (uint)p);
        }

        private void FreeYarn(long yarn)
        {
            long p = _emulator.ReadUInt32(yarn);
            if (p != 0)
            {
                _emulator.FreeHeap(p);
            }

            _emulator.WriteUInt32(yarn, 0);
            _emulator.WriteUInt32(yarn + 4, 0);
        }

        private string YarnText(long yarn)
        {
            long p = _emulator.ReadUInt32(yarn);
            return p == 0 ? string.Empty : _frame.ReadNarrowString(p);
        }

        // ------------------------------------------------------------------------------
        // ctype
        // ------------------------------------------------------------------------------

        private void RegisterCtype(Dictionary<string, Action> h)
        {
            const string C = "?$ctype@D@std@@";
            const string W = "?$ctype@_W@std@@";

            h[$"?_Getcat@{C}SAIPAPBVfacet@locale@2@PBV42@@Z"] = () => Getcat(LcCtype, NewCtypeChar);
            h[$"?_Getcat@{W}SAIPAPBVfacet@locale@2@PBV42@@Z"] = () => Getcat(LcCtype, () => NewFacet("ctype<wchar_t>", 0x20));

            // ctype<char>(const mask* table = 0, bool del = false, size_t refs = 0)
            h[$"??0{C}QAA@PBF_NI@Z"] = () =>
            {
                long self = _frame.Arg(0);
                long table = _frame.Arg(1);
                bool delete = (_frame.Arg(2) & 0xFF) != 0;
                _emulator.WriteMemory(self, new byte[CtypeSize]);
                _emulator.WriteUInt32(self, (uint)Vtable("ctype<char>"));
                _emulator.WriteUInt32(self + Refs, (uint)_frame.Arg(3));
                _emulator.WriteUInt32(self + CtypeTable, (uint)(table != 0 ? table : ClassicTable()));
                _emulator.WriteUInt32(self + CtypeDelfl, table != 0 && delete ? uint.MaxValue : 0);
                _frame.Return(self);
            };
            h[$"??1{C}MAA@XZ"] = () => { TidyCtype(_frame.Arg(0)); _frame.Return(0); };
            h[$"??1{W}MAA@XZ"] = () => _frame.Return(0);
            h[$"??0{W}QAA@I@Z"] = () =>
            {
                long self = _frame.Arg(0);
                _emulator.WriteMemory(self, new byte[0x20]);
                _emulator.WriteUInt32(self, (uint)Vtable("ctype<wchar_t>"));
                _emulator.WriteUInt32(self + Refs, (uint)_frame.Arg(1));
                _frame.Return(self);
            };

            // ctype<char>'s own virtuals, exported because a derived game class's vtable names them.
            h[$"?do_tolower@{C}MBADD@Z"] = () => _frame.Return(ToLowerC((int)(_frame.Arg(1) & 0xFF)));
            h[$"?do_tolower@{C}MBAPBDPADPBD@Z"] = () => CaseRange(wide: false, upper: false);
            h[$"?do_toupper@{C}MBADD@Z"] = () => _frame.Return(ToUpperC((int)(_frame.Arg(1) & 0xFF)));
            h[$"?do_toupper@{C}MBAPBDPADPBD@Z"] = () => CaseRange(wide: false, upper: true);
            h[$"?do_widen@{C}MBADD@Z"] = () => _frame.Return(_frame.Arg(1) & 0xFF);
            h[$"?do_widen@{C}MBAPBDPBD0PAD@Z"] = () => CopyRange(1, 1, _frame.Arg(1), _frame.Arg(2), _frame.Arg(3), 0);
            h[$"?do_narrow@{C}MBADDD@Z"] = () => _frame.Return(_frame.Arg(1) & 0xFF);
            h[$"?do_narrow@{C}MBAPBDPBD0DPAD@Z"] = () => CopyRange(1, 1, _frame.Arg(1), _frame.Arg(2), _frame.Arg(4), 0);

            // The public members forward to the virtuals (slots as in Vtable()).
            h[$"?tolower@{C}QBADD@Z"] = () => TailCall(4);
            h[$"?tolower@{C}QBAPBDPADPBD@Z"] = () => TailCall(3);
            h[$"?toupper@{C}QBADD@Z"] = () => TailCall(6);
            h[$"?toupper@{C}QBAPBDPADPBD@Z"] = () => TailCall(5);
            h[$"?widen@{C}QBADD@Z"] = () => TailCall(8);
            h[$"?widen@{C}QBAPBDPBD0PAD@Z"] = () => TailCall(7);
            h[$"?narrow@{C}QBADDD@Z"] = () => TailCall(10);
            h[$"?narrow@{C}QBAPBDPBD0DPAD@Z"] = () => TailCall(9);

            // ctype<char>::is reads the table directly; it is not virtual.
            h[$"?is@{C}QBA_NFD@Z"] = () =>
            {
                int mask = (int)(_frame.Arg(1) & 0xFFFF);
                int c = (int)(_frame.Arg(2) & 0xFF);
                _frame.Return((TableEntry(_frame.Arg(0), c) & mask) != 0 ? 1 : 0);
            };
            h[$"?is@{C}QBAPBDPBD0PAF@Z"] = () =>
            {
                long self = _frame.Arg(0), first = _frame.Arg(1), last = _frame.Arg(2), dest = _frame.Arg(3);
                for (long p = first; p < last; p++, dest += 2)
                {
                    int entry = TableEntry(self, _emulator.ReadMemory(p, 1)[0]);
                    _emulator.WriteMemory(dest, [(byte)entry, (byte)(entry >> 8)]);
                }

                _frame.Return(last);
            };
            h[$"?scan_is@{C}QBAPBDFPBD0@Z"] = () => NarrowScan(match: true);
            h[$"?scan_not@{C}QBAPBDFPBD0@Z"] = () => NarrowScan(match: false);
            h[$"?table@{C}IBAPBFXZ"] = () => _frame.Return(_emulator.ReadUInt32(_frame.Arg(0) + CtypeTable));
            h[$"?classic_table@{C}KAPBFXZ"] = () => _frame.Return(ClassicTable());
            h[$"?classic_table@{C}SAPBFXZ"] = () => _frame.Return(ClassicTable());
            h[$"?_Init@{C}IAAXABV_Locinfo@2@@Z"] = () =>
            {
                long self = _frame.Arg(0);
                _emulator.WriteUInt32(self + CtypeTable, (uint)ClassicTable());
                _emulator.WriteUInt32(self + CtypeDelfl, 0);
                _frame.Return(0);
            };

            // ctype<wchar_t>: every public member forwards to its virtual.
            h[$"?is@{W}QBA_NF_W@Z"] = () => TailCall(4);
            h[$"?is@{W}QBAPB_WPB_W0PAF@Z"] = () => TailCall(3);
            h[$"?scan_is@{W}QBAPB_WFPB_W0@Z"] = () => TailCall(5);
            h[$"?scan_not@{W}QBAPB_WFPB_W0@Z"] = () => TailCall(6);
            h[$"?tolower@{W}QBA_W_W@Z"] = () => TailCall(8);
            h[$"?tolower@{W}QBAPB_WPA_WPB_W@Z"] = () => TailCall(7);
            h[$"?toupper@{W}QBA_W_W@Z"] = () => TailCall(10);
            h[$"?toupper@{W}QBAPB_WPA_WPB_W@Z"] = () => TailCall(9);
            h[$"?widen@{W}QBA_WD@Z"] = () => TailCall(12);
            h[$"?widen@{W}QBAPBDPBD0PA_W@Z"] = () => TailCall(11);
            h[$"?narrow@{W}QBAD_WD@Z"] = () => TailCall(14);
            h[$"?narrow@{W}QBAPB_WPB_W0DPAD@Z"] = () => TailCall(13);
        }

        private long NewCtypeChar()
        {
            long self = NewFacet("ctype<char>", CtypeSize);
            _emulator.WriteUInt32(self + CtypeTable, (uint)ClassicTable());
            return self;
        }

        private long NewFacet(string vtable, int size)
        {
            long self = Allocate(size);
            _emulator.WriteUInt32(self, (uint)Vtable(vtable));
            return self;
        }

        /// <summary>static size_t _Getcat(const facet** ppf, const locale* ploc): make the "C" facet if asked to.</summary>
        private void Getcat(int category, Func<long> make)
        {
            long ppf = _frame.Arg(0);
            if (ppf != 0 && _emulator.ReadUInt32(ppf) == 0)
            {
                long facet = make();
                _emulator.WriteUInt32(ppf, (uint)facet);
            }

            _frame.Return(category);
        }

        /// <summary>ctype&lt;char&gt;::_Tidy: free a table the facet owns (_Delfl &gt; 0 malloc, &lt; 0 new[]).</summary>
        private void TidyCtype(long self)
        {
            int delfl = unchecked((int)_emulator.ReadUInt32(self + CtypeDelfl));
            long table = _emulator.ReadUInt32(self + CtypeTable);
            if (delfl != 0 && table != 0 && table != _classicTable)
            {
                _emulator.FreeHeap(table);
            }

            _emulator.WriteUInt32(self + CtypeDelfl, 0);
            long name = _emulator.ReadUInt32(self + CtypeName);
            if (name != 0)
            {
                _emulator.FreeHeap(name);
                _emulator.WriteUInt32(self + CtypeName, 0);
            }
        }

        private int TableEntry(long self, int c)
        {
            long table = _emulator.ReadUInt32(self + CtypeTable);
            if (table == 0)
            {
                table = ClassicTable();
            }

            byte[] b = _emulator.ReadMemory(table + (c * 2), 2);
            return b[0] | (b[1] << 8);
        }

        /// <summary>
        /// The "C" locale's classification table, 256 masks: what the CRT's _ctype table holds
        /// for 0-255 with no locale set, which is what VC11's classic_table() hands back.
        /// </summary>
        public long ClassicTable()
        {
            if (_classicTable != 0)
            {
                return _classicTable;
            }

            byte[] bytes = new byte[512];
            for (int c = 0; c < 256; c++)
            {
                int mask = NarrowMask(c);
                bytes[c * 2] = (byte)mask;
                bytes[(c * 2) + 1] = (byte)(mask >> 8);
            }

            _classicTable = _emulator.AllocateHeap(bytes.Length);
            _emulator.WriteMemory(_classicTable, bytes);
            return _classicTable;
        }

        /// <summary>The "C" locale mask of a byte: ASCII only, nothing above 0x7F.</summary>
        public static int NarrowMask(int c)
        {
            if (c is < 0 or > 0x7F)
            {
                return 0;
            }

            int mask = 0;
            if (c is >= 'A' and <= 'Z') mask |= Upper | Alpha;
            if (c is >= 'a' and <= 'z') mask |= Lower | Alpha;
            if (c is >= '0' and <= '9') mask |= Digit;
            if (c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F') mask |= Xdigit;
            if (c is ' ' or (>= 0x09 and <= 0x0D)) mask |= Space;
            if (c is ' ' or '\t') mask |= Blank;
            if (c < 0x20 || c == 0x7F) mask |= Cntrl;
            if (c is > 0x20 and < 0x7F && (mask & (Alpha | Digit)) == 0) mask |= Punct;
            return mask;
        }

        /// <summary>
        /// The mask of a wide character. The CRT classifies wide characters from Unicode in every
        /// locale (iswalpha(L'é') is true even in "C"), so above ASCII this is Unicode's answer.
        /// </summary>
        public static int WideMask(int wc)
        {
            if (wc < 0x80)
            {
                return NarrowMask(wc);
            }

            char ch = (char)wc;
            int mask = 0;
            switch (char.GetUnicodeCategory(ch))
            {
                case UnicodeCategory.UppercaseLetter:
                    mask |= Upper | Alpha;
                    break;
                case UnicodeCategory.LowercaseLetter:
                    mask |= Lower | Alpha;
                    break;
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                    mask |= Alpha;
                    break;
                case UnicodeCategory.DecimalDigitNumber:
                    mask |= Digit;
                    break;
                case UnicodeCategory.SpaceSeparator:
                    mask |= Space | Blank;
                    break;
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    mask |= Space;
                    break;
                case UnicodeCategory.Control:
                    mask |= Cntrl;
                    if (char.IsWhiteSpace(ch)) mask |= Space;
                    break;
                case UnicodeCategory.ConnectorPunctuation:
                case UnicodeCategory.DashPunctuation:
                case UnicodeCategory.OpenPunctuation:
                case UnicodeCategory.ClosePunctuation:
                case UnicodeCategory.InitialQuotePunctuation:
                case UnicodeCategory.FinalQuotePunctuation:
                case UnicodeCategory.OtherPunctuation:
                case UnicodeCategory.MathSymbol:
                case UnicodeCategory.CurrencySymbol:
                case UnicodeCategory.ModifierSymbol:
                case UnicodeCategory.OtherSymbol:
                    mask |= Punct;
                    break;
            }

            return mask;
        }

        /// <summary>tolower in the "C" locale: A-Z only, for narrow and wide alike.</summary>
        public static int ToLowerC(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

        public static int ToUpperC(int c) => c is >= 'a' and <= 'z' ? c - 32 : c;

        /// <summary>do_tolower/do_toupper(first, last): in place, answers last.</summary>
        private void CaseRange(bool wide, bool upper)
        {
            long first = _frame.Arg(1), last = _frame.Arg(2);
            int size = wide ? 2 : 1;
            for (long p = first; p < last; p += size)
            {
                byte[] b = _emulator.ReadMemory(p, size);
                int c = wide ? b[0] | (b[1] << 8) : b[0];
                int mapped = upper ? ToUpperC(c) : ToLowerC(c);
                if (mapped != c)
                {
                    _emulator.WriteMemory(p, wide ? [(byte)mapped, (byte)(mapped >> 8)] : [(byte)mapped]);
                }
            }

            _frame.Return(last);
        }

        /// <summary>
        /// The widen/narrow ranges: [first, last) of one width into dest of another, answering
        /// last. Narrowing a wide character with no single-byte form writes the default.
        /// </summary>
        private void CopyRange(int fromSize, int toSize, long first, long last, long dest, int defaultChar)
        {
            for (long p = first; p < last; p += fromSize, dest += toSize)
            {
                byte[] b = _emulator.ReadMemory(p, fromSize);
                int c = fromSize == 2 ? b[0] | (b[1] << 8) : b[0];
                if (toSize == 1 && c > 0xFF)
                {
                    c = defaultChar;
                }

                _emulator.WriteMemory(dest, toSize == 2 ? [(byte)c, (byte)(c >> 8)] : [(byte)c]);
            }

            _frame.Return(last);
        }

        private void WideIsRange()
        {
            long first = _frame.Arg(1), last = _frame.Arg(2), dest = _frame.Arg(3);
            for (long p = first; p < last; p += 2, dest += 2)
            {
                byte[] b = _emulator.ReadMemory(p, 2);
                int mask = WideMask(b[0] | (b[1] << 8));
                _emulator.WriteMemory(dest, [(byte)mask, (byte)(mask >> 8)]);
            }

            _frame.Return(last);
        }

        /// <summary>do_scan_is / do_scan_not(mask, first, last): the first element that does / does not match.</summary>
        private void WideScan(bool match)
        {
            int mask = (int)(_frame.Arg(1) & 0xFFFF);
            long first = _frame.Arg(2), last = _frame.Arg(3);
            long p = first;
            for (; p < last; p += 2)
            {
                byte[] b = _emulator.ReadMemory(p, 2);
                if (((WideMask(b[0] | (b[1] << 8)) & mask) != 0) == match)
                {
                    break;
                }
            }

            _frame.Return(p);
        }

        private void NarrowScan(bool match)
        {
            long self = _frame.Arg(0);
            int mask = (int)(_frame.Arg(1) & 0xFFFF);
            long first = _frame.Arg(2), last = _frame.Arg(3);
            long p = first;
            for (; p < last; p++)
            {
                if (((TableEntry(self, _emulator.ReadMemory(p, 1)[0]) & mask) != 0) == match)
                {
                    break;
                }
            }

            _frame.Return(p);
        }

        // ------------------------------------------------------------------------------
        // codecvt
        // ------------------------------------------------------------------------------

        private void RegisterCodecvt(Dictionary<string, Action> h)
        {
            const string N = "?$codecvt@DDH@std@@";
            const string W = "?$codecvt@_WDH@std@@";

            h[$"?_Getcat@{N}SAIPAPBVfacet@locale@2@PBV42@@Z"] = () => Getcat(LcCtype, () => NewFacet("codecvt<char>", 8));
            h[$"?_Getcat@{W}SAIPAPBVfacet@locale@2@PBV42@@Z"] = () => Getcat(LcCtype, () => NewFacet("codecvt<wchar_t>", 0x10));

            // codecvt_base's public members and codecvt's in/out/unshift/length forward to the
            // virtuals: slots 3-5 are codecvt_base's, 6-9 codecvt's own.
            h["?always_noconv@codecvt_base@std@@QBA_NXZ"] = () => TailCall(3);
            h["?max_length@codecvt_base@std@@QBAHXZ"] = () => TailCall(4);
            h["?encoding@codecvt_base@std@@QBAHXZ"] = () => TailCall(5);
            h["?do_always_noconv@codecvt_base@std@@MBA_NXZ"] = () => _frame.Return(0);
            h["?do_max_length@codecvt_base@std@@MBAHXZ"] = () => _frame.Return(1);
            h["?do_encoding@codecvt_base@std@@MBAHXZ"] = () => _frame.Return(1);
            foreach (string t in new[] { N, W })
            {
                string elem = t == N ? "D" : "_W";
                h[$"?in@{t}QBAHAAHPBD1AAPBDP{AElem(elem)}3AAP{AElem(elem)}@Z"] = () => TailCall(6);
                h[$"?out@{t}QBAHAAHPB{elem}1AAPB{elem}PAD3AAPAD@Z"] = () => TailCall(7);
                h[$"?unshift@{t}QBAHAAHPAD1AAPAD@Z"] = () => TailCall(8);
                h[$"?length@{t}QBAHABHPBD1I@Z"] = () => TailCall(9);
            }

            // codecvt<char,char,int>'s own virtuals: the identity conversion.
            h[$"?do_always_noconv@{N}MBA_NXZ"] = () => _frame.Return(1);
            h[$"?do_in@{N}MBAHAAHPBD1AAPBDPAD3AAPAD@Z"] = NarrowCodecvtNoconv;
            h[$"?do_out@{N}MBAHAAHPBD1AAPBDPAD3AAPAD@Z"] = NarrowCodecvtNoconv;
            h[$"?do_unshift@{N}MBAHAAHPAD1AAPAD@Z"] = NarrowCodecvtUnshift;
            h[$"?do_length@{N}MBAHABHPBD1I@Z"] = () => CodecvtLength(wide: false);
            h[$"??0{N}QAA@I@Z"] = () => ConstructFacet("codecvt<char>", 8);
            h[$"??1{N}MAA@XZ"] = () => _frame.Return(0);

            // codecvt<wchar_t,char,int>: one byte per character in the "C" locale.
            h[$"??0{W}QAA@I@Z"] = () => ConstructFacet("codecvt<wchar_t>", 0x10);
            h[$"??1{W}MAA@XZ"] = () => _frame.Return(0);
            h[$"?do_always_noconv@{W}MBA_NXZ"] = () => _frame.Return(0);
            h[$"?do_max_length@{W}MBAHXZ"] = () => _frame.Return(5);
            h[$"?do_encoding@{W}MBAHXZ"] = () => _frame.Return(0);
            h[$"?do_in@{W}MBAHAAHPBD1AAPBDPA_W3AAPA_W@Z"] = WideCodecvtIn;
            h[$"?do_out@{W}MBAHAAHPB_W1AAPB_WPAD3AAPAD@Z"] = WideCodecvtOut;
            h[$"?do_unshift@{W}MBAHAAHPAD1AAPAD@Z"] = WideCodecvtUnshift;
            h[$"?do_length@{W}MBAHABHPBD1I@Z"] = () => CodecvtLength(wide: true);
        }

        private static string AElem(string elem) => "A" + elem;

        /// <summary>A facet constructor: (this, refs). Sets our vtable; a derived class replaces it.</summary>
        private void ConstructFacet(string vtable, int size)
        {
            long self = _frame.Arg(0);
            _emulator.WriteMemory(self, new byte[size]);
            _emulator.WriteUInt32(self, (uint)Vtable(vtable));
            _emulator.WriteUInt32(self + Refs, (uint)_frame.Arg(1));
            _frame.Return(self);
        }

        /// <summary>
        /// codecvt&lt;char,char,int&gt;::do_in/do_out(state, from, from_end, from_next&amp;, to,
        /// to_end, to_next&amp;): nothing to convert, so from_next = from, to_next = to, noconv.
        /// </summary>
        private void NarrowCodecvtNoconv()
        {
            _emulator.WriteUInt32(_frame.Arg(4), (uint)_frame.Arg(2));
            _emulator.WriteUInt32(_frame.Arg(7), (uint)_frame.Arg(5));
            _frame.Return(Noconv);
        }

        /// <summary>do_unshift(state, to, to_end, to_next&amp;): to_next = to.</summary>
        private void NarrowCodecvtUnshift()
        {
            _emulator.WriteUInt32(_frame.Arg(4), (uint)_frame.Arg(2));
            _frame.Return(Noconv);
        }

        /// <summary>
        /// codecvt&lt;wchar_t,char&gt;::do_unshift: the "C" locale's encoding has no shift
        /// state, so there is nothing to write: to_next = to and ok.
        /// </summary>
        private void WideCodecvtUnshift()
        {
            _emulator.WriteUInt32(_frame.Arg(4), (uint)_frame.Arg(2));
            _frame.Return(Ok);
        }

        /// <summary>do_in: bytes to wide characters, one for one; partial when the output is full.</summary>
        private void WideCodecvtIn()
        {
            long from = _frame.Arg(2), fromEnd = _frame.Arg(3), fromNext = _frame.Arg(4);
            long to = _frame.Arg(5), toEnd = _frame.Arg(6), toNext = _frame.Arg(7);
            long src = from, dst = to;
            while (src < fromEnd && dst + 2 <= toEnd)
            {
                byte c = _emulator.ReadMemory(src, 1)[0];
                _emulator.WriteMemory(dst, [c, 0]);
                src++;
                dst += 2;
            }

            _emulator.WriteUInt32(fromNext, (uint)src);
            _emulator.WriteUInt32(toNext, (uint)dst);
            _frame.Return(src == fromEnd ? Ok : Partial);
        }

        /// <summary>do_out: wide characters to bytes; error at the first one with no single-byte form.</summary>
        private void WideCodecvtOut()
        {
            long from = _frame.Arg(2), fromEnd = _frame.Arg(3), fromNext = _frame.Arg(4);
            long to = _frame.Arg(5), toEnd = _frame.Arg(6), toNext = _frame.Arg(7);
            long src = from, dst = to;
            int result = Ok;
            while (src + 2 <= fromEnd)
            {
                if (dst >= toEnd)
                {
                    result = Partial;
                    break;
                }

                byte[] b = _emulator.ReadMemory(src, 2);
                int wc = b[0] | (b[1] << 8);
                if (wc > 0xFF)
                {
                    result = Error;
                    break;
                }

                _emulator.WriteMemory(dst, [(byte)wc]);
                src += 2;
                dst++;
            }

            _emulator.WriteUInt32(fromNext, (uint)src);
            _emulator.WriteUInt32(toNext, (uint)dst);
            _frame.Return(result);
        }

        /// <summary>do_length(state, from, from_end, max): bytes making at most max characters - min(max, length).</summary>
        private void CodecvtLength(bool wide)
        {
            long from = _frame.Arg(2), fromEnd = _frame.Arg(3);
            long max = _frame.Arg(4);
            _ = wide;
            _frame.Return(Math.Max(0, Math.Min(max, fromEnd - from)));
        }

        // ------------------------------------------------------------------------------
        // numpunct, collate, time_put
        // ------------------------------------------------------------------------------

        private void RegisterOtherFacets(Dictionary<string, Action> h)
        {
            foreach ((string elem, bool wide) in new[] { ("D", false), ("_W", true) })
            {
                string np = $"?$numpunct@{elem}@std@@";
                string co = $"?$collate@{elem}@std@@";
                string typeName = wide ? "wchar_t" : "char";
                string str = $"?AV?$basic_string@{elem}U?$char_traits@{elem}@std@@V?$allocator@{elem}@2@@2@";

                h[$"?_Getcat@{np}SAIPAPBVfacet@locale@2@PBV42@@Z"] = () => Getcat(LcNumeric, () => NewFacet($"numpunct<{typeName}>", 0x18));
                h[$"?_Getcat@{co}SAIPAPBVfacet@locale@2@PBV42@@Z"] = () => Getcat(LcCollate, () => NewFacet($"collate<{typeName}>", 0x10));
                h[$"??0{np}QAA@I@Z"] = () => ConstructFacet($"numpunct<{typeName}>", 0x18);
                h[$"??1{np}MAA@XZ"] = () => _frame.Return(0);
                h[$"??0{co}QAA@I@Z"] = () => ConstructFacet($"collate<{typeName}>", 0x10);
                h[$"??1{co}MAA@XZ"] = () => _frame.Return(0);

                // numpunct's members forward to do_decimal_point (3) ... do_truename (7).
                h[$"?decimal_point@{np}QBA{elem}XZ"] = () => TailCall(3);
                h[$"?thousands_sep@{np}QBA{elem}XZ"] = () => TailCall(4);
                h[$"?grouping@{np}QBA?AV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@2@XZ"] = () => TailCall(5);
                h[$"?falsename@{np}QBA{str}XZ"] = () => TailCall(6);
                h[$"?truename@{np}QBA{str}XZ"] = () => TailCall(7);

                // collate's members forward to do_compare (3), do_transform (4), do_hash (5).
                h[$"?compare@{co}QBAHPB{elem}000@Z"] = () => TailCall(3);
                h[$"?transform@{co}QBA{str}PB{elem}0@Z"] = () => TailCall(4);
                h[$"?hash@{co}QBAJPB{elem}0@Z"] = () => TailCall(5);
            }

            const string Tp = "?$time_put@DV?$ostreambuf_iterator@DU?$char_traits@D@std@@@std@@@std@@";
            h[$"?_Getcat@{Tp}SAIPAPBVfacet@locale@2@PBV42@@Z"] = () => Getcat(LcTime, () => NewFacet("time_put<char>", 0xC));
            h[$"?put@{Tp}QBA?AV?$ostreambuf_iterator@DU?$char_traits@D@std@@@2@V32@AAVios_base@2@DPBUtm@@PBD3@Z"] = TimePut;
            h[$"?put@{Tp}QBA?AV?$ostreambuf_iterator@DU?$char_traits@D@std@@@2@V32@AAVios_base@2@DPBUtm@@DD@Z"] = TimeDoPut;
        }

        /// <summary>
        /// Writes an MSVC 2012 std::string / std::wstring by value into <paramref name="result"/>:
        /// a 16-byte small buffer (15 chars or 7 wide chars), then size and capacity; longer
        /// text goes in an operator-new block the string's own destructor frees.
        /// </summary>
        public void WriteStdString(long result, string text, bool wide)
        {
            int size = wide ? 2 : 1;
            int inline = (16 / size) - 1;
            byte[] bytes = wide ? Encoding.Unicode.GetBytes(text + "\0") : Encoding.Latin1.GetBytes(text + "\0");
            _emulator.WriteMemory(result, new byte[16]);
            if (text.Length <= inline)
            {
                _emulator.WriteMemory(result, bytes);
                _emulator.WriteUInt32(result + 20, (uint)inline);
            }
            else
            {
                long block = _emulator.AllocateHeap(bytes.Length);
                _emulator.WriteMemory(block, bytes);
                _emulator.WriteUInt32(result, (uint)block);
                _emulator.WriteUInt32(result + 20, (uint)text.Length);
            }

            _emulator.WriteUInt32(result + 16, (uint)text.Length);
        }

        /// <summary>A virtual returning a string by value: this in r0, the hidden result in r1.</summary>
        private void ReturnString(long result, string text, bool wide)
        {
            WriteStdString(result, text, wide);
            _frame.Return(result);
        }

        /// <summary>
        /// The "C" locale collation of [first1, last1) and [first2, last2): element values compared
        /// as unsigned, a shorter prefix ordering first. Answers -1, 0 or 1, as _Strcoll does.
        /// </summary>
        private int Collate(int size, long first1, long last1, long first2, long last2)
        {
            int n1 = (int)Math.Max(0, (last1 - first1) / size), n2 = (int)Math.Max(0, (last2 - first2) / size);
            byte[] a = n1 == 0 ? [] : _emulator.ReadMemory(first1, n1 * size);
            byte[] b = n2 == 0 ? [] : _emulator.ReadMemory(first2, n2 * size);
            for (int i = 0; i < Math.Min(n1, n2); i++)
            {
                int x = size == 2 ? a[i * 2] | (a[(i * 2) + 1] << 8) : a[i];
                int y = size == 2 ? b[i * 2] | (b[(i * 2) + 1] << 8) : b[i];
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }
            }

            return n1 == n2 ? 0 : n1 < n2 ? -1 : 1;
        }

        /// <summary>collate::do_hash: VC11's _Hash_seq, 32-bit FNV-1a over the bytes of the range.</summary>
        private long Hash(int size, long first, long last)
        {
            uint hash = 2166136261;
            int n = (int)Math.Max(0, last - first);
            byte[] bytes = n == 0 ? [] : _emulator.ReadMemory(first, n - (n % size));
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= 16777619;
            }

            return hash;
        }

        /// <summary>collate::do_transform(result, first, last): in "C" the key is the text itself.</summary>
        private void Transform(int size)
        {
            long result = _frame.Arg(1), first = _frame.Arg(2), last = _frame.Arg(3);
            int n = (int)Math.Max(0, (last - first) / size);
            byte[] bytes = n == 0 ? [] : _emulator.ReadMemory(first, n * size);
            string text = size == 2 ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            ReturnString(result, text, size == 2);
        }

        /// <summary>
        /// time_put::put(dest, ios_base&amp;, fill, const tm*, fmt_first, fmt_last): this in r0,
        /// the hidden ostreambuf_iterator result in r1, dest { bool failed; streambuf* } in r2:r3,
        /// then the rest on the stack. Literal text is copied; each %[E|O]x is formatted for "C".
        /// </summary>
        private void TimePut()
        {
            long result = _frame.Arg(1);
            bool failed = (_frame.Arg(2) & 0xFF) != 0;
            long sb = _frame.Arg(3);
            long tm = _frame.Arg(6);
            long first = _frame.Arg(7), last = _frame.Arg(8);
            string format = first < last ? Encoding.Latin1.GetString(_emulator.ReadMemory(first, (int)(last - first))) : string.Empty;
            WriteTime(result, failed, sb, CLocaleTime.Format(format, ReadTm(tm)));
        }

        /// <summary>do_put(dest, ios_base&amp;, fill, const tm*, specifier, modifier): one conversion.</summary>
        private void TimeDoPut()
        {
            long result = _frame.Arg(1);
            bool failed = (_frame.Arg(2) & 0xFF) != 0;
            long sb = _frame.Arg(3);
            long tm = _frame.Arg(6);
            char specifier = (char)(_frame.Arg(7) & 0xFF);
            char modifier = (char)(_frame.Arg(8) & 0xFF);
            string format = modifier == 0 ? "%" + specifier : "%" + modifier + specifier;
            WriteTime(result, failed, sb, CLocaleTime.Format(format, ReadTm(tm)));
        }

        private int[] ReadTm(long tm) =>
            tm == 0 ? new int[9] : Enumerable.Range(0, 9).Select(i => unchecked((int)_emulator.ReadUInt32(tm + (i * 4)))).ToArray();

        /// <summary>
        /// Writes through the iterator's streambuf. The text is short, so it goes through the
        /// buffer's put area while there is room and through its overflow when not.
        /// </summary>
        private void WriteTime(long result, bool failed, long sb, string text)
        {
            long returnTo = _emulator.ReturnAddress;
            void Done(bool nowFailed)
            {
                _emulator.WriteMemory(result, [nowFailed ? (byte)1 : (byte)0, 0, 0, 0]);
                _emulator.WriteUInt32(result + 4, (uint)sb);
                Finish(result, returnTo);
            }

            if (failed || sb == 0)
            {
                Done(true);
                return;
            }

            StreamPut?.Invoke(sb, Encoding.Latin1.GetBytes(text).Select(b => (int)b).ToArray(), written => Done(written < text.Length));
        }

        /// <summary>Writes narrow characters through a streambuf (set by <see cref="StreamLibrary"/>).</summary>
        internal Action<long, int[], Action<int>>? StreamPut { get; set; }

        // ------------------------------------------------------------------------------
        // _Locinfo
        // ------------------------------------------------------------------------------

        // _Locinfo: _Lockit (4), then the yarns _Days, _Months, _W_Days, _W_Months, _Oldlocname, _Newlocname.
        private const int LiOldName = 0x24, LiNewName = 0x2C, LocinfoSize = 0x34;

        private const string Days = ":Sun:Sunday:Mon:Monday:Tue:Tuesday:Wed:Wednesday:Thu:Thursday:Fri:Friday:Sat:Saturday";
        private const string Months = ":Jan:January:Feb:February:Mar:March:Apr:April:May:May:Jun:June:Jul:July:Aug:August:Sep:September:Oct:October:Nov:November:Dec:December";

        private void RegisterLocinfo(Dictionary<string, Action> h)
        {
            const string L = "_Locinfo@std@@";

            h[$"??0{L}QAA@PBD@Z"] = () => { ConstructLocinfo(_frame.Arg(0), _frame.Arg(1)); _frame.Return(_frame.Arg(0)); };
            h[$"??0{L}QAA@HPBD@Z"] = () => { ConstructLocinfo(_frame.Arg(0), _frame.Arg(2)); _frame.Return(_frame.Arg(0)); };
            h[$"?_Locinfo_ctor@{L}SAXPAV12@PBD@Z"] = () => { ConstructLocinfo(_frame.Arg(0), _frame.Arg(1)); _frame.Return(0); };
            h[$"?_Locinfo_ctor@{L}SAXPAV12@HPBD@Z"] = () => { ConstructLocinfo(_frame.Arg(0), _frame.Arg(2)); _frame.Return(0); };
            h[$"??1{L}QAA@XZ"] = () => { DestroyLocinfo(_frame.Arg(0)); _frame.Return(0); };
            h[$"?_Locinfo_dtor@{L}SAXPAV12@@Z"] = () => { DestroyLocinfo(_frame.Arg(0)); _frame.Return(0); };

            h[$"?_Getname@{L}QBAPBDXZ"] = () =>
            {
                long p = _emulator.ReadUInt32(_frame.Arg(0) + LiNewName);
                _frame.Return(p != 0 ? p : StaticString("", wide: false));
            };
            h[$"?_Getdays@{L}QBAPBDXZ"] = () => _frame.Return(StaticString(Days, wide: false));
            h[$"?_Getmonths@{L}QBAPBDXZ"] = () => _frame.Return(StaticString(Months, wide: false));
            h[$"?_W_Getdays@{L}QBAPB_WXZ"] = () => _frame.Return(StaticString(Days, wide: true));
            h[$"?_W_Getmonths@{L}QBAPB_WXZ"] = () => _frame.Return(StaticString(Months, wide: true));
            h[$"?_Getfalse@{L}QBAPBDXZ"] = () => _frame.Return(StaticString("false", wide: false));
            h[$"?_Gettrue@{L}QBAPBDXZ"] = () => _frame.Return(StaticString("true", wide: false));
            h[$"?_Getdateorder@{L}QBAHXZ"] = () => _frame.Return(2);   // time_base::mdy, the "C" order
            h[$"?_Getlconv@{L}QBAPBUlconv@@XZ"] = () => _frame.Return(Lconv());

            // The vectors by value: this in r0, the hidden result in r1. VC11's are
            // { _Page; _LocaleName } (and _Ctypevec { _Page; _Table; _Delfl; _LocaleName }):
            // page 0 and no name is the "C" locale.
            h[$"?_Getcvt@{L}QBA?AU_Cvtvec@@XZ"] = () => ReturnVector(_frame.Arg(1), [0, 0]);
            h[$"?_Getcoll@{L}QBA?AU_Collvec@@XZ"] = () => ReturnVector(_frame.Arg(1), [0, 0]);
            h[$"?_Getctype@{L}QBA?AU_Ctypevec@@XZ"] = () => ReturnVector(_frame.Arg(1), [0, ClassicTable(), 0, 0]);
            h[$"?_Gettnames@{L}QBA?AV_Timevec@2@XZ"] = () => ReturnVector(_frame.Arg(1), [0]);

            // The same as free functions: no this, so the hidden result is r0.
            h["_Getcvt"] = () => ReturnVector(_frame.Arg(0), [0, 0]);
            h["_Getcoll"] = () => ReturnVector(_frame.Arg(0), [0, 0]);
            h["_Getctype"] = () => ReturnVector(_frame.Arg(0), [0, ClassicTable(), 0, 0]);
            h["_Getdateorder"] = () => _frame.Return(2);

            // _Getdays / _Getmonths hand back a malloc'd copy the caller frees.
            h["_Getdays"] = () => _frame.Return(MallocString(Days));
            h["_Getmonths"] = () => _frame.Return(MallocString(Months));

            // _Yarn<char> / _Yarn<wchar_t>
            h["??4?$_Yarn@D@std@@QAAAAV01@PBD@Z"] = () =>
            {
                long yarn = _frame.Arg(0), text = _frame.Arg(1);
                SetYarn(yarn, text == 0 ? null : _frame.ReadNarrowString(text));
                _frame.Return(yarn);
            };
            h["??4?$_Yarn@_W@std@@QAAAAV01@PB_W@Z"] = () =>
            {
                long yarn = _frame.Arg(0), text = _frame.Arg(1);
                SetYarn(yarn, text == 0 ? null : _emulator.ReadUtf16String(text, 1 << 16), wide: true);
                _frame.Return(yarn);
            };
            foreach (string y in new[] { "?$_Yarn@D@std@@QBAPBDXZ", "?$_Yarn@_W@std@@QBAPB_WXZ" })
            {
                h["?c_str@" + y] = () => YarnCStr();
                h["?_C_str@" + y] = () => YarnCStr();
            }

            h["?empty@?$_Yarn@D@std@@QBA_NXZ"] = () => _frame.Return(_emulator.ReadUInt32(_frame.Arg(0)) == 0 ? 1 : 0);
            h["?_Empty@?$_Yarn@D@std@@QBA_NXZ"] = () => _frame.Return(_emulator.ReadUInt32(_frame.Arg(0)) == 0 ? 1 : 0);
        }

        private void YarnCStr()
        {
            long yarn = _frame.Arg(0);
            long p = _emulator.ReadUInt32(yarn);
            _frame.Return(p != 0 ? p : yarn + 4);
        }

        /// <summary>
        /// _Locinfo(name): "C" and "" (the environment's default, which is "C" here) succeed;
        /// any other name leaves "*" as the new name, VC11's bad-locale marker.
        /// </summary>
        private void ConstructLocinfo(long self, long name)
        {
            _emulator.WriteMemory(self, new byte[LocinfoSize]);
            string requested = name == 0 ? "C" : _frame.ReadNarrowString(name);
            SetYarn(self + LiOldName, "C");
            SetYarn(self + LiNewName, requested is "C" or "" or "POSIX" ? "C" : "*");
            if (requested is not ("C" or "" or "POSIX"))
            {
                Note($"_Locinfo(\"{requested}\"): only the \"C\" locale exists - name set to \"*\"");
            }
        }

        private void DestroyLocinfo(long self)
        {
            for (int yarn = 4; yarn < LocinfoSize; yarn += 8)
            {
                FreeYarn(self + yarn);
            }
        }

        private void ReturnVector(long result, long[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                _emulator.WriteUInt32(result + (i * 4), (uint)words[i]);
            }

            _frame.Return(result);
        }

        private long MallocString(string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text + "\0");
            long p = _emulator.AllocateHeap(bytes.Length);
            _emulator.WriteMemory(p, bytes);
            return p;
        }

        /// <summary>
        /// The "C" locale's lconv: "." and the empty strings, CHAR_MAX for the numeric members, then
        /// VC11's wide copies of the eight string members.
        /// </summary>
        private long Lconv()
        {
            if (_lconv != 0)
            {
                return _lconv;
            }

            long empty = StaticString("", wide: false), dot = StaticString(".", wide: false);
            long wempty = StaticString("", wide: true), wdot = StaticString(".", wide: true);
            _lconv = Allocate(0x50);
            long[] strings = [dot, empty, empty, empty, empty, empty, empty, empty, empty, empty];
            for (int i = 0; i < strings.Length; i++)
            {
                _emulator.WriteUInt32(_lconv + (i * 4), (uint)strings[i]);
            }

            _emulator.WriteMemory(_lconv + 0x28, Enumerable.Repeat((byte)0x7F, 8).ToArray());
            long[] wide = [wdot, wempty, wempty, wempty, wempty, wempty, wempty, wempty];
            for (int i = 0; i < wide.Length; i++)
            {
                _emulator.WriteUInt32(_lconv + 0x30 + (i * 4), (uint)wide[i]);
            }

            return _lconv;
        }

        // ------------------------------------------------------------------------------
        // C-locale conversion helpers exported by MSVCP110
        // ------------------------------------------------------------------------------

        private void RegisterConversions(Dictionary<string, Action> h)
        {
            // int _Mbrtowc(wchar_t* pwc, const char* s, size_t n, mbstate_t*, const _Cvtvec*)
            h["_Mbrtowc"] = () =>
            {
                long pwc = _frame.Arg(0), s = _frame.Arg(1), n = _frame.Arg(2);
                if (s == 0)
                {
                    _frame.Return(0);
                    return;
                }

                if (n == 0)
                {
                    _frame.Return(unchecked((uint)-2));
                    return;
                }

                byte c = _emulator.ReadMemory(s, 1)[0];
                if (pwc != 0)
                {
                    _emulator.WriteMemory(pwc, [c, 0]);
                }

                _frame.Return(c == 0 ? 0 : 1);
            };

            // int _Wcrtomb(char* s, wchar_t wc, mbstate_t*, const _Cvtvec*): -1 (EILSEQ) above 0xFF.
            h["_Wcrtomb"] = () =>
            {
                long s = _frame.Arg(0);
                int wc = (int)(_frame.Arg(1) & 0xFFFF);
                if (wc > 0xFF)
                {
                    _frame.Return(unchecked((uint)-1));
                    return;
                }

                if (s != 0)
                {
                    _emulator.WriteMemory(s, [(byte)wc]);
                }

                _frame.Return(1);
            };

            // int _Strcoll(first1, last1, first2, last2, const _Collvec*); _Wcscoll likewise.
            h["_Strcoll"] = () => _frame.Return(unchecked((uint)Collate(1, _frame.Arg(0), _frame.Arg(1), _frame.Arg(2), _frame.Arg(3))));
            h["_Wcscoll"] = () => _frame.Return(unchecked((uint)Collate(2, _frame.Arg(0), _frame.Arg(1), _frame.Arg(2), _frame.Arg(3))));

            // size_t _Strxfrm(char* first1, char* last1, const char* first2, const char* last2, const _Collvec*):
            // the "C" key is the text; copied only when it fits, the length answered either way.
            h["_Strxfrm"] = () => Xfrm(1);
            h["_Wcsxfrm"] = () => Xfrm(2);

            // int _Tolower(int, const _Ctypevec*) etc.: "C" maps A-Z only.
            h["_Tolower"] = () => _frame.Return(unchecked((uint)ToLowerC(unchecked((int)(uint)_frame.Arg(0)))));
            h["_Toupper"] = () => _frame.Return(unchecked((uint)ToUpperC(unchecked((int)(uint)_frame.Arg(0)))));
            h["_Towlower"] = () => _frame.Return(ToLowerC((int)(_frame.Arg(0) & 0xFFFF)));
            h["_Towupper"] = () => _frame.Return(ToUpperC((int)(_frame.Arg(0) & 0xFFFF)));
        }

        private void Xfrm(int size)
        {
            long first1 = _frame.Arg(0), last1 = _frame.Arg(1), first2 = _frame.Arg(2), last2 = _frame.Arg(3);
            long n = Math.Max(0, (last2 - first2) / size);
            if (n <= (last1 - first1) / size && n > 0)
            {
                _emulator.WriteMemory(first1, _emulator.ReadMemory(first2, (int)(n * size)));
            }

            _frame.Return(n);
        }
    }

    /// <summary>strftime for the "C" locale, as time_put formats it.</summary>
    public static class CLocaleTime
    {
        private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
        private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

        /// <summary>
        /// Formats <paramref name="tm"/> (sec, min, hour, mday, mon, year, wday, yday, isdst)
        /// by a strftime format. The E and O modifiers change nothing in "C"; an unknown
        /// conversion is copied as it stands.
        /// </summary>
        public static string Format(string format, int[] tm)
        {
            StringBuilder sb = new();
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c != '%' || i + 1 >= format.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char spec = format[++i];
                if (spec is 'E' or 'O' or '#')
                {
                    if (i + 1 >= format.Length)
                    {
                        break;
                    }

                    spec = format[++i];
                }

                sb.Append(Convert(spec, tm));
            }

            return sb.ToString();
        }

        private static string Convert(char spec, int[] tm)
        {
            int sec = tm[0], min = tm[1], hour = tm[2], mday = tm[3], mon = tm[4], year = tm[5], wday = tm[6], yday = tm[7];
            string Day() => DayNames[((wday % 7) + 7) % 7];
            string Month() => MonthNames[((mon % 12) + 12) % 12];
            string D2(int v) => v.ToString("00", CultureInfo.InvariantCulture);
            return spec switch
            {
                'a' => Day()[..3],
                'A' => Day(),
                'b' or 'h' => Month()[..3],
                'B' => Month(),
                'c' => $"{D2(mon + 1)}/{D2(mday)}/{D2(((year % 100) + 100) % 100)} {D2(hour)}:{D2(min)}:{D2(sec)}",
                'd' => D2(mday),
                'H' => D2(hour),
                'I' => D2(hour % 12 == 0 ? 12 : hour % 12),
                'j' => (yday + 1).ToString("000", CultureInfo.InvariantCulture),
                'm' => D2(mon + 1),
                'M' => D2(min),
                'p' => hour < 12 ? "AM" : "PM",
                'S' => D2(sec),
                'U' => D2((yday + 7 - wday) / 7),
                'W' => D2((yday + 7 - ((wday + 6) % 7)) / 7),
                'w' => wday.ToString(CultureInfo.InvariantCulture),
                'x' => $"{D2(mon + 1)}/{D2(mday)}/{D2(((year % 100) + 100) % 100)}",
                'X' => $"{D2(hour)}:{D2(min)}:{D2(sec)}",
                'y' => D2(((year % 100) + 100) % 100),
                'Y' => (year + 1900).ToString(CultureInfo.InvariantCulture),
                'z' or 'Z' => string.Empty,
                '%' => "%",
                _ => "%" + spec,
            };
        }
    }
}
