using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// MSVCP110 iostreams, streambufs, ios_base, locale and facets. Guest virtuals cannot run in a
/// direct call, so the streams here sit on buffers whose get and put areas are laid out in guest
/// memory and big enough that the base implementations never call back; where a member's whole
/// job is to forward to a virtual, the test checks that it forwards to the right slot.
/// </summary>
public sealed class StreamsLocaleSuite : IContractSuite
{
    public string Name => "streams and locale";

    private const string T = "DU?$char_traits@D@std@@";
    private const string WT = "_WU?$char_traits@_W@std@@";
    private const string Sb = $"?$basic_streambuf@{T}@std@@";
    private const string WSb = $"?$basic_streambuf@{WT}@std@@";
    private const string Os = $"?$basic_ostream@{T}@std@@";
    private const string WOs = $"?$basic_ostream@{WT}@std@@";
    private const string Is = $"?$basic_istream@{T}@std@@";
    private const string Ios = $"?$basic_ios@{T}@std@@";
    private const string B = "ios_base@std@@";
    private const int R12 = 78;

    private Harness _h = null!;
    private ArmEmulator _e = null!;

    public void Run(Harness h)
    {
        _h = h;
        _e = h.E;
        Numbers();
        Locale();
        Ctype();
        Codecvt();
        Locinfo();
        Streambuf();
        Ostream();
        WideOstream();
        Istream();
        IosBase();
        DataImports();
        CensusReport();
    }

    /// <summary>
    /// WP8_CENSUS=&lt;path to Plans/WP8-API-CENSUS-unimplemented.tsv&gt;: lists the MSVCP110
    /// entries that still reach no handler and are not variables. Informational, not a check.
    /// </summary>
    private void CensusReport()
    {
        string? path = Environment.GetEnvironmentVariable("WP8_CENSUS");
        if (path is null || !File.Exists(path))
        {
            return;
        }

        var open = File.ReadLines(path)
            .Select(l => l.Split('\t'))
            .Where(f => f.Length > 1 && f[1].StartsWith("MSVCP110", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Titles: f[0], Name: f[1][(f[1].IndexOf('!') + 1)..]))
            .Where(e => _h.S.ResolveDirect(e.Name) is null && !StreamLibrary.IsDataImport(e.Name))
            .ToList();
        Console.WriteLine($"census: {open.Count} MSVCP110 entries still open");
        foreach (var e in open) Console.WriteLine($"  open {e.Titles,4} {e.Name}");
    }

    private void Check(string what, bool ok, string detail = "") => _h.Check(what, ok, detail);

    private long Call(string fn, params long[] args) => _h.Call(fn, args);

    private long Words64(long low) => low | (_h.R1Value << 32);

    private uint U32(long a) => _e.ReadUInt32(a);

    private long I64(long a) => BitConverter.ToInt64(_e.ReadMemory(a, 8));

    private long Slot(long obj, int slot) => U32(U32(obj) + (slot * 4));

    private long R12Value => _e.ReadRegister(R12) & 0xFFFFFFFF;

    // ------------------------------------------------------------------
    private void Numbers()
    {
        const uint Fixed = 0x2000, Sci = 0x1000, Upper = 0x4, Hex = 0x800, Oct = 0x400, ShowBase = 0x8, ShowPos = 0x20, Internal = 0x100, Left = 0x40, ShowPoint = 0x10;
        string F(double v, uint flags, long p) => StreamNumbers.FormatFloat(v, flags, p).Text;
        Check("%g of 3.14159 at precision 6", F(3.14159, 0, 6) == "3.14159", F(3.14159, 0, 6));
        Check("%g switches to e at 1e10, with VC's three-digit exponent", F(1e10, 0, 6) == "1e+010", F(1e10, 0, 6));
        Check("%g keeps 0.0001 and turns 1e-5 into e", F(0.0001, 0, 6) == "0.0001" && F(1e-5, 0, 6) == "1e-005");
        Check("%g rounds before choosing (999999.5 -> 1e+006)", F(999999.5, 0, 6) == "1e+006", F(999999.5, 0, 6));
        Check("precision 0 means 6 unless fixed", F(1.0 / 3, 0, 0) == "0.333333" && F(2.5, Fixed, 0) == "2");
        Check("fixed, precision 2", F(3.14159, Fixed, 2) == "3.14");
        Check("scientific, uppercase", F(1234.5, Sci | Upper, 3) == "1.234E+003", F(1234.5, Sci | Upper, 3));
        Check("showpoint keeps the point and zeros", F(2.0, ShowPoint, 6) == "2.00000", F(2.0, ShowPoint, 6));
        Check("showpos on a double", F(1.5, ShowPos, 6) == "+1.5");
        Check("infinity is 1.#INF00 in fixed, 1.#INF in %g", F(double.PositiveInfinity, Fixed, 6) == "1.#INF00" && F(double.PositiveInfinity, 0, 6) == "1.#INF");
        Check("%.2f of infinity is 1.#J (rounded like digits)", F(double.PositiveInfinity, Fixed, 2) == "1.#J", F(double.PositiveInfinity, Fixed, 2));
        Check("the default NaN is -1.#IND00", F(double.NaN, Fixed, 6) == "-1.#IND00", F(double.NaN, Fixed, 6));
        Check("hexfloat", F(1.0, Fixed | Sci, 2) == "0x1.00p+0", F(1.0, Fixed | Sci, 2));

        string I(long v, bool s, int bits, uint flags) => StreamNumbers.FormatInteger(v, s, bits, flags).Text;
        Check("hex, showbase, uppercase", I(42, true, 32, Hex | ShowBase | Upper) == "0X2A");
        Check("negative int in hex is its 32-bit pattern", I(-1, true, 32, Hex) == "ffffffff");
        Check("negative short in hex is its 16-bit pattern", I(-1, true, 16, Hex) == "ffff");
        Check("octal showbase, and 0 stays 0", I(8, true, 32, Oct | ShowBase) == "010" && I(0, true, 32, Oct | ShowBase) == "0");
        Check("showpos on a signed decimal, not on an unsigned", I(5, true, 32, ShowPos | 0x200) == "+5" && I(5, false, 32, ShowPos | 0x200) == "5");
        Check("internal padding goes after the sign", StreamNumbers.Pad(StreamNumbers.FormatInteger(-42, true, 32, 0x200), 6, '0', Internal) == "-00042");
        Check("left padding goes after", StreamNumbers.Pad(StreamNumbers.FormatInteger(7, true, 32, 0x200), 3, '*', Left) == "7**");

        StreamNumbers.IntegerScanner Scan(string text, uint flags)
        {
            var s = new StreamNumbers.IntegerScanner(flags);
            foreach (char c in text) if (!s.Feed(c)) break;
            return s;
        }

        Check("auto base reads 0x1F as 31 and 017 as 15", Scan("0x1F", 0).AsSigned(32) == 31 && Scan("017", 0).AsSigned(32) == 15);
        Check("decimal stops 0x1F at the x", Scan("0x1F", 0x200).AsSigned(32) == 0);
        Check("0x with no digits fails", Scan("0x", 0).AsSigned(32) is null);
        Check("an int that does not fit fails", Scan("4294967296", 0x200).AsSigned(32) is null && Scan("2147483648", 0x200).AsSigned(32) is null);
        Check("strtoul negates: -1 is 4294967295", Scan("-1", 0x200).AsUnsigned(32) == 4294967295);
        Check("unsigned short: magnitude must fit 16 bits", Scan("65536", 0x200).AsUnsigned(16) is null && Scan("-1", 0x200).AsUnsigned(16) == 65535);

        var f = new StreamNumbers.FloatScanner();
        foreach (char c in "-3.5e2x") if (!f.Feed(c)) break;
        Check("float scan -3.5e2 stops at x", f.AsDouble(false) == -350 && f.ToString() == "-3.5e2");
        var bad = new StreamNumbers.FloatScanner();
        foreach (char c in "1e+") if (!bad.Feed(c)) break;
        Check("an exponent with no digits fails", bad.AsDouble(false) is null);
        var big = new StreamNumbers.FloatScanner();
        foreach (char c in "1e39") if (!big.Feed(c)) break;
        Check("1e39 overflows a float but not a double", big.AsDouble(true) is null && big.AsDouble(false) == 1e39);
        Check("strftime C locale", CLocaleTime.Format("%a %b %d %H:%M:%S %Y %j %p", [5, 4, 13, 9, 9, 126, 5, 281, 0]) == "Fri Oct 09 13:04:05 2026 282 PM");
    }

    // ------------------------------------------------------------------
    private void Locale()
    {
        long global = Call("?_Getgloballocale@locale@std@@CAPAV_Locimp@12@XZ");
        Check("_Getgloballocale makes the global locale", global != 0);
        uint refs = U32(global + 4);
        Check("_Init(true) answers the global locale with a reference", Call("?_Init@locale@std@@CAPAV_Locimp@12@_N@Z", 1) == global && U32(global + 4) == refs + 1);
        Check("its name is \"C\", catmask all, no facets yet, not transparent",
            _h.ReadNarrow(U32(global + 0x18)) == "C" && U32(global + 0x10) == 0x3F && U32(global + 0xC) == 0 && _e.ReadMemory(global + 0x14, 1)[0] == 0);
        long classic = Call("?classic@locale@std@@SAABV12@XZ");
        Check("classic() is a locale holding the \"C\" _Locimp", classic != 0 && U32(classic) == global);

        long id1 = _h.Buffer(4), id2 = _h.Buffer(4);
        long a = Call("??Bid@locale@std@@QAAIXZ", id1), b = Call("??Bid@locale@std@@QAAIXZ", id2);
        Check("id::operator size_t numbers ids from 1, once each", a > 0 && b == a + 1 && Call("??Bid@locale@std@@QAAIXZ", id1) == a && U32(id1) == a);
        Check("id(size_t) stores its value", Call("??0id@locale@std@@QAA@I@Z", id1, 77) == id1 && U32(id1) == 77);

        long empty = _h.Buffer(4);
        Call("?empty@locale@std@@SA?AV12@XZ", empty);
        long emptyImp = U32(empty);
        Check("empty() is a transparent _Locimp with one reference", emptyImp != 0 && emptyImp != global && _e.ReadMemory(emptyImp + 0x14, 1)[0] == 1 && U32(emptyImp + 4) == 1);

        // A copy of the global locale, then a facet added to it.
        long copy = Call("?_New_Locimp@_Locimp@locale@std@@CAPAV123@ABV123@@Z", global);
        Check("_New_Locimp copies name and mask, one reference", copy != 0 && copy != global && _h.ReadNarrow(U32(copy + 0x18)) == "C" && U32(copy + 4) == 1);
        long ppf = _h.Buffer(4);
        Call("?_Getcat@?$ctype@D@std@@SAIPAPBVfacet@locale@2@PBV42@@Z", ppf, classic);
        long facet = U32(ppf);
        Call("?_Addfac@_Locimp@locale@std@@AAAXPAVfacet@23@I@Z", copy, facet, 5);
        Check("_Addfac grows the vector to 40 and references the facet",
            U32(copy + 0xC) == 40 && U32(U32(copy + 8) + 20) == facet && U32(facet + 4) == 1, $"count={U32(copy + 0xC)} refs={U32(facet + 4)}");
        long copy2 = Call("?_New_Locimp@_Locimp@locale@std@@CAPAV123@ABV123@@Z", copy);
        Check("copying a _Locimp references its facets", U32(copy2 + 0xC) == 40 && U32(U32(copy2 + 8) + 20) == facet && U32(facet + 4) == 2);

        long locale = _h.Buffer(4);
        _e.WriteUInt32(locale, (uint)copy);
        long old = _h.Buffer(4);
        Call("?global@locale@std@@SA?AV12@ABV12@@Z", old, locale);
        Check("global(loc) installs loc and answers the old global", U32(old) == global && Call("?_Getgloballocale@locale@std@@CAPAV_Locimp@12@XZ") == copy);
        Call("?global@locale@std@@SA?AV12@ABV12@@Z", _h.Buffer(4), classic);
        Check("global(classic()) restores it", Call("?_Getgloballocale@locale@std@@CAPAV_Locimp@12@XZ") == global);

        Check("facet::_Incref / _Decref count, and _Decref answers the facet at zero",
            Call("?_Decref@facet@locale@std@@UAAPAV_Facet_base@3@XZ", facet) == 0 && U32(facet + 4) == 1);
        long own = _h.Buffer(8);
        Call("??0facet@locale@std@@IAA@I@Z", own, 0);
        Check("facet(0) has refs 0 and a vtable; _Incref then _Decref answers it", U32(own) != 0 && U32(own + 4) == 0 &&
            Call("?_Incref@facet@locale@std@@UAAXXZ", own) == 0 && Call("?_Decref@facet@locale@std@@UAAPAV_Facet_base@3@XZ", own) == own);
    }

    // ------------------------------------------------------------------
    private void Ctype()
    {
        const string C = "?$ctype@D@std@@";
        long ppf = _h.Buffer(4);
        Check("_Getcat(ctype<char>) answers LC_CTYPE (2) and makes a facet", Call($"?_Getcat@{C}SAIPAPBVfacet@locale@2@PBV42@@Z", ppf, 0) == 2 && U32(ppf) != 0);
        long f = U32(ppf);
        Check("_Getcat leaves an existing facet alone", Call($"?_Getcat@{C}SAIPAPBVfacet@locale@2@PBV42@@Z", ppf, 0) == 2 && U32(ppf) == f);
        Check("_Getcat(0, 0) still answers the category", Call($"?_Getcat@{C}SAIPAPBVfacet@locale@2@PBV42@@Z", 0, 0) == 2);

        long table = Call($"?classic_table@{C}SAPBFXZ");
        int M(int c) => _h.U16(table + (c * 2));
        Check("classic table: 'A' 0x181, 'a' 0x182, '0' 0x84, ' ' 0x48, '\\t' 0x68, '!' 0x10, 0xE9 0",
            M('A') == 0x181 && M('a') == 0x182 && M('0') == 0x84 && M(' ') == 0x48 && M('\t') == 0x68 && M('!') == 0x10 && M(0xE9) == 0,
            $"A={M('A'):X} a={M('a'):X} 0={M('0'):X} sp={M(' '):X} tab={M('\t'):X}");
        Check("is(space, ' ') and not is(alpha, '5')", Call($"?is@{C}QBA_NFD@Z", f, 0x8, ' ') == 1 && Call($"?is@{C}QBA_NFD@Z", f, 0x103, '5') == 0);
        Check("table() is the classic table", Call($"?table@{C}IBAPBFXZ", f) == table);

        Check("do_tolower / do_toupper: A-Z only in \"C\"", Call($"?do_tolower@{C}MBADD@Z", f, 'Q') == 'q' && Call($"?do_toupper@{C}MBADD@Z", f, 'q') == 'Q' && Call($"?do_tolower@{C}MBADD@Z", f, 0xC9) == 0xC9);
        long text = _h.Narrow("MiXeD 1!");
        long end = text + 8;
        Check("do_toupper(range) in place, answers last", Call($"?do_toupper@{C}MBAPBDPADPBD@Z", f, text, end) == end && _h.ReadNarrow(text) == "MIXED 1!");
        long dest = _h.Buffer(16);
        Check("do_widen(range) copies", Call($"?do_widen@{C}MBAPBDPBD0PAD@Z", f, text, end, dest) == end && _h.ReadNarrow(dest) == "MIXED 1!");
        Check("do_narrow(c, dflt) is c", Call($"?do_narrow@{C}MBADDD@Z", f, 'z', '?') == 'z');

        Call($"?tolower@{C}QBADD@Z", f, 'A');
        Check("tolower(c) forwards to the facet's do_tolower slot (4)", R12Value == Slot(f, 4), $"r12=0x{R12Value:X} slot=0x{Slot(f, 4):X}");
        Call($"?widen@{C}QBADD@Z", f, 'A');
        Check("widen(c) forwards to slot 8", R12Value == Slot(f, 8));

        long own = _h.Buffer(0x18), mine = _h.Buffer(512);
        Check("ctype<char>(table, del, refs) keeps the table", Call($"??0{C}QAA@PBF_NI@Z", own, mine, 1, 3) == own && U32(own + 0xC) == mine && U32(own + 0x10) == 0xFFFFFFFF && U32(own + 4) == 3);
        Call($"??1{C}MAA@XZ", own);
        Check("~ctype frees an owned table", U32(own + 0x10) == 0);
        Check("ctype<char>(0) uses the classic table", Call($"??0{C}QAA@PBF_NI@Z", own, 0, 0, 0) == own && U32(own + 0xC) == table);

        const string W = "?$ctype@_W@std@@";
        long wp = _h.Buffer(4);
        Call($"?_Getcat@{W}SAIPAPBVfacet@locale@2@PBV42@@Z", wp, 0);
        long wf = U32(wp);
        Call($"?is@{W}QBA_NF_W@Z", wf, 0x100, 0xE9);
        Check("ctype<wchar_t>::is forwards to do_is (slot 4)", wf != 0 && R12Value == Slot(wf, 4));
        Check("wide classification is Unicode's: 0xE9 alpha|lower, 0x3000 space, 0x663 digit",
            (LocaleLibrary.WideMask(0xE9) & 0x102) == 0x102 && (LocaleLibrary.WideMask(0x3000) & 0x8) != 0 && (LocaleLibrary.WideMask(0x663) & 0x4) != 0);
    }

    // ------------------------------------------------------------------
    private void Codecvt()
    {
        long ppf = _h.Buffer(4);
        Check("_Getcat(codecvt<char,char,int>) answers 2", Call("?_Getcat@?$codecvt@DDH@std@@SAIPAPBVfacet@locale@2@PBV42@@Z", ppf, 0) == 2);
        long c = U32(ppf);
        Call("?always_noconv@codecvt_base@std@@QBA_NXZ", c);
        Check("always_noconv forwards to slot 3", R12Value == Slot(c, 3));
        Call("?in@?$codecvt@DDH@std@@QBAHAAHPBD1AAPBDPAD3AAPAD@Z", c, 0, 0, 0, 0, 0, 0, 0);
        Check("in forwards to slot 6, out to 7, unshift to 8", R12Value == Slot(c, 6) &&
            Forwarded("?out@?$codecvt@DDH@std@@QBAHAAHPBD1AAPBDPAD3AAPAD@Z", c) == Slot(c, 7) &&
            Forwarded("?unshift@?$codecvt@DDH@std@@QBAHAAHPAD1AAPAD@Z", c) == Slot(c, 8));

        long state = _h.Buffer(4), from = _h.Narrow("abc"), fromNext = _h.Buffer(4), to = _h.Buffer(8), toNext = _h.Buffer(4);
        long r = Call("?do_in@?$codecvt@DDH@std@@MBAHAAHPBD1AAPBDPAD3AAPAD@Z", c, state, from, from + 3, fromNext, to, to + 8, toNext);
        Check("codecvt<char>::do_in is noconv with nothing consumed", r == 3 && U32(fromNext) == from && U32(toNext) == to);

        const string W = "?$codecvt@_WDH@std@@";
        long wfacet = _h.Buffer(0x10);
        Check("codecvt<wchar_t,char,int>(refs) constructs", Call($"??0{W}QAA@I@Z", wfacet, 1) == wfacet && U32(wfacet + 4) == 1 && U32(wfacet) != 0);
        long wide = _h.Wide("HiĀ!"), bytes = _h.Buffer(8);
        r = Call($"?do_out@{W}MBAHAAHPB_W1AAPB_WPAD3AAPAD@Z", wfacet, state, wide, wide + 4, fromNext, bytes, bytes + 8, toNext);
        Check("do_out converts byte-sized characters", r == 0 && U32(fromNext) == wide + 4 && U32(toNext) == bytes + 2 && _h.ReadNarrow(bytes) == "Hi");
        r = Call($"?do_out@{W}MBAHAAHPB_W1AAPB_WPAD3AAPAD@Z", wfacet, state, wide, wide + 8, fromNext, bytes, bytes + 8, toNext);
        Check("do_out stops with error at U+0100", r == 2 && U32(fromNext) == wide + 4 && U32(toNext) == bytes + 2);
        long src = _h.Narrow("xyz"), wout = _h.Buffer(16);
        r = Call($"?do_in@{W}MBAHAAHPBD1AAPBDPA_W3AAPA_W@Z", wfacet, state, src, src + 3, fromNext, wout, wout + 4, toNext);
        Check("do_in is partial when the output fills", r == 1 && U32(fromNext) == src + 2 && _h.ReadWide(wout, 2) == "xy");
        r = Call($"?do_unshift@{W}MBAHAAHPAD1AAPAD@Z", wfacet, state, bytes, bytes + 8, toNext);
        Check("do_unshift has nothing to write", r == 0 && U32(toNext) == bytes);
    }

    private long Forwarded(string fn, long self)
    {
        Call(fn, self, 0, 0, 0, 0, 0, 0, 0);
        return R12Value;
    }

    // ------------------------------------------------------------------
    private void Locinfo()
    {
        long li = _h.Buffer(0x40);
        _e.WriteMemory(li + 0x34, [0xCC, 0xCC, 0xCC, 0xCC]);
        Call("??0_Locinfo@std@@QAA@PBD@Z", li, _h.Narrow("C"));
        Check("_Locinfo(\"C\") is named C, and stays in its 52 bytes", _h.ReadNarrow(Call("?_Getname@_Locinfo@std@@QBAPBDXZ", li)) == "C" && U32(li + 0x34) == 0xCCCCCCCC);
        Check("_Gettrue / _Getfalse", _h.ReadNarrow(Call("?_Gettrue@_Locinfo@std@@QBAPBDXZ", li)) == "true" && _h.ReadNarrow(Call("?_Getfalse@_Locinfo@std@@QBAPBDXZ", li)) == "false");
        Check("_Getdays starts :Sun:Sunday", _h.ReadNarrow(Call("?_Getdays@_Locinfo@std@@QBAPBDXZ", li)).StartsWith(":Sun:Sunday:Mon"));
        long lconv = Call("?_Getlconv@_Locinfo@std@@QBAPBUlconv@@XZ", li);
        Check("_Getlconv: decimal point \".\"", lconv != 0 && _h.ReadNarrow(U32(lconv)) == ".");
        long vec = _h.Buffer(16);
        _e.WriteMemory(vec, Enumerable.Repeat((byte)0xAB, 16).ToArray());
        Call("?_Getcvt@_Locinfo@std@@QBA?AU_Cvtvec@@XZ", li, vec);
        Check("_Getcvt writes the 8-byte \"C\" _Cvtvec and no more", U32(vec) == 0 && U32(vec + 4) == 0 && U32(vec + 8) == 0xABABABAB);
        Call("??1_Locinfo@std@@QAA@XZ", li);
        Call("??0_Locinfo@std@@QAA@PBD@Z", li, _h.Narrow("fr-FR"));
        Check("any other locale name is the bad-name marker \"*\"", _h.ReadNarrow(Call("?_Getname@_Locinfo@std@@QBAPBDXZ", li)) == "*");
        Call("??1_Locinfo@std@@QAA@XZ", li);

        long yarn = _h.Buffer(8);
        Check("_Yarn<char>::operator= copies, c_str reads it", Call("??4?$_Yarn@D@std@@QAAAAV01@PBD@Z", yarn, _h.Narrow("hello")) == yarn && _h.ReadNarrow(Call("?c_str@?$_Yarn@D@std@@QBAPBDXZ", yarn)) == "hello");
        Call("??4?$_Yarn@D@std@@QAAAAV01@PBD@Z", yarn, 0);
        Check("an empty yarn's c_str is its own NUL", Call("?c_str@?$_Yarn@D@std@@QBAPBDXZ", yarn) == yarn + 4);

        long wc = _h.Buffer(4);
        long e9 = _h.Buffer(2);
        _e.WriteMemory(e9, [0xE9]);
        Check("_Mbrtowc: one byte is one character; NUL is 0; n=0 is -2",
            Call("_Mbrtowc", wc, e9, 1, 0, 0) == 1 && _h.U16(wc) == 0xE9 && Call("_Mbrtowc", wc, _h.Narrow(""), 1, 0, 0) == 0 && Call("_Mbrtowc", wc, _h.Narrow("a"), 0, 0, 0) == 0xFFFFFFFE);
        long mb = _h.Buffer(4);
        Check("_Wcrtomb: byte-sized ok, U+0100 is -1", Call("_Wcrtomb", mb, 'z', 0, 0) == 1 && _e.ReadMemory(mb, 1)[0] == 'z' && Call("_Wcrtomb", mb, 0x100, 0, 0) == 0xFFFFFFFF);
        long s1 = _h.Narrow("abc"), s2 = _h.Narrow("abd");
        Check("_Strcoll orders bytes", (int)(uint)Call("_Strcoll", s1, s1 + 3, s2, s2 + 3, 0) < 0 && Call("_Strcoll", s1, s1 + 3, s1, s1 + 3, 0) == 0 && Call("_Strcoll", s1, s1 + 2, s1, s1 + 3, 0) == 0xFFFFFFFF);
        long xf = _h.Buffer(8);
        Check("_Strxfrm copies when it fits, answers the length either way", Call("_Strxfrm", xf, xf + 8, s2, s2 + 3, 0) == 3 && _h.ReadNarrow(xf) == "abd" && Call("_Strxfrm", xf, xf + 2, s1, s1 + 3, 0) == 3);
    }

    // ------------------------------------------------------------------
    private long _sb;

    /// <summary>A narrow streambuf constructed by the import, with get and put areas.</summary>
    private long NewBuffer(string get, int putSize, out long putArea)
    {
        long sb = _h.Buffer(0x40);
        Call($"??0{Sb}IAA@XZ", sb);
        long g = _h.Narrow(get);
        Call($"?setg@{Sb}IAAXPAD00@Z", sb, g, g, g + get.Length);
        putArea = _h.Buffer(putSize + 1);
        Call($"?setp@{Sb}IAAXPAD0@Z", sb, putArea, putArea + putSize);
        return sb;
    }

    private string Written(long sb) => _h.ReadNarrow(Call($"?pbase@{Sb}IBAPADXZ", sb), (int)(Call($"?pptr@{Sb}IBAPADXZ", sb) - Call($"?pbase@{Sb}IBAPADXZ", sb)) + 1)
        [..(int)(Call($"?pptr@{Sb}IBAPADXZ", sb) - Call($"?pbase@{Sb}IBAPADXZ", sb))];

    private void Streambuf()
    {
        long sb = _h.Buffer(0x40);
        Check("basic_streambuf() returns this with the base vtable and the global locale",
            Call($"??0{Sb}IAA@XZ", sb) == sb && U32(sb) != 0 && U32(U32(sb + 0x34)) == Call("?_Getgloballocale@locale@std@@CAPAV_Locimp@12@XZ"));
        Check("its indirect fields point at its own", U32(sb + 0xC) == sb + 4 && U32(sb + 0x1C) == sb + 0x14 && U32(sb + 0x2C) == sb + 0x24);
        long loc = _h.Buffer(4);
        Check("getloc returns a copy through r1", Call($"?getloc@{Sb}QBA?AVlocale@2@XZ", sb, loc) == loc && U32(loc) == U32(U32(sb + 0x34)));

        long g = _h.Narrow("hello");
        Call($"?setg@{Sb}IAAXPAD00@Z", sb, g, g + 1, g + 5);
        Check("setg: eback/gptr/egptr", Call($"?eback@{Sb}IBAPADXZ", sb) == g && Call($"?gptr@{Sb}IBAPADXZ", sb) == g + 1 && Call($"?egptr@{Sb}IBAPADXZ", sb) == g + 5);
        Check("in_avail counts the get area", Words64(Call($"?in_avail@{Sb}QAA_JXZ", sb)) == 4);
        Check("sgetc peeks, sbumpc takes, snextc takes and peeks",
            Call($"?sgetc@{Sb}QAAHXZ", sb) == 'e' && Call($"?sbumpc@{Sb}QAAHXZ", sb) == 'e' && Call($"?snextc@{Sb}QAAHXZ", sb) == 'l');
        Check("sungetc backs up, sputbackc of the right char backs up", Call($"?sungetc@{Sb}QAAHXZ", sb) == 'l' && Call($"?sputbackc@{Sb}QAAHD@Z", sb, 'e') == 'e' && Call($"?gptr@{Sb}IBAPADXZ", sb) == g + 1);
        Call($"?sputbackc@{Sb}QAAHD@Z", sb, 'x');
        Check("sputbackc of a different char forwards to pbackfail(c)", R12Value == Slot(sb, 4) && (_e.ReadRegister(Harness.R1) & 0xFF) == 'x');
        Check("gbump / _Gninc / _Gndec", Call($"?gbump@{Sb}IAAXH@Z", sb, 2) == 0 && Call($"?_Gninc@{Sb}IAAPADXZ", sb) == g + 3 && Call($"?_Gndec@{Sb}IAAPADXZ", sb) == g + 3 && Call($"?gptr@{Sb}IBAPADXZ", sb) == g + 3);
        long dest = _h.Buffer(8);
        long got = Words64(Call($"?xsgetn@{Sb}MAA_JPAD_J@Z", sb, dest, 2, 0));
        Check("xsgetn takes from the get area (64-bit count)", got == 2 && _h.ReadNarrow(dest) == "lo");

        long p = _h.Buffer(16);
        Call($"?setp@{Sb}IAAXPAD00@Z", sb, p, p + 2, p + 10);
        Check("setp(first, next, last): pbase/pptr/epptr", Call($"?pbase@{Sb}IBAPADXZ", sb) == p && Call($"?pptr@{Sb}IBAPADXZ", sb) == p + 2 && Call($"?epptr@{Sb}IBAPADXZ", sb) == p + 10);
        Call($"?setp@{Sb}IAAXPAD0@Z", sb, p, p + 10);
        Check("sputc stores and answers the character", Call($"?sputc@{Sb}QAAHD@Z", sb, 'A') == 'A' && _e.ReadMemory(p, 1)[0] == 'A');
        Check("xsputn writes the rest of a string", Words64(Call($"?xsputn@{Sb}MAA_JPBD_J@Z", sb, _h.Narrow("BCD"), 3, 0)) == 3 && _h.ReadNarrow(p) == "ABCD");
        Check("pbump and _Pninc", Call($"?pbump@{Sb}IAAXH@Z", sb, 1) == 0 && Call($"?_Pninc@{Sb}IAAPADXZ", sb) == p + 5 && Call($"?pptr@{Sb}IBAPADXZ", sb) == p + 6);
        Call($"?pubsync@{Sb}QAAHXZ", sb);
        Check("pubsync forwards to sync (slot 13)", R12Value == Slot(sb, 13));

        long fpos = _h.Buffer(24);
        Call($"?seekoff@{Sb}MAA?AV?$fpos@H@2@_JHH@Z", sb, fpos, 0, 0, 1, 1);
        Check("the base seekoff answers fpos(-1) through r1", I64(fpos) == -1 && I64(fpos + 8) == 0);
        Check("the base overflow / underflow answer eof", Call($"?overflow@{Sb}MAAHH@Z", sb, 'x') == 0xFFFFFFFF && Call($"?underflow@{Sb}MAAHXZ", sb) == 0xFFFFFFFF);
        Check("the base wide uflow's eof is WEOF", Call($"?pbackfail@{WSb}MAAGG@Z", sb, 1) == 0xFFFF);

        // _Init(gf, gn, gc, pf, pn, pc): the indirect fields aimed at the caller's own.
        long cells = _h.Buffer(24);
        Call($"?_Init@{Sb}IAAXPAPAD0PAH001@Z", sb, cells, cells + 4, cells + 8, cells + 12, cells + 16, cells + 20);
        Check("_Init(6 pointers) takes three on the stack", U32(sb + 0xC) == cells && U32(sb + 0x1C) == cells + 4 && U32(sb + 0x2C) == cells + 8 &&
            U32(sb + 0x10) == cells + 12 && U32(sb + 0x20) == cells + 16 && U32(sb + 0x30) == cells + 20);
        _sb = sb;
    }

    // ------------------------------------------------------------------
    private long NewOstream(long sb)
    {
        long os = _h.Buffer(0x50);
        Call($"??0{Os}QAA@PAV?$basic_streambuf@{T}@1@_N@Z", os, sb, 0, 1);
        return os;
    }

    private void Ostream()
    {
        long sb = NewBuffer("", 256, out long put);
        long os = NewOstream(sb);
        long ios = os + 8;
        Check("ostream(sb) most-derived: vbtable to the ios at +8, ios initialised",
            U32(os) != 0 && U32(U32(os) + 4) == 8 && U32(ios + 0x38) == sb && U32(ios + 0xC) == 0 && U32(ios + 0x14) == 0x201 && I64(ios + 0x18) == 6 && U32(ios + 0x40) == ' ' && U32(ios + 0x30) != 0 && U32(ios) != 0);

        string Out() => Written(sb);
        void Reset() => Call($"?setp@{Sb}IAAXPAD0@Z", sb, put, put + 256);

        _e.WriteUInt64(ios + 0x20, 5);
        Check("<< int pads to width and returns *this", Call($"??6{Os}QAAAAV01@H@Z", os, 42) == os && Out() == "   42" && I64(ios + 0x20) == 0);
        Reset();
        Call($"?setf@{B}QAAHHH@Z", ios, 0x800 | 0x8 | 0x4, 0x800 | 0x200 | 0x400 | 0x8 | 0x4);
        Call($"??6{Os}QAAAAV01@H@Z", os, 42);
        Check("hex | showbase | uppercase", Out() == "0X2A", Out());
        Reset();
        Call($"?setf@{B}QAAHHH@Z", ios, 0x200 | 0x100, 0xFFFF);
        Call($"?fill@{Ios}QAADD@Z", ios, '0');
        _e.WriteUInt64(ios + 0x20, 6);
        Call($"??6{Os}QAAAAV01@H@Z", os, unchecked((uint)-42));
        Check("internal with fill '0'", Out() == "-00042", Out());
        Call($"?fill@{Ios}QAADD@Z", ios, ' ');
        Reset();
        Call($"?flags@{B}QAAHH@Z", ios, 0x201);
        _h.SetD64(0, 3.14159);
        Call($"??6{Os}QAAAAV01@N@Z", os);
        _h.SetS32(0, 0.5f);
        Call($"??6{Os}QAAAAV01@M@Z", os);
        Check("<< double (d0) and << float (s0), precision 6 %g", Out() == "3.141590.5", Out());
        Reset();
        Call($"?setf@{B}QAAHHH@Z", ios, 0x2000, 0x3000);
        _e.WriteUInt64(ios + 0x18, 2);
        _h.SetD64(0, 2.0 / 3);
        Call($"??6{Os}QAAAAV01@N@Z", os);
        Check("fixed, precision 2", Out() == "0.67", Out());
        Reset();
        Call($"?flags@{B}QAAHH@Z", ios, 0x201 | 0x4000);
        Call($"??6{Os}QAAAAV01@_N@Z", os, 1);
        Call($"?flags@{B}QAAHH@Z", ios, 0x201);
        Call($"??6{Os}QAAAAV01@_N@Z", os, 0);
        Check("bool: boolalpha \"true\", otherwise 0", Out() == "true0", Out());
        Reset();
        Call($"??6{Os}QAAAAV01@_J@Z", os, 0, unchecked((uint)-1), 0x7FFFFFFF);
        Check("<< long long from r2:r3", Out() == long.MaxValue.ToString(), Out());
        Reset();
        Call($"??6{Os}QAAAAV01@PBX@Z", os, 0x1234ABCD);
        Check("<< const void* is %p", Out() == "1234ABCD", Out());
        Reset();
        Call($"?put@{Os}QAAAAV12@D@Z", os, 'x');
        Call($"?write@{Os}QAAAAV12@PBD_J@Z", os, _h.Narrow("yz!"), 2, 0);
        Check("put and write (unformatted)", Out() == "xyz", Out());
        Reset();

        // A failed stream writes nothing and gains no state from an inserter.
        Call($"?setstate@{Ios}QAAXH_N@Z", ios, 2, 0);
        Call($"??6{Os}QAAAAV01@H@Z", os, 7);
        Check("a failed sentry writes nothing and adds no state", Out() == "" && U32(ios + 0xC) == 2);
        Call($"?put@{Os}QAAAAV12@D@Z", os, 'x');
        Check("put on a failed stream sets badbit", U32(ios + 0xC) == 6);
        Call($"?clear@{Ios}QAAXH_N@Z", ios, 0, 0);
        Check("clear(goodbit) restores it", U32(ios + 0xC) == 0);

        // A stream with no buffer: clear() keeps badbit, flush is harmless.
        long bare = NewOstream(0);
        Check("ostream(nullptr) is badbit, and clear() keeps it", U32(bare + 8 + 0xC) == 4 && Call($"?clear@{Ios}QAAXH_N@Z", bare + 8, 0, 0) == 0 && U32(bare + 8 + 0xC) == 4);
        Check("flush with no buffer returns *this", Call($"?flush@{Os}QAAAAV12@XZ", bare) == bare);

        long pos = _h.Buffer(24);
        Check("tellp on a failed stream is fpos(-1) through r1", Call($"?tellp@{Os}QAA?AV?$fpos@H@2@XZ", bare, pos) == pos && I64(pos) == -1);

        // The streambuf inserter copies until the source runs dry; it reports the end through
        // snextc, which here forwards to the source's uflow - so the copy is checked up to there.
        long src = NewBuffer("copy me", 0, out _);
        Call($"??6{Os}QAAAAV01@PAV?$basic_streambuf@{T}@1@@Z", os, src);
        long asked = R12Value;
        Check("<< streambuf* copies the get area, then asks the source's underflow (snextc) for more", Out() == "copy me" && asked == Slot(src, 6), Out());
        Reset();

        // endl through a buffer with room: '\n' then flush, which calls sync - a virtual, so the
        // flush is checked by where it forwards rather than run.
        Call("?endl@std@@YAAAV?$basic_ostream@DU?$char_traits@D@std@@@1@AAV21@@Z", os);
        long synced = R12Value;
        Check("endl writes '\\n' and flushes through the buffer's sync (slot 13)", Out() == "\n" && synced == Slot(sb, 13), Out());
        Reset();

        Check("~basic_ostream and the vbase destructor run", Call($"??1{Os}UAA@XZ", os) == 0 && Call($"??_D{Os}QAAXXZ", os) == 0 && U32(ios + 0x30) == 0);
    }

    // ------------------------------------------------------------------
    private void WideOstream()
    {
        long sb = _h.Buffer(0x40);
        Call($"??0{WSb}IAA@XZ", sb);
        long put = _h.Buffer(64);
        Call($"?setp@{WSb}IAAXPA_W0@Z", sb, put, put + 64);
        Check("wide sputc answers the character", Call($"?sputc@{WSb}QAAG_W@Z", sb, 0x263A) == 0x263A && _h.U16(put) == 0x263A);
        Call($"?setp@{WSb}IAAXPA_W0@Z", sb, put, put + 64);
        long os = _h.Buffer(0x50);
        Call($"??0{WOs}QAA@PAV?$basic_streambuf@{WT}@1@_N@Z", os, sb, 0, 1);
        Call($"??6{WOs}QAAAAV01@H@Z", os, 123);
        Check("wostream << int writes wide characters", _h.ReadWide(put, 3) == "123" && Call($"?pptr@{WSb}IBAPA_WXZ", sb) == put + 6);
        Check("wide fill() is L' '", Call($"?fill@?$basic_ios@{WT}@std@@QBA_WXZ", os + 8) == ' ');
        long src = _h.Wide("ab");
        Check("wide xsputn takes a count of elements", Words64(Call($"?xsputn@{WSb}MAA_JPB_W_J@Z", sb, src, 2, 0)) == 2 && _h.ReadWide(put, 5) == "123ab");
    }

    // ------------------------------------------------------------------
    private void Istream()
    {
        long sb = NewBuffer("  42 -7 3.5e2 true 0x1F abc 99999999999 0 1.#X|", 0, out _);
        long ist = _h.Buffer(0x58);
        Call($"??0{Is}QAA@PAV?$basic_streambuf@{T}@1@_N@Z", ist, sb, 0, 1);
        long ios = ist + 0x10;
        Check("istream(sb) most-derived: ios at +0x10, _Chcount 0", U32(U32(ist) + 4) == 0x10 && U32(ios + 0x38) == sb && I64(ist + 8) == 0);

        long v = _h.Buffer(8);
        Check(">> int skips whitespace", Call($"??5{Is}QAAAAV01@AAH@Z", ist, v) == ist && (int)U32(v) == 42 && U32(ios + 0xC) == 0);
        Call($"??5{Is}QAAAAV01@AAF@Z", ist, v);
        Check(">> short", (short)_h.U16(v) == -7);
        Call($"??5{Is}QAAAAV01@AAN@Z", ist, v);
        Check(">> double", BitConverter.ToDouble(_e.ReadMemory(v, 8)) == 350.0);
        Call($"?setf@{B}QAAHH@Z", ios, 0x4000);
        Call($"??5{Is}QAAAAV01@AA_N@Z", ist, v);
        Check(">> bool with boolalpha reads \"true\"", _e.ReadMemory(v, 1)[0] == 1 && U32(ios + 0xC) == 0);
        Call($"?unsetf@{B}QAAXH@Z", ios, 0xE00 | 0x4000);
        Call($"??5{Is}QAAAAV01@AAH@Z", ist, v);
        Check(">> int with no basefield reads 0x1F as 31", U32(v) == 31);
        Call($"?setf@{B}QAAHH@Z", ios, 0x200);
        _e.WriteUInt32(v, 0x55555555);
        Call($"??5{Is}QAAAAV01@AAH@Z", ist, v);
        Check(">> int on \"abc\" fails and leaves the target alone", U32(ios + 0xC) == 2 && U32(v) == 0x55555555);
        Call($"?clear@{Ios}QAAXH_N@Z", ios, 0, 0);
        long word = _h.Buffer(64);
        Call($"??5{Is}QAAAAV01@AAH@Z", ist, v);   // "abc" again: fails, nothing consumed
        Call($"?clear@{Ios}QAAXH_N@Z", ios, 0, 0);
        Call($"?ignore@{Is}QAAAAV12@_JH@Z", ist, 0, 3, 0, ' ');
        Check("ignore(3, ' ') stops after the delimiter-free three", I64(ist + 8) == 3 && Call($"?peek@{Is}QAAHXZ", ist) == ' ');
        Call($"??5{Is}QAAAAV01@AAH@Z", ist, v);
        Check(">> int of 99999999999 is out of range: failbit", U32(ios + 0xC) == 2);
        Call($"?clear@{Ios}QAAXH_N@Z", ios, 0, 0);
        Call($"??5{Is}QAAAAV01@AA_N@Z", ist, v);
        Check(">> bool without boolalpha reads 0 as false", U32(ios + 0xC) == 0 && _e.ReadMemory(v, 1)[0] == 0);
        _e.WriteMemory(v, BitConverter.GetBytes(-1.0f));
        Call($"??5{Is}QAAAAV01@AAM@Z", ist, v);
        Check(">> float takes \"1.\" and stops at '#'", BitConverter.ToSingle(_e.ReadMemory(v, 4)) == 1.0f && Call($"?peek@{Is}QAAHXZ", ist) == '#');

        Check("get() reads one and counts it", Call($"?get@{Is}QAAHXZ", ist) == '#' && Words64(Call($"?gcount@{Is}QBA_JXZ", ist)) == 1);
        Call($"?unget@{Is}QAAAAV12@XZ", ist);
        Check("unget puts it back", Call($"?peek@{Is}QAAHXZ", ist) == '#');
        Call($"?get@{Is}QAAAAV12@AAD@Z", ist, word);
        Check("get(char&)", _e.ReadMemory(word, 1)[0] == '#');
        Call($"?putback@{Is}QAAAAV12@D@Z", ist, '#');
        Check("putback of the right char", Call($"?peek@{Is}QAAHXZ", ist) == '#' && U32(ios + 0xC) == 0);

        // getline / get on a second buffer.
        long lines = NewBuffer("line one\nline two\nabcdef\nxyz|", 0, out _);
        long ls = _h.Buffer(0x58);
        Call($"??0{Is}QAA@PAV?$basic_streambuf@{T}@1@_N@Z", ls, lines, 0, 1);
        long lios = ls + 0x10;
        Call($"?getline@{Is}QAAAAV12@PAD_J@Z", ls, word, 64, 0);
        Check("getline reads a line and extracts the delimiter", _h.ReadNarrow(word) == "line one" && I64(ls + 8) == 9 && U32(lios + 0xC) == 0);
        Call($"?get@{Is}QAAAAV12@PAD_J@Z", ls, word, 4, 0);
        Check("get(s, 4) stores three, no failbit", _h.ReadNarrow(word) == "lin" && I64(ls + 8) == 3 && U32(lios + 0xC) == 0);
        Call($"?getline@{Is}QAAAAV12@PAD_J@Z", ls, word, 64, 0);
        Call($"?get@{Is}QAAAAV12@PAD_JD@Z", ls, word, 64, 0, 'd');
        Check("get(s, n, 'd') stops before the delimiter", _h.ReadNarrow(word) == "abc" && Call($"?peek@{Is}QAAHXZ", ls) == 'd');
        Call($"?ignore@{Is}QAAAAV12@_JH@Z", ls, 0, 0xFFFFFFFF, 0x7FFFFFFF, '\n');
        Call($"?getline@{Is}QAAAAV12@PAD_J@Z", ls, word, 3, 0);
        Check("getline that fills n-1 sets failbit", _h.ReadNarrow(word) == "xy" && U32(lios + 0xC) == 2);
        Call($"?clear@{Ios}QAAXH_N@Z", lios, 0, 0);

        // tellg on a failed stream, readsome from the buffer.
        long pos = _h.Buffer(24);
        Call($"?setstate@{Ios}QAAXH_N@Z", lios, 2, 0);
        Check("tellg on a failed stream is fpos(-1) through r1", Call($"?tellg@{Is}QAA?AV?$fpos@H@2@XZ", ls, pos) == pos && I64(pos) == -1);
        Call($"?clear@{Ios}QAAXH_N@Z", lios, 0, 0);

        // readsome reads only what in_avail() promises.
        long rs = NewBuffer("0123456789", 0, out _);
        long ri = _h.Buffer(0x58);
        Call($"??0{Is}QAA@PAV?$basic_streambuf@{T}@1@_N@Z", ri, rs, 0, 1);
        Call($"?gbump@{Sb}IAAXH@Z", rs, 6);
        Call($"?readsome@{Is}QAA_JPAD_J@Z", ri, word, 64, 0);
        Check("readsome(s, 64) reads min(64, in_avail() = 4) through xsgetn",
            R12Value == Slot(rs, 8) && (_e.ReadRegister(Harness.R2) & 0xFFFFFFFF) == 4 && (_e.ReadRegister(Harness.R1) & 0xFFFFFFFF) == word);
    }

    // ------------------------------------------------------------------
    private void IosBase()
    {
        long sb = NewBuffer("", 16, out _);
        long os = NewOstream(sb);
        long ios = os + 8;
        Check("flags() and flags(f) answer the old flags", Call($"?flags@{B}QBAHXZ", ios) == 0x201 && Call($"?flags@{B}QAAHH@Z", ios, 0x801) == 0x201 && Call($"?flags@{B}QBAHXZ", ios) == 0x801);
        Check("setf(f, mask) only touches the mask", Call($"?setf@{B}QAAHHH@Z", ios, 0x200, 0xE00) == 0x801 && Call($"?flags@{B}QBAHXZ", ios) == 0x201);
        Check("width(n) / precision(n) answer the old 64-bit value",
            Words64(Call($"?width@{B}QAA_J_J@Z", ios, 0, 9, 0)) == 0 && Words64(Call($"?width@{B}QBA_JXZ", ios)) == 9 &&
            Words64(Call($"?precision@{B}QAA_J_J@Z", ios, 0, 3, 0)) == 6 && Words64(Call($"?precision@{B}QBA_JXZ", ios)) == 3);
        Call($"?setstate@{Ios}QAAXH_N@Z", ios, 1, 0);
        Check("eofbit alone: eof, not fail, operator void* non-null",
            Call($"?eof@{B}QBA_NXZ", ios) == 1 && Call($"?fail@{B}QBA_NXZ", ios) == 0 && Call($"??B{B}QBAPAXXZ", ios) == ios && Call($"?good@{B}QBA_NXZ", ios) == 0);
        Call($"?setstate@{Ios}QAAXH_N@Z", ios, 2, 0);
        Check("failbit: fail, operator! true, operator void* null", Call($"?fail@{B}QBA_NXZ", ios) == 1 && Call($"??7{B}QBA_NXZ", ios) == 1 && Call($"??B{B}QBAPAXXZ", ios) == 0 && Call($"?rdstate@{B}QBAHXZ", ios) == 3);
        Call($"?clear@{Ios}QAAXH_N@Z", ios, 0, 0);
        Check("exceptions() reads the mask", Call($"?exceptions@{B}QBAHXZ", ios) == 0);

        int x1 = (int)Call($"?xalloc@{B}SAHXZ"), x2 = (int)Call($"?xalloc@{B}SAHXZ");
        long iw = Call($"?iword@{B}QAAAAJH@Z", ios, x1);
        _e.WriteUInt32(iw, 1234);
        long pw = Call($"?pword@{B}QAAAAPAXH@Z", ios, x2);
        Check("xalloc numbers slots; iword/pword are per-index storage, zero at first",
            x2 == x1 + 1 && U32(pw) == 0 && U32(Call($"?iword@{B}QAAAAJH@Z", ios, x1)) == 1234 && Call($"?pword@{B}QAAAAPAXH@Z", ios, x1) == iw + 4);
        Call($"?register_callback@{B}QAAXP6AXW4event@12@AAV12@H@ZH@Z", ios, 0x00412345, 7);
        Check("register_callback pushes { next, index, fn }", U32(U32(ios + 0x2C) + 4) == 7 && U32(U32(ios + 0x2C) + 8) == 0x00412345);
        _e.WriteUInt32(ios + 0x2C, 0);   // the callback is not code; keep the destructor from calling it

        Check("tie(os) answers the old tie, tie() reads it", Call($"?tie@{Ios}QAAPAV?$basic_ostream@{T}@2@PAV32@@Z", ios, 0x5000) == 0 && Call($"?tie@{Ios}QBAPAV?$basic_ostream@{T}@2@XZ", ios) == 0x5000);
        _e.WriteUInt32(ios + 0x3C, 0);
        long other = NewBuffer("", 4, out _);
        Check("rdbuf(sb) swaps and clears", Call($"?rdbuf@{Ios}QAAPAV?$basic_streambuf@{T}@2@PAV32@@Z", ios, other) == sb && Call($"?rdbuf@{Ios}QBAPAV?$basic_streambuf@{T}@2@XZ", ios) == other);
        Check("narrow / widen in \"C\"", Call($"?narrow@{Ios}QBADDD@Z", ios, 'q', '?') == 'q' && Call($"?narrow@?$basic_ios@{WT}@std@@QBAD_WD@Z", ios, 0x263A, '?') == '?' && Call($"?widen@{Ios}QBADD@Z", ios, 'w') == 'w');

        long loc = _h.Buffer(4);
        Check("ios_base::getloc copies the stream's locale", Call($"?getloc@{B}QBA?AVlocale@2@XZ", ios, loc) == loc && U32(loc) == U32(U32(ios + 0x30)));
        long locale = _h.Buffer(4);
        long empty = _h.Buffer(4);
        Call("?empty@locale@std@@SA?AV12@XZ", empty);
        _e.WriteUInt32(locale, U32(empty));
        long old = _h.Buffer(4);
        Call($"?imbue@{B}QAA?AVlocale@2@ABV32@@Z", ios, old, locale);
        Check("ios_base::imbue installs the locale and answers the old one", U32(U32(ios + 0x30)) == U32(empty) && U32(old) == U32(loc));

        Check("ios_base::Init constructs", Call($"??0Init@{B}QAA@XZ", 0x6000) == 0x6000);
        Check("sync_with_stdio answers the old setting (true)", Call($"?sync_with_stdio@{B}SA_N_N@Z", 0) == 1 && Call($"?sync_with_stdio@{B}SA_N_N@Z", 1) == 0);
        Call($"?_Ios_base_dtor@{B}CAXPAV12@@Z", ios);
        Check("_Ios_base_dtor frees the lists and the locale", U32(ios + 0x28) == 0 && U32(ios + 0x2C) == 0 && U32(ios + 0x30) == 0);
    }

    // ------------------------------------------------------------------
    private void DataImports()
    {
        StreamLibrary st = _h.S.Streams;
        Check("data imports are recognised",
            StreamLibrary.IsDataImport("?cout@std@@3V?$basic_ostream@DU?$char_traits@D@std@@@1@A") &&
            StreamLibrary.IsDataImport("?wcerr@std@@3V?$basic_ostream@_WU?$char_traits@_W@std@@@1@A") &&
            StreamLibrary.IsDataImport("?id@?$ctype@D@std@@2V0locale@2@A") &&
            StreamLibrary.IsDataImport("?_BADOFF@std@@3_JB") &&
            StreamLibrary.IsDataImport("??_7ios_base@std@@6B@") &&
            StreamLibrary.IsDataImport("??_7?$codecvt@_WDH@std@@6B@") &&
            !StreamLibrary.IsDataImport("?_Xbad_alloc@std@@YAXXZ") &&
            !StreamLibrary.IsDataImport("??_7exception@std@@6B@"));
        Check("every iostream/locale variable the image imports has its IAT slot filled",
            _e.StreamDataImports.All(d => U32(d.Slot) != 0),
            $"{_e.StreamDataImports.Count} imported: {string.Join(", ", _e.StreamDataImports.Where(d => U32(d.Slot) == 0).Select(d => d.Name))}");
        foreach ((string name, long slot) in _e.StreamDataImports.Where(d => d.Name == "?_BADOFF@std@@3_JB"))
        {
            Check("the image's own _BADOFF slot reads -1", I64(U32(slot)) == -1);
        }

        long badoff = st.BuildDataImport("?_BADOFF@std@@3_JB");
        Check("_BADOFF is the 64-bit -1", I64(badoff) == -1);
        long id = st.BuildDataImport("?id@?$codecvt@DDH@std@@2V0locale@2@A");
        Check("a facet id starts at 0 and is numbered on first use", U32(id) == 0 && Call("??Bid@locale@std@@QAAIXZ", id) != 0);
        long vt = st.BuildDataImport("??_7facet@locale@std@@6B@");
        Check("??_7facet is a vtable of three Thumb entries", vt != 0 && Enumerable.Range(0, 3).All(i => (U32(vt + (i * 4)) & 1) == 1));
        Check("??_7ios_base is a vtable", (U32(st.BuildDataImport("??_7ios_base@std@@6B@")) & 1) == 1);

        long cout = st.BuildDataImport("?cout@std@@3V?$basic_ostream@DU?$char_traits@D@std@@@1@A");
        long cerr = st.BuildDataImport("?cerr@std@@3V?$basic_ostream@DU?$char_traits@D@std@@@1@A");
        long cin = st.BuildDataImport("?cin@std@@3V?$basic_istream@DU?$char_traits@D@std@@@1@A");
        Check("cout is a good ostream; the same object every time", cout != 0 && U32(cout + 8 + 0xC) == 0 && st.BuildDataImport("?cout@std@@3V?$basic_ostream@DU?$char_traits@D@std@@@1@A") == cout);
        Check("cerr is unitbuf and tied to cout; cin is tied to cout", (U32(cerr + 8 + 0x14) & 2) != 0 && U32(cerr + 8 + 0x3C) == cout && U32(cin + 0x10 + 0x3C) == cout);
        Call($"??6{Os}QAAAAV01@H@Z", cout, 2026);
        Call("?endl@std@@YAAAV?$basic_ostream@DU?$char_traits@D@std@@@1@AAV21@@Z", cout);
        Check("cout << 2026 << endl reaches the log", st.StandardOutput.Contains("stdout: 2026"), string.Join(" | ", st.StandardOutput));
        Call($"?put@{Os}QAAAAV12@D@Z", cerr, 'E');
        Check("cerr is unit-buffered: a put is logged at once", st.StandardOutput.Contains("stderr: E"), string.Join(" | ", st.StandardOutput));
        long v = _h.Buffer(4);
        Call($"??5{Is}QAAAAV01@AAH@Z", cin, v);
        Check("cin is at its end: >> int sets eof|fail", U32(cin + 0x10 + 0xC) == 3);
        long wcout = st.BuildDataImport("?wcout@std@@3V?$basic_ostream@_WU?$char_traits@_W@std@@@1@A");
        Call($"??6{WOs}QAAAAV01@H@Z", wcout, 7);
        Call("?endl@std@@YAAAV?$basic_ostream@_WU?$char_traits@_W@std@@@1@AAV21@@Z", wcout);
        Check("wcout writes wide text to the log", st.StandardOutput.Contains("stdout: 7"));
        Check("~ios_base on a standard stream leaves it open", Call($"?_Ios_base_dtor@{B}CAXPAV12@@Z", cout + 8) == 0 && U32(cout + 8 + 0x30) != 0);
    }
}
