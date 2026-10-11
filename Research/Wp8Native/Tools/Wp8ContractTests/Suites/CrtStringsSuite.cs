using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// MSVCR110 strings, characters, multibyte/wide conversion, number&lt;-&gt;text and formatted text
/// (CrtStrings, CrtFloatText and the PrintfFormatter changes), checked against the documented
/// contracts in the "C" locale.
/// </summary>
public sealed class CrtStringsSuite : IContractSuite
{
    public string Name => "crt strings";

    private const long Truncate = 0xFFFFFFFF;
    private const int Einval = 22, Erange = 34, Eilseq = 42, Enomem = 12, Struncate = 80;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Call(string fn, params long[] a) => h.Call(fn, a);
        int I(long value) => unchecked((int)(uint)value);
        long N(string text) => h.Narrow(text);
        long W(string text) => h.Wide(text);
        long Latin1(string text)
        {
            long p = e.AllocateHeap(text.Length + 1);
            e.WriteMemory(p, [.. Encoding.Latin1.GetBytes(text), 0]);
            return p;
        }

        long errnoAddress = Call("_errno");
        void ClearErrno() => e.WriteUInt32(errnoAddress, 0);
        long Errno() => e.ReadUInt32(errnoAddress);
        long Word() => h.Buffer(8);
        long[] D(double value)
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            return [(long)(bits & 0xFFFFFFFF), (long)(bits >> 32)];
        }

        long VaList(params long[] words)
        {
            long p = h.Buffer(Math.Max(words.Length, 1) * 4);
            for (int i = 0; i < words.Length; i++) e.WriteUInt32(p + (i * 4), (uint)words[i]);
            return p;
        }

        string Narrow(long p, int max = 256) => Encoding.Latin1.GetString(e.ReadMemory(p, max)).Split('\0')[0];
        string Wide(long p, int max = 256) => h.ReadWide(p, max);
        long Result64() => (long)((ulong)e.ReadRegister(Harness.R0) & 0xFFFFFFFF | ((ulong)h.R1Value << 32));

        // ---------------------------------------------------------------- characters
        Check("iswdigit('5')", Call("iswdigit", '5') != 0);
        Check("iswdigit('a') is 0", Call("iswdigit", 'a') == 0);
        Check("iswdigit(Arabic-Indic three)", Call("iswdigit", 0x0663) != 0);
        Check("iswspace(' ') and ideographic space", Call("iswspace", ' ') != 0 && Call("iswspace", 0x3000) != 0);
        Check("iswspace('x') is 0", Call("iswspace", 'x') == 0);
        Check("iswspace(WEOF) is 0", Call("iswspace", 0xFFFF) == 0);
        Check("iswupper: A and É yes, a no", Call("iswupper", 'A') != 0 && Call("iswupper", 0xC9) != 0 && Call("iswupper", 'a') == 0);
        Check("iswlower(é)", Call("iswlower", 0xE9) != 0);
        Check("iswalpha(CJK)", Call("iswalpha", 0x4E2D) != 0);
        Check("towupper('a') == 'A'", Call("towupper", 'a') == 'A');
        Check("towupper(é) unchanged in the C locale", Call("towupper", 0xE9) == 0xE9);
        Check("towlower('Z') == 'z'", Call("towlower", 'Z') == 'z');
        Check("towlower(WEOF) == WEOF", Call("towlower", 0xFFFF) == 0xFFFF);
        Check("_isctype('A', _UPPER)", Call("_isctype", 'A', 1) != 0);
        Check("_isctype('a', _UPPER) is 0", Call("_isctype", 'a', 1) == 0);
        Check("_isctype(EOF, all) is 0", Call("_isctype", 0xFFFFFFFF, 0xFFFF) == 0);
        Check("_isctype(É, _ALPHA) is 0 in the C locale", Call("_isctype", 0xC9, 0x103) == 0);
        Check("tolower(EOF) == EOF", I(Call("tolower", 0xFFFFFFFF)) == -1);
        Check("tolower('A') == 'a'", Call("tolower", 'A') == 'a');
        Check("tolower(É) unchanged in the C locale", Call("tolower", 0xC9) == 0xC9);
        Check("toupper('z') == 'Z'", Call("toupper", 'z') == 'Z');
        Check("_isdigit_l('7', locale)", Call("_isdigit_l", '7', 0) != 0);

        // ---------------------------------------------------------------- narrow strings
        Check("strnlen stops at max", Call("strnlen", N("hello"), 3) == 3);
        Check("strnlen stops at NUL", Call("strnlen", N("hi"), 10) == 2);
        long hay = N("abcdef");
        Check("memchr finds a byte", Call("memchr", hay, 'd', 6) == hay + 3);
        Check("memchr compares (unsigned char)c", Call("memchr", hay, 0x163, 6) == hay + 2);
        Check("memchr respects n", Call("memchr", hay, 'f', 5) == 0);
        Check("_memicmp ignores ASCII case", Call("_memicmp", N("ABC"), N("abc"), 3) == 0);
        Check("_memicmp orders", I(Call("_memicmp", N("ABC"), N("abd"), 3)) < 0);
        ClearErrno();
        Check("_memicmp(NULL) is _NLSCMPERROR + EINVAL", Call("_memicmp", 0, N("a"), 1) == 0x7FFFFFFF && Errno() == Einval);
        Check("_memicmp(NULL, NULL, 0) is 0", Call("_memicmp", 0, 0, 0) == 0);
        Check("_stricmp equal ignoring case", Call("_stricmp", N("Hello"), N("hELLO")) == 0);
        Check("_stricmp folds to lower: '_' < 'A'", I(Call("_stricmp", N("_"), N("A"))) < 0);
        Check("_stricmp does not fold Latin-1 in C", Call("_stricmp", Latin1("É"), Latin1("é")) != 0);
        Check("_strnicmp limits the count", Call("_strnicmp", N("abcX"), N("ABCy"), 3) == 0);

        long d5 = h.Buffer(8);
        Check("strncpy_s _TRUNCATE fits", Call("strncpy_s", d5, 5, N("abc"), Truncate) == 0 && Narrow(d5) == "abc");
        Check("strncpy_s _TRUNCATE cuts: STRUNCATE", Call("strncpy_s", d5, 5, N("abcdef"), Truncate) == Struncate && Narrow(d5) == "abcd");
        Check("strncpy_s count < size copies count", Call("strncpy_s", d5, 5, N("abcdef"), 3) == 0 && Narrow(d5) == "abc");
        ClearErrno();
        Check("strncpy_s too small: ERANGE, emptied", Call("strncpy_s", d5, 5, N("abcde"), 5) == Erange && Narrow(d5) == "" && Errno() == Erange);
        e.WriteMemory(d5, Encoding.ASCII.GetBytes("zz\0"));
        Check("strncpy_s NULL source: EINVAL, emptied", Call("strncpy_s", d5, 5, 0, 2) == Einval && Narrow(d5) == "");
        Check("strncpy_s(NULL, 0, NULL, 0) is 0", Call("strncpy_s", 0, 0, 0, 0) == 0);
        Check("strncpy_s size 0 is EINVAL", Call("strncpy_s", d5, 0, N("x"), 1) == Einval);

        long d6 = h.Buffer(8);
        e.WriteMemory(d6, Encoding.ASCII.GetBytes("ab\0"));
        Check("strncat_s appends count", Call("strncat_s", d6, 6, N("cdef"), 2) == 0 && Narrow(d6) == "abcd");
        Check("strncat_s _TRUNCATE: STRUNCATE", Call("strncat_s", d6, 6, N("xyz"), Truncate) == Struncate && Narrow(d6) == "abcdx");
        e.WriteMemory(d6, Encoding.ASCII.GetBytes("ab\0"));
        Check("strncat_s too small: ERANGE, emptied", Call("strncat_s", d6, 4, N("cd"), 5) == Erange && Narrow(d6) == "");
        e.WriteMemory(d6, Encoding.ASCII.GetBytes("xxxx"));
        Check("strncat_s unterminated destination: EINVAL", Call("strncat_s", d6, 4, N("a"), 1) == Einval && Narrow(d6) == "");

        long d4 = h.Buffer(8);
        ClearErrno();
        Check("strcpy_s too small: ERANGE, emptied, errno", Call("strcpy_s", d4, 3, N("abc")) == Erange && Narrow(d4) == "" && Errno() == Erange);
        Check("strcpy_s fits", Call("strcpy_s", d4, 4, N("abc")) == 0 && Narrow(d4) == "abc");
        Check("strcat_s too small: ERANGE", Call("strcat_s", d4, 5, N("de")) == Erange && Narrow(d4) == "");
        e.WriteMemory(d4, Encoding.ASCII.GetBytes("ab\0"));
        Check("strcat_s fits", Call("strcat_s", d4, 5, N("cd")) == 0 && Narrow(d4) == "abcd");

        long lw = N("HeLLo");
        Check("_strlwr_s lowers in place", Call("_strlwr_s", lw, 6) == 0 && Narrow(lw) == "hello");
        long lw2 = N("HeLLo");
        Check("_strlwr_s unterminated within size: EINVAL", Call("_strlwr_s", lw2, 5) == Einval && Narrow(lw2) == "");

        // ---------------------------------------------------------------- wide strings
        long hello = W("hello");
        Check("wcslen", Call("wcslen", hello) == 5);
        Check("wcsnlen", Call("wcsnlen", hello, 3) == 3 && Call("wcsnlen", hello, 30) == 5);
        Check("wcschr finds", Call("wcschr", hello, 'l') == hello + 4);
        Check("wcschr of NUL finds the terminator", Call("wcschr", hello, 0) == hello + 10);
        Check("wcschr misses", Call("wcschr", hello, 'z') == 0);
        Check("wcsrchr finds the last", Call("wcsrchr", hello, 'l') == hello + 6);
        Check("wcsstr finds", Call("wcsstr", hello, W("ll")) == hello + 4);
        Check("wcsstr of empty is the haystack", Call("wcsstr", hello, W("")) == hello);
        Check("wcsstr misses", Call("wcsstr", hello, W("xyz")) == 0);
        Check("wcspbrk finds the first of a set", Call("wcspbrk", hello, W("ol")) == hello + 4);
        Check("wcscspn", Call("wcscspn", hello, W("lo")) == 2);
        Check("wcsspn", Call("wcsspn", W("aab"), W("a")) == 2);
        Check("wcsncmp equal within n", Call("wcsncmp", W("abc"), W("abd"), 2) == 0);
        Check("wcsncmp orders", I(Call("wcsncmp", W("abc"), W("abd"), 3)) < 0);
        Check("wcscmp is unsigned", I(Call("wcscmp", W("￾"), W("a"))) > 0);
        Check("_wcsicmp equal ignoring case", Call("_wcsicmp", W("ABC"), W("abc")) == 0);
        Check("_wcsicmp orders", I(Call("_wcsicmp", W("a"), W("B"))) < 0);
        ClearErrno();
        Check("_wcsicmp(NULL) is _NLSCMPERROR + EINVAL", Call("_wcsicmp", 0, W("a")) == 0x7FFFFFFF && Errno() == Einval);
        Check("_wcsnicmp limits the count", Call("_wcsnicmp", W("ABCd"), W("abcE"), 3) == 0);
        Check("_wcsicoll is _wcsicmp in C", Call("_wcsicoll", W("Abc"), W("aBC")) == 0);
        Check("wcscoll orders", I(Call("wcscoll", W("a"), W("b"))) < 0);

        long wd = h.Buffer(32);
        Check("wcscpy_s fits", Call("wcscpy_s", wd, 4, W("abc")) == 0 && Wide(wd) == "abc");
        Check("wcscpy_s too small: ERANGE, emptied", Call("wcscpy_s", wd, 3, W("abc")) == Erange && Wide(wd) == "");
        Call("wcscpy_s", wd, 8, W("ab"));
        Check("wcscat_s fits", Call("wcscat_s", wd, 8, W("cd")) == 0 && Wide(wd) == "abcd");
        Check("wcscat_s too small: ERANGE", Call("wcscat_s", wd, 5, W("e")) == Erange && Wide(wd) == "");
        Check("wcsncpy_s _TRUNCATE: STRUNCATE", Call("wcsncpy_s", wd, 4, W("abcdef"), Truncate) == Struncate && Wide(wd) == "abc");
        Check("wcsncpy_s fits", Call("wcsncpy_s", wd, 8, W("abcdef"), 2) == 0 && Wide(wd) == "ab");
        long padded = h.Buffer(16);
        e.WriteMemory(padded, Encoding.Unicode.GetBytes("XXXXXX"));
        Call("wcsncpy", padded, W("ab"), 4);
        Check("wcsncpy pads with NULs to n and no further", h.U16(padded) == 'a' && h.U16(padded + 4) == 0 && h.U16(padded + 6) == 0 && h.U16(padded + 8) == 'X');
        Call("wcscpy_s", wd, 8, W("ab"));
        Check("wcsncat appends n and terminates", Call("wcsncat", wd, W("cdef"), 2) == wd && Wide(wd) == "abcd");
        long dup = Call("_wcsdup", W("hey"));
        Check("_wcsdup copies", dup != 0 && Wide(dup) == "hey");
        Check("_wcsdup(NULL) is NULL", Call("_wcsdup", 0) == 0);
        long tok = W("a,b,,c");
        long delim = W(",");
        long t1 = Call("wcstok", tok, delim), t2 = Call("wcstok", 0, delim), t3 = Call("wcstok", 0, delim), t4 = Call("wcstok", 0, delim);
        Check("wcstok splits, skipping empty fields", Wide(t1) == "a" && Wide(t2) == "b" && Wide(t3) == "c" && t4 == 0);
        long lower = W("ABcÉ");
        Check("_wcslwr is ASCII-only in C", Call("_wcslwr", lower) == lower && Wide(lower) == "abcÉ");

        // ---------------------------------------------------------------- multibyte
        long wc = h.Buffer(4);
        Check("mbtowc of a Latin-1 byte", Call("mbtowc", wc, Latin1("é"), 1) == 1 && h.U16(wc) == 0xE9);
        Check("mbtowc of NUL is 0", Call("mbtowc", wc, N(""), 1) == 0 && h.U16(wc) == 0);
        Check("mbtowc(NULL source) is 0 (no shift states)", Call("mbtowc", wc, 0, 1) == 0);
        Check("mbtowc with n == 0 is 0", Call("mbtowc", wc, N("a"), 0) == 0);
        long mb = h.Buffer(4);
        Check("wctomb of 'A'", Call("wctomb", mb, 'A') == 1 && Narrow(mb) == "A");
        ClearErrno();
        Check("wctomb above 255: -1, EILSEQ", I(Call("wctomb", mb, 0x263A)) == -1 && Errno() == Eilseq);
        Check("wctomb(NULL) is 0", Call("wctomb", 0, 'A') == 0);

        long count = h.Buffer(4);
        long wbuf = h.Buffer(64);
        Check("mbstowcs_s converts and counts the NUL", Call("mbstowcs_s", count, wbuf, 10, N("abc"), Truncate) == 0 && e.ReadUInt32(count) == 4 && Wide(wbuf) == "abc");
        Check("mbstowcs_s too small: ERANGE, emptied", Call("mbstowcs_s", count, wbuf, 3, N("abc"), 3) == Erange && Wide(wbuf) == "");
        Check("mbstowcs_s _TRUNCATE: STRUNCATE", Call("mbstowcs_s", count, wbuf, 3, N("abc"), Truncate) == Struncate && Wide(wbuf) == "ab" && e.ReadUInt32(count) == 3);
        Check("mbstowcs_s count < size converts count", Call("mbstowcs_s", count, wbuf, 10, N("abcdef"), 2) == 0 && Wide(wbuf) == "ab" && e.ReadUInt32(count) == 3);
        Check("mbstowcs_s sizing call", Call("mbstowcs_s", count, 0, 0, N("abcd"), 0) == 0 && e.ReadUInt32(count) == 5);
        Check("mbstowcs_s(NULL dst, size 5) is EINVAL", Call("mbstowcs_s", count, 0, 5, N("a"), 1) == Einval);
        long nbuf = h.Buffer(32);
        Check("wcstombs_s converts", Call("wcstombs_s", count, nbuf, 10, W("abc"), Truncate) == 0 && e.ReadUInt32(count) == 4 && Narrow(nbuf) == "abc");
        Check("wcstombs_s above 255: EILSEQ, emptied", Call("wcstombs_s", count, nbuf, 10, W("a☺"), Truncate) == Eilseq && Narrow(nbuf) == "");
        Check("wcstombs_s sizing call", Call("wcstombs_s", count, 0, 0, W("abcd"), 0) == 0 && e.ReadUInt32(count) == 5);
        Check("wcstombs(NULL) is the length", Call("wcstombs", 0, W("abc"), 0) == 3);
        Check("wcstombs above 255 is -1", I(Call("wcstombs", nbuf, W("a☺"), 10)) == -1);
        Check("wcstombs Latin-1", Call("wcstombs", nbuf, W("été"), 10) == 3 && e.ReadMemory(nbuf, 1)[0] == 0xE9);

        // ---------------------------------------------------------------- text -> numbers
        long end = Word();
        long s1 = N("  -123xyz");
        Check("strtol: whitespace, sign, end", I(Call("strtol", s1, end, 10)) == -123 && e.ReadUInt32(end) == s1 + 6);
        long s2 = N("0x1A");
        Check("strtol base 0 takes 0x", Call("strtol", s2, end, 0) == 26 && e.ReadUInt32(end) == s2 + 4);
        long s3 = N("0x");
        Check("strtol(\"0x\", 16): 0, end at the start (VC11)", Call("strtol", s3, end, 16) == 0 && e.ReadUInt32(end) == s3);
        Check("strtol base 0 octal", Call("strtol", N("077"), 0, 0) == 63);
        ClearErrno();
        long s4 = N("2147483648");
        Check("strtol overflow: LONG_MAX, ERANGE, end past the digits", Call("strtol", s4, end, 10) == 0x7FFFFFFF && Errno() == Erange && e.ReadUInt32(end) == s4 + 10);
        ClearErrno();
        Check("strtol underflow: LONG_MIN", Call("strtol", N("-2147483649"), 0, 10) == 0x80000000 && Errno() == Erange);
        ClearErrno();
        Check("strtol(LONG_MIN) does not overflow", Call("strtol", N("-2147483648"), 0, 10) == 0x80000000 && Errno() == 0);
        Check("strtol base 36", Call("strtol", N("z"), 0, 36) == 35);
        ClearErrno();
        long s5 = N("1");
        Check("strtol base 1: 0, EINVAL, end at the start", Call("strtol", s5, end, 1) == 0 && Errno() == Einval && e.ReadUInt32(end) == s5);
        ClearErrno();
        Check("strtoul(\"-1\") wraps without error", Call("strtoul", N("-1"), 0, 10) == 0xFFFFFFFF && Errno() == 0);
        Check("strtoul overflow: ULONG_MAX, ERANGE", Call("strtoul", N("4294967296"), 0, 10) == 0xFFFFFFFF && Errno() == Erange);
        ClearErrno();
        Call("_strtoi64", N("-9223372036854775808"), 0, 10);
        Check("_strtoi64 of INT64_MIN in r0:r1", Result64() == long.MinValue && Errno() == 0);
        Call("_strtoi64", N("9223372036854775808"), 0, 10);
        Check("_strtoi64 overflow: INT64_MAX, ERANGE", Result64() == long.MaxValue && Errno() == Erange);
        Call("_strtoui64", N("18446744073709551615"), 0, 10);
        Check("_strtoui64 of UINT64_MAX", Result64() == -1);
        long w1 = W("  42abc");
        Check("wcstol: whitespace and end", Call("wcstol", w1, end, 10) == 42 && e.ReadUInt32(end) == w1 + 8);
        Check("wcstol takes fullwidth digits", Call("wcstol", W("１２"), 0, 10) == 12);
        Check("wcstoul hex", Call("wcstoul", W("ff"), 0, 16) == 255);
        Call("_wcstoi64", W("-5"), 0, 10);
        Check("_wcstoi64 negative", Result64() == -5);
        Call("_wcstoui64", W("0xFFFFFFFFFF"), 0, 0);
        Check("_wcstoui64 base 0 hex past 32 bits", Result64() == 0xFFFFFFFFFF);
        ClearErrno();
        Check("atoi clamps with ERANGE", Call("atoi", N("99999999999")) == 0x7FFFFFFF && Errno() == Erange);
        Check("atoi reads the leading integer", Call("atoi", N(" 42z")) == 42);
        Call("_atoi64", N("123456789012"));
        Check("_atoi64", Result64() == 123456789012);
        Check("_wtoi", I(Call("_wtoi", W("-7"))) == -7);
        Check("_wtol", Call("_wtol", W("31x")) == 31);
        Call("_wtoi64", W("-123456789012"));
        Check("_wtoi64", Result64() == -123456789012);

        long f1 = N("  1.5e3x");
        Call("strtod", f1, end);
        Check("strtod: exponent and end", h.D64(0) == 1500 && e.ReadUInt32(end) == f1 + 7);
        Call("strtod", N("1.5d2"), 0);
        Check("strtod accepts a d exponent (VC11)", h.D64(0) == 150);
        long f2 = N("1e");
        Call("strtod", f2, end);
        Check("strtod: a bare e is not an exponent", h.D64(0) == 1 && e.ReadUInt32(end) == f2 + 1);
        long f3 = N("inf");
        Call("strtod", f3, end);
        Check("strtod(\"inf\") is no number in VC11", h.D64(0) == 0 && e.ReadUInt32(end) == f3);
        ClearErrno();
        Call("strtod", N("1e999"), 0);
        Check("strtod overflow: HUGE_VAL, ERANGE", double.IsPositiveInfinity(h.D64(0)) && Errno() == Erange);
        ClearErrno();
        Call("strtod", N("1e-999"), 0);
        Check("strtod underflow: 0, ERANGE", h.D64(0) == 0 && Errno() == Erange);
        Call("strtod", N("-.5e1"), 0);
        Check("strtod(\"-.5e1\")", h.D64(0) == -5);

        // The number parsers read in growing steps now (ReadNumberText); a number longer than the
        // first step, and one whose exponent straddles it, must parse exactly as before.
        string longMantissa = new string(' ', 60) + "1" + new string('0', 100) + "e-98;tail";
        long f4 = N(longMantissa);
        Call("strtod", f4, end);
        Check("strtod: 60 spaces and a 101-digit mantissa past the first read step", h.D64(0) == 100 && e.ReadUInt32(end) == f4 + longMantissa.IndexOf(';'));
        string straddle = new string('0', 62) + "1e+5 x";
        long f5 = N(straddle);
        Call("strtod", f5, end);
        Check("strtod: an exponent that straddles the read step", h.D64(0) == 100000 && e.ReadUInt32(end) == f5 + straddle.IndexOf(' '));
        ClearErrno();
        long s6 = N(new string('9', 80) + "!");
        Check("strtol: 80 digits overflow, end past all of them", Call("strtol", s6, end, 10) == 0x7FFFFFFF && Errno() == Erange && e.ReadUInt32(end) == s6 + 80);
        Check("strncmp: equal in the first n, different after", Call("strncmp", N("abcdef" + new string('x', 200)), N("abcdeg"), 5) == 0);
        Check("strncmp: different inside the first n", I(Call("strncmp", N("abcdef"), N("abcdeg"), 6)) < 0);
        Check("strncmp: a shorter string compares lower inside n", I(Call("strncmp", N("abc"), N("abcd"), 10)) < 0);
        Call("wcstod", W(" 2.25"), 0);
        Check("wcstod", h.D64(0) == 2.25);
        Call("_wtof", W("-3.5"));
        Check("_wtof", h.D64(0) == -3.5);
        Call("atof", N("7.25"));
        Check("atof", h.D64(0) == 7.25);
        Call("_atof_l", N("0.125"), 0);
        Check("_atof_l", h.D64(0) == 0.125);
        Call("_strtod_l", N("6.5"), 0, 0);
        Check("_strtod_l", h.D64(0) == 6.5);

        // ---------------------------------------------------------------- numbers -> text
        long nb = h.Buffer(80);
        Check("_itoa negative base 10", Call("_itoa", unchecked((uint)-255), nb, 10) == nb && Narrow(nb) == "-255");
        Call("_itoa", 0xFFFFFFFF, nb, 16);
        Check("_itoa negative base 16 is unsigned", Narrow(nb) == "ffffffff");
        Check("_itoa_s too small: ERANGE, emptied", Call("_itoa_s", 12345, nb, 5, 10) == Erange && Narrow(nb) == "");
        Check("_itoa_s fits", Call("_itoa_s", 12345, nb, 6, 10) == 0 && Narrow(nb) == "12345");
        Check("_itoa_s radix 1: EINVAL", Call("_itoa_s", 5, nb, 6, 1) == Einval);
        long wb = h.Buffer(160);
        Check("_itow_s negative", Call("_itow_s", unchecked((uint)-5), wb, 3, 10) == 0 && Wide(wb) == "-5");
        Check("_itow_s negative needs room for the sign", Call("_itow_s", unchecked((uint)-5), wb, 2, 10) == Erange);
        Check("_ltow_s binary", Call("_ltow_s", 5, wb, 8, 2) == 0 && Wide(wb) == "101");
        Check("_ultow_s max", Call("_ultow_s", 0xFFFFFFFF, wb, 11, 10) == 0 && Wide(wb) == "4294967295");
        long big = -1234567890123;
        Check("_i64tow_s (value in r0:r1)", Call("_i64tow_s", big & 0xFFFFFFFF, (big >> 32) & 0xFFFFFFFF, wb, 20, 10) == 0 && Wide(wb) == "-1234567890123");
        Check("_i64tow_s base 16 of -1", Call("_i64tow_s", 0xFFFFFFFF, 0xFFFFFFFF, wb, 20, 16) == 0 && Wide(wb) == "ffffffffffffffff");
        Check("_ui64tow_s base 2 of max", Call("_ui64tow_s", 0xFFFFFFFF, 0xFFFFFFFF, wb, 65, 2) == 0 && Wide(wb, 80) == new string('1', 64));
        Check("_ui64tow_s too small", Call("_ui64tow_s", 0xFFFFFFFF, 0xFFFFFFFF, wb, 64, 2) == Erange && Wide(wb) == "");

        // ---------------------------------------------------------------- _ecvt / _fcvt / _gcvt
        long dec = Word(), sign = Word();
        h.SetD64(0, 3.1415926535);
        long r = Call("_ecvt", 10, dec, sign);
        Check("_ecvt(pi, 10) (MSDN's example)", Narrow(r) == "3141592654" && e.ReadUInt32(dec) == 1 && e.ReadUInt32(sign) == 0, Narrow(r));
        h.SetD64(0, -0.00123);
        r = Call("_ecvt", 3, dec, sign);
        Check("_ecvt negative small", Narrow(r) == "123" && I(e.ReadUInt32(dec)) == -2 && e.ReadUInt32(sign) == 1);
        h.SetD64(0, 9.99);
        r = Call("_ecvt", 2, dec, sign);
        Check("_ecvt rounding carries into the point", Narrow(r) == "10" && e.ReadUInt32(dec) == 2, Narrow(r));
        h.SetD64(0, 3.1415926535);
        r = Call("_fcvt", 7, dec, sign);
        Check("_fcvt(pi, 7) (MSDN's example)", Narrow(r) == "31415927" && e.ReadUInt32(dec) == 1, Narrow(r));
        h.SetD64(0, 123.456);
        r = Call("_fcvt", 2, dec, sign);
        Check("_fcvt(123.456, 2)", Narrow(r) == "12346" && e.ReadUInt32(dec) == 3, Narrow(r));
        h.SetD64(0, 0.006);
        r = Call("_fcvt", 2, dec, sign);
        Check("_fcvt(0.006, 2) rounds up to one digit", Narrow(r) == "1" && I(e.ReadUInt32(dec)) == -1, Narrow(r));
        h.SetD64(0, 0.1);
        r = Call("_ecvt", 20, dec, sign);
        Check("_ecvt: 17 digits, then zeros", Narrow(r) == "10000000000000001000", Narrow(r));
        h.SetD64(0, 1.25);
        Check("_ecvt_s too small: ERANGE", Call("_ecvt_s", nb, 4, 5, dec, sign) == Erange && Narrow(nb) == "");
        h.SetD64(0, 1.25);
        Check("_ecvt_s fits", Call("_ecvt_s", nb, 8, 5, dec, sign) == 0 && Narrow(nb) == "12500");
        (double Value, string Text)[] gcvt =
        [
            (-1234567890.123, "-1234567890.12"), (-12345678901.23, "-12345678901.2"),
            (-123456789012.3, "-123456789012."), (-1234567890123, "-1.23456789012e+012"),
            (-12.34567890123, "-12.3456789012"), (-0.1234567890123, "-0.123456789012"),
            (-0.01234567890123, "-1.23456789012e-002"),
        ];
        foreach ((double value, string text) in gcvt)
        {
            h.SetD64(0, value);
            long rc = Call("_gcvt_s", nb, 64, 12);
            Check($"_gcvt_s({value}, 12) == \"{text}\" (MSDN's table)", rc == 0 && Narrow(nb) == text, Narrow(nb));
        }

        h.SetD64(0, 1.0);
        Check("_gcvt_s keeps the point", Call("_gcvt_s", nb, 16, 5) == 0 && Narrow(nb) == "1.");
        h.SetD64(0, 1.5);
        Check("_gcvt_s digits >= size: ERANGE", Call("_gcvt_s", nb, 4, 4) == Erange && Narrow(nb) == "");
        h.SetD64(0, 123456.0);
        Check("_gcvt_s result too long: ERANGE, emptied", Call("_gcvt_s", nb, 5, 3) == Erange && Narrow(nb) == "");

        // ---------------------------------------------------------------- printf
        long pb = h.Buffer(128);
        long rs = Call("sprintf_s", pb, 32, N("%d|%5.2f|%s"), 42, D(3.14159)[0], D(3.14159)[1], N("x"));
        Check("sprintf_s int, double, string", I(rs) == 10 && Narrow(pb) == "42| 3.14|x", Narrow(pb));
        ClearErrno();
        Check("sprintf_s too small: -1, emptied, ERANGE", I(Call("sprintf_s", pb, 4, N("%d"), 12345)) == -1 && Narrow(pb) == "" && Errno() == Erange);
        Check("sprintf_s(NULL) is -1", I(Call("sprintf_s", 0, 4, N("x"))) == -1);
        string Fmt(string format, double value)
        {
            Call("sprintf_s", pb, 128, N(format), 0, D(value)[0], D(value)[1]);
            return Narrow(pb);
        }

        // The double follows a padding word in r3: a variadic double takes an even core position.
        Check("%e has a three-digit exponent (VC11)", Fmt("%e", 1e6) == "1.000000e+006", Fmt("%e", 1e6));
        Check("%g large", Fmt("%g", 1e20) == "1e+020", Fmt("%g", 1e20));
        Check("%g 0.0001", Fmt("%g", 0.0001) == "0.0001");
        Check("%g 100000", Fmt("%g", 100000) == "100000");
        Check("%g 1000000", Fmt("%g", 1000000) == "1e+006");
        Check("%.20f: zeros after 17 digits (VC11)", Fmt("%.20f", 0.1) == "0.10000000000000001000", Fmt("%.20f", 0.1));
        Check("%f of infinity", Fmt("%f", double.PositiveInfinity) == "1.#INF00", Fmt("%f", double.PositiveInfinity));
        Check("%g of infinity", Fmt("%g", double.NegativeInfinity) == "-1.#INF");
        Check("%f of 0/0 is -1.#IND00", Fmt("%f", double.NaN) == "-1.#IND00", Fmt("%f", double.NaN));
        Check("%#.0f keeps the point", Fmt("%#.0f", 2.0) == "2.");
        Check("%.2f rounds half up on decimal digits", Fmt("%.2f", 0.125) == "0.13", Fmt("%.2f", 0.125));
        Check("%E", Fmt("%.2E", -12345.678) == "-1.23E+004");
        // 70000 in r3, 0 at [sp], a pad word, the __int64 at the even pair [sp+8], then the string.
        Call("sprintf_s", pb, 64, N("%hd|%.0d|%I64d|%5s"), 70000, 0, 0, 0, 2, N("ab"));
        Check("%hd truncates, %.0d of 0 is empty, %I64d, width", Narrow(pb) == "4464||8589934592|   ab", Narrow(pb));
        Call("sprintf_s", pb, 64, N("%ls|%S|%c"), W("wide"), W("W2"), 'q');
        Check("%ls and %S read wide in a narrow function", Narrow(pb) == "wide|W2|q", Narrow(pb));
        ClearErrno();
        Check("a narrow function cannot print a wide char above 255", I(Call("sprintf_s", pb, 64, N("%ls"), W("☺"))) == -1 && Errno() == Eilseq);
        Check("vsprintf", Call("vsprintf", pb, N("%x"), VaList(255)) == 2 && Narrow(pb) == "ff");
        Check("_snprintf_s count < size cuts at count", I(Call("_snprintf_s", pb, 10, 4, N("%s"), N("abcdef"))) == -1 && Narrow(pb) == "abcd");
        Check("_vsnprintf_l", Call("_vsnprintf_l", pb, 20, N("%u"), 0, VaList(3000000000)) == 10 && Narrow(pb) == "3000000000");
        Check("_scprintf", Call("_scprintf", N("%s!"), N("hello")) == 6);

        long ww = h.Buffer(128);
        Check("swprintf_s", Call("swprintf_s", ww, 32, W("%s=%d"), W("ab"), 5) == 4 && Wide(ww) == "ab=5");
        Call("swprintf_s", ww, 32, W("%S|%hs|%c|%C"), N("n1"), N("n2"), 0x263A, 'z');
        Check("wide %S/%hs narrow, %c wide, %C narrow", Wide(ww) == "n1|n2|☺|z", Wide(ww));
        ClearErrno();
        Check("swprintf_s too small: -1, emptied, ERANGE", I(Call("swprintf_s", ww, 3, W("abc"))) == -1 && Wide(ww) == "" && Errno() == Erange);
        e.WriteMemory(ww, Encoding.Unicode.GetBytes("ZZZZZZZZ"));
        Check("_snwprintf cut: -1, no terminator", I(Call("_snwprintf", ww, 3, W("abcdef"))) == -1 && Wide(ww, 4) == "abcZ");
        e.WriteMemory(ww, Encoding.Unicode.GetBytes("ZZZZZZZZ"));
        Check("_snwprintf exact: count, no terminator", Call("_snwprintf", ww, 6, W("abcdef")) == 6 && Wide(ww, 7) == "abcdefZ");
        Check("_snwprintf room: terminated", Call("_snwprintf", ww, 10, W("abcdef")) == 6 && Wide(ww) == "abcdef");
        Check("_snwprintf(NULL, 0) is the length", Call("_snwprintf", 0, 0, W("abcdef")) == 6);
        long va = VaList(W("abcdefg"));
        Check("_vsnwprintf_s _TRUNCATE keeps what fits", I(Call("_vsnwprintf_s", ww, 5, Truncate, W("%s"), va)) == -1 && Wide(ww) == "abcd");
        Check("_vsnwprintf_s count < size", I(Call("_vsnwprintf_s", ww, 5, 3, W("%s"), va)) == -1 && Wide(ww) == "abc");
        ClearErrno();
        Check("_vsnwprintf_s too small: emptied, ERANGE", I(Call("_vsnwprintf_s", ww, 5, 10, W("%s"), va)) == -1 && Wide(ww) == "" && Errno() == Erange);
        Check("_vsnwprintf_s fits", Call("_vsnwprintf_s", ww, 10, Truncate, W("%s"), va) == 7 && Wide(ww) == "abcdefg");
        Check("_vsnwprintf", Call("_vsnwprintf", ww, 10, W("%d-%d"), VaList(1, 2)) == 3 && Wide(ww) == "1-2");
        Check("_vswprintf_c_l cut: -1, terminated", I(Call("_vswprintf_c_l", ww, 4, W("%d"), 0, VaList(12345))) == -1 && Wide(ww) == "123");
        Check("_vswprintf_c_l fits", Call("_vswprintf_c_l", ww, 8, W("%d"), 0, VaList(12345)) == 5 && Wide(ww) == "12345");
        Check("_swprintf_c", Call("_swprintf_c", ww, 8, W("%x"), 0xBEEF) == 4 && Wide(ww) == "beef");
        Check("_vswprintf (non-conforming)", Call("_vswprintf", ww, W("<%s>"), VaList(W("q"))) == 3 && Wide(ww) == "<q>");
        Check("vswprintf_s", Call("vswprintf_s", ww, 16, W("%05d"), VaList(42)) == 5 && Wide(ww) == "00042");
        Check("_scwprintf", Call("_scwprintf", W("%d"), 1234) == 4);
        long vd = h.Buffer(16);
        e.WriteMemory(vd, BitConverter.GetBytes(2.5));
        Check("vswprintf_s reads a double from a va_list", Call("vswprintf_s", ww, 16, W("%.1f"), vd) == 3 && Wide(ww) == "2.5");

        // ---------------------------------------------------------------- scanf
        long si = Word(), sf = Word(), sd = h.Buffer(8), sw = h.Buffer(64), sn = h.Buffer(64);
        Check("swscanf_s %d %s %f", Call("swscanf_s", W("12 abc 3.5"), W("%d %s %f"), si, sw, 16, sf) == 3
            && e.ReadUInt32(si) == 12 && Wide(sw) == "abc" && BitConverter.ToSingle(e.ReadMemory(sf, 4)) == 3.5f);
        ClearErrno();
        Check("swscanf_s field too big: stops, emptied, ENOMEM", Call("swscanf_s", W("toolong"), W("%s"), sw, 3) == 0 && Wide(sw) == "" && Errno() == Enomem);
        long i1 = Word(), i2 = Word(), i3 = Word();
        Check("sscanf %i takes prefixes", Call("sscanf", N("0x1f 077 -5"), N("%i %i %i"), i1, i2, i3) == 3
            && e.ReadUInt32(i1) == 31 && e.ReadUInt32(i2) == 63 && I(e.ReadUInt32(i3)) == -5);
        Check("sscanf %[a-z] range", Call("sscanf", N("abc123"), N("%[a-z]%d"), sn, si) == 2 && Narrow(sn) == "abc" && e.ReadUInt32(si) == 123);
        Check("sscanf of empty input is EOF", I(Call("sscanf", N(""), N("%d"), si)) == -1);
        Check("sscanf matching failure is 0", Call("sscanf", N("x"), N("%d"), si) == 0);
        Check("sscanf literal", Call("sscanf", N("10:20"), N("%d:%d"), i1, i2) == 2 && e.ReadUInt32(i2) == 20);
        Check("sscanf %lf", Call("sscanf", N("3.25e2"), N("%lf"), sd) == 1 && BitConverter.ToDouble(e.ReadMemory(sd, 8)) == 325);
        e.WriteMemory(sn, Encoding.ASCII.GetBytes("ZZZZ"));
        Check("sscanf %2c is unterminated", Call("sscanf", N("abc"), N("%2c"), sn) == 1 && Narrow(sn, 4) == "abZZ");
        Check("sscanf %n and %*d", Call("sscanf", N("12 34"), N("%*d %d%n"), i1, i2) == 1 && e.ReadUInt32(i1) == 34 && e.ReadUInt32(i2) == 5);
        Check("sscanf %ls writes wide", Call("sscanf", N("hey"), N("%ls"), sw) == 1 && Wide(sw) == "hey");
        Check("sscanf_s %s with room", Call("sscanf_s", N("ok rest"), N("%s"), sn, 8) == 1 && Narrow(sn) == "ok");
        Check("sscanf %I64d", Call("sscanf", N("-8589934592"), N("%I64d"), sd) == 1 && BitConverter.ToInt64(e.ReadMemory(sd, 8)) == -8589934592);

        // ---------------------------------------------------------------- error text
        Check("strerror(ENOENT)", Narrow(Call("strerror", 2)) == "No such file or directory");
        Check("strerror(EBUSY) is the CRT's own wording", Narrow(Call("strerror", 16)) == "Resource device");
        Check("strerror out of range", Narrow(Call("strerror", 999)) == "Unknown error");
        Check("_wcserror_s truncates silently", Call("_wcserror_s", ww, 8, 22) == 0 && Wide(ww) == "Invalid");
        Check("strerror_s(NULL) is EINVAL", Call("strerror_s", 0, 8, 2) == Einval);
        Check("_wcserror", Wide(Call("_wcserror", 42)) == "Illegal byte sequence");
    }
}
