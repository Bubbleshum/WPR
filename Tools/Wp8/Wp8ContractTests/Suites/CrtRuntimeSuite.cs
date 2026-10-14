using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// MSVCR110's runtime (CrtRuntime.cs) and the rest of its stdio and low-level I/O
/// (FileLibrary.Crt.cs): floating-point control and status, memory, time, process control, C++
/// EH and RTTI support, threads, streams, descriptors and paths - against the VC11 CRT's
/// documented behaviour and .NET's answers.
/// </summary>
public sealed class CrtRuntimeSuite : IContractSuite
{
    public string Name => "crt runtime";

    private const string Local = @"C:\Data\Users\DefApps\AppData\{E94059A2-135C-420E-8E60-BCDA5FC3EC30}\Local\";
    private const int Fpscr = 6;   // UC_ARM_REG_FPSCR
    private const long Eof = 0xFFFFFFFF;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        HostStubs s = h.S;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Call(string fn, params long[] a) => h.Call(fn, a);
        long Narrow(string text) => h.Narrow(text);
        long Wide(string text) => h.Wide(text);
        long Errno() => h.Errno();
        long Pointer(long address) => e.ReadUInt32(address);
        bool Terminates(Action action)
        {
            int before = s.CrtTerminations.Count;
            action();
            return s.CrtTerminations.Count > before;
        }

        string Sandbox(string name) => Path.Combine(h.Sandbox, name);

        // ---- floating point ----
        Call("_fpreset");
        Check("_controlfp(0, 0) is the default word: all exceptions masked, round to nearest", Call("_controlfp", 0, 0) == 0x8001F, $"0x{Call("_controlfp", 0, 0):X}");
        long cw = Call("_controlfp", 0x300, 0x300);
        Check("_controlfp(_RC_CHOP) sets FPSCR RMode to RZ", (cw & 0x300) == 0x300 && ((e.Cpu.RegRead(Fpscr) >> 22) & 3) == 3, $"cw=0x{cw:X} fpscr=0x{e.Cpu.RegRead(Fpscr):X}");
        Call("_controlfp", 0x200, 0x300);
        Check("_RC_UP is RP (1)", ((e.Cpu.RegRead(Fpscr) >> 22) & 3) == 1);
        Call("_controlfp", 0x100, 0x300);
        Check("_RC_DOWN is RM (2)", ((e.Cpu.RegRead(Fpscr) >> 22) & 3) == 2);
        Check("unmasking reads back masked (no FP traps, as on the phone's VFP)", (Call("_controlfp", 0, 0x8001F) & 0x8001F) == 0x8001F);
        cw = Call("_controlfp", 0x01000000, 0x03000000);
        Check("_DN_FLUSH sets FPSCR.FZ", (cw & 0x03000000) == 0x01000000 && (e.Cpu.RegRead(Fpscr) & 0x01000000) != 0);
        long current = h.Buffer(4);
        Check("_controlfp_s applies and reports", Call("_controlfp_s", current, 0, 0x300) == 0 && (e.ReadUInt32(current) & 0x300) == 0);
        long r = 0;
        Check("_controlfp_s with _MCW_PC is an invalid parameter (EINVAL)", Terminates(() => r = Call("_controlfp_s", current, 0x20000, 0x30000)) && r == 22);
        Call("_fpreset");
        Check("_fpreset restores the default word", Call("_controlfp", 0, 0) == 0x8001F && (e.Cpu.RegRead(Fpscr) & 0x03C00000) == 0);
        e.Cpu.RegWrite(Fpscr, e.Cpu.RegRead(Fpscr) | 0x11 | 0x80);
        Check("_statusfp reads the sticky flags", Call("_statusfp") == (0x10 | 0x1 | 0x80000));
        Check("_clearfp returns invalid|inexact|denormal", Call("_clearfp") == (0x10 | 0x1 | 0x80000));
        Check("... and clears them", Call("_statusfp") == 0 && (e.Cpu.RegRead(Fpscr) & 0x9F) == 0);

        (double Value, long Class)[] classes =
        [
            (1.0, 0x100), (-1.0, 0x8), (0.0, 0x40), (-0.0, 0x20), (double.PositiveInfinity, 0x200),
            (double.NegativeInfinity, 0x4), (double.NaN, 0x2), (BitConverter.Int64BitsToDouble(0x7FF0000000000001), 0x1),
            (double.Epsilon, 0x80), (-double.Epsilon, 0x10),
        ];
        foreach ((double value, long wantClass) in classes)
        {
            h.SetD64(0, value);
            long got = Call("_fpclass");
            Check($"_fpclass(0x{BitConverter.DoubleToInt64Bits(value):X16}) = 0x{wantClass:X}", got == wantClass, $"0x{got:X}");
        }

        h.SetD64(0, 3); h.SetD64(1, -0.0);
        Call("_copysign");
        Check("_copysign(3, -0.0) = -3", h.D64(0) == -3);
        h.SetD64(0, 3); h.SetD64(1, 4);
        Call("_hypot");
        Check("_hypot(3, 4) = 5", h.D64(0) == 5);
        Call("_set_errno", 0);
        h.SetD64(0, 1.5e308); h.SetD64(1, 1.5e308);
        Call("_hypot");
        Check("_hypot overflowing is inf with ERANGE", double.IsPositiveInfinity(h.D64(0)) && Errno() == 34);
        h.SetS32(0, 5); h.SetS32(1, 12);
        Call("_hypotf");
        Check("_hypotf(5, 12) = 13", h.S32(0) == 13f);

        long quot = h.Buffer(8);
        Check("ldiv returns its hidden result pointer", Call("ldiv", quot, unchecked((uint)-7), 2) == quot);
        Check("ldiv(-7, 2) = { -3, -1 }", (int)e.ReadUInt32(quot) == -3 && (int)e.ReadUInt32(quot + 4) == -1);
        Call("div", quot, 17, 5);
        Check("div(17, 5) = { 3, 2 } through the hidden pointer", e.ReadUInt32(quot) == 3 && e.ReadUInt32(quot + 4) == 2);
        Check("div by zero ends the process", Terminates(() => Call("div", quot, 1, 0)));

        // ---- memory ----
        long src = Narrow("0123456789"), dst = h.Buffer(16);
        Check("memcpy_s copies", Call("memcpy_s", dst, 16, src, 10) == 0 && h.ReadNarrow(dst) == "0123456789");
        Check("memcpy_s of nothing is 0", Call("memcpy_s", 0, 0, 0, 0) == 0);
        Check("memcpy_s that does not fit: ERANGE, the destination zeroed", Terminates(() => r = Call("memcpy_s", dst, 4, src, 10)) && r == 34 && e.ReadMemory(dst, 4).All(b => b == 0));
        e.WriteMemory(dst, Encoding.ASCII.GetBytes("abcdefgh\0"));
        Check("memmove_s handles overlap", Call("memmove_s", dst + 2, 14, dst, 6) == 0 && h.ReadNarrow(dst) == "ababcdef");
        Check("memmove_s with a NULL source is EINVAL", Terminates(() => r = Call("memmove_s", dst, 16, 0, 4)) && r == 22);

        long block = Call("malloc", 16);
        e.WriteMemory(block, Enumerable.Repeat((byte)0xAA, 16).ToArray());
        long grown = Call("_recalloc", block, 4, 16);
        byte[] g = e.ReadMemory(grown, 64);
        Check("_recalloc keeps the old bytes and zeroes the rest", grown != 0 && g.Take(16).All(b => b == 0xAA) && g.Skip(16).All(b => b == 0));
        Check("_msize covers the request", Call("_msize", grown) >= 64, $"{Call("_msize", grown)}");
        Check("_expand to a smaller size stays put", Call("_expand", grown, 8) == grown);
        Check("_expand beyond the block is NULL with ENOMEM", Call("_expand", grown, 1 << 20) == 0 && Errno() == 12);
        Check("_msize(NULL) is an invalid parameter", Terminates(() => r = Call("_msize", 0)) && r == 0xFFFFFFFF);
        Check("_recalloc(NULL, n, size) allocates zeroed", Call("_recalloc", 0, 2, 8) is var z && z != 0 && e.ReadMemory(z, 16).All(b => b == 0));

        // ---- time ----
        long tm = h.Buffer(36);
        void SetTm(params int[] fields)
        {
            for (int i = 0; i < 9; i++)
            {
                e.WriteUInt32(tm + (i * 4), (uint)(i < fields.Length ? fields[i] : 0));
            }
        }

        int TmField(int i) => (int)e.ReadUInt32(tm + (i * 4));
        long Return64() => (e.ReadRegister(Harness.R0) & 0xFFFFFFFF) | (h.R1Value << 32);

        SetTm(0, 0, 0, 1, 0, 70);
        Call("_mkgmtime64", tm);
        Check("_mkgmtime64(1970-01-01) = 0", Return64() == 0);
        SetTm(30, 61, 25, 32, 12, 100);
        Call("_mkgmtime64", tm);
        DateTime expected = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(12).AddDays(31).AddHours(25).AddMinutes(61).AddSeconds(30);
        Check("_mkgmtime64 normalises out-of-range fields as .NET does", Return64() == new DateTimeOffset(expected).ToUnixTimeSeconds(), $"{Return64()} vs {expected:O}");
        Check("... and writes the normalised tm back, with wday and yday",
            TmField(5) + 1900 == expected.Year && TmField(4) == expected.Month - 1 && TmField(3) == expected.Day && TmField(2) == expected.Hour &&
            TmField(1) == expected.Minute && TmField(0) == expected.Second && TmField(6) == (int)expected.DayOfWeek && TmField(7) == expected.DayOfYear - 1);
        SetTm(0, 0, 0, 1, 0, 1101);
        Call("_mkgmtime64", tm);
        Check("_mkgmtime64 past the year 3000 is -1", Return64() == -1);

        long t = h.Buffer(8);
        e.WriteUInt64(t, 0);
        Check("_ctime64(0)", h.ReadNarrow(Call("_ctime64", t)) == "Thu Jan 01 00:00:00 1970\n");
        e.WriteUInt64(t, 1_000_000_000);
        Check("_wctime64(1e9)", h.ReadWide(Call("_wctime64", t)) == "Sun Sep 09 01:46:40 2001\n");
        e.WriteUInt64(t, unchecked((ulong)-5));
        Check("_ctime64 of a negative time is NULL with EINVAL", Call("_ctime64", t) == 0 && Errno() == 22);
        SetTm(40, 46, 1, 9, 8, 101, 0, 251);
        Check("asctime", h.ReadNarrow(Call("asctime", tm)) == "Sun Sep 09 01:46:40 2001\n");

        long text = h.Buffer(256);
        (string Format, string Expected)[] formats =
        [
            ("%a %A %b %B", "Sun Sunday Sep September"),
            ("%d %H %I %j %m %M %p %S", "09 01 01 252 09 46 AM 40"),
            ("%U %w %W %y %Y %%", "36 0 36 01 2001 %"),
            ("%c", "09/09/01 01:46:40"),
            ("%x|%X", "09/09/01|01:46:40"),
            ("%#d %#H %#j", "9 1 252"),
            ("%#x", "Sunday, September 9, 2001"),
            ("%Z", "Coordinated Universal Time"),
        ];
        foreach ((string format, string want) in formats)
        {
            long n = Call("strftime", text, 256, Narrow(format), tm);
            Check($"strftime(\"{format}\")", h.ReadNarrow(text) == want && n == want.Length, h.ReadNarrow(text));
        }

        Check("strftime that does not fit is 0 with ERANGE", Call("strftime", text, 4, Narrow("%Y-%m"), tm) == 0 && Errno() == 34);
        Check("strftime with an unknown directive is an invalid parameter", Terminates(() => r = Call("strftime", text, 256, Narrow("%Q"), tm)) && r == 0);
        long wtext = h.Buffer(256);
        Check("wcsftime", Call("wcsftime", wtext, 128, Wide("%Y-%m-%d %H:%M"), tm) == 16 && h.ReadWide(wtext) == "2001-09-09 01:46");

        Check("__timezone and __daylight are 0 (UTC)", e.ReadUInt32(Call("__timezone")) == 0 && e.ReadUInt32(Call("__daylight")) == 0);
        long size = h.Buffer(4);
        Check("_get_tzname sizes the name", Call("_get_tzname", size, 0, 0, 0) == 0 && e.ReadUInt32(size) == 27);
        Check("_get_tzname writes the name", Call("_get_tzname", size, text, 64, 1) == 0 && h.ReadNarrow(text) == "Coordinated Universal Time");
        long timeb = h.Buffer(16);
        Call("_ftime64", timeb);
        Check("_ftime64 is now", Math.Abs(BitConverter.ToInt64(e.ReadMemory(timeb, 8)) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) <= 5);

        // ---- process, environment, locale ----
        Check("_set_invalid_parameter_handler returns the previous one", Call("_set_invalid_parameter_handler", 0x1235) == 0 && Call("_set_invalid_parameter_handler", 0) == 0x1235);
        Check("signal(SIGINT, SIG_IGN) returns SIG_DFL", Call("signal", 2, 1) == 0);
        Check("signal(SIGINT, SIG_DFL) returns SIG_IGN", Call("signal", 2, 0) == 1);
        Check("SIGABRT_COMPAT (6) is SIGABRT", Call("signal", 6, 0x401001) == 0 && Call("signal", 22, 0) == 0x401001);
        Check("signal of an unknown signal is SIG_ERR (invalid parameter)", Terminates(() => r = Call("signal", 99, 0)) && r == 0xFFFFFFFF);
        Check("_XcptFilter(access violation) continues the search", Call("_XcptFilter", 0xC0000005, 0) == 0);
        Call("signal", 11, 1);
        Check("... and with SIGSEGV ignored, continues execution", Call("_XcptFilter", 0xC0000005, 0) == 0xFFFFFFFF);
        Call("signal", 11, 0);
        Check("__CxxFrameHandler3 answers ExceptionContinueSearch and is recorded", Call("__CxxFrameHandler3", 0, 0, 0, 0) == 1 && s.CrtUnreachableCalls.Any(c => c.StartsWith("__CxxFrameHandler3")));
        Check("getenv finds nothing (no environment)", Call("getenv", Narrow("PATH")) == 0);
        Check("system(NULL) says there is no command processor", Call("system", 0) == 0);
        Check("system(cmd) fails with ENOENT", Call("system", Narrow("dir")) == Eof && Errno() == 2);
        Check("_popen fails with ENOENT", Call("_popen", Narrow("dir"), Narrow("r")) == 0 && Errno() == 2);
        Check("setlocale(LC_ALL, NULL) is \"C\"", h.ReadNarrow(Call("setlocale", 0, 0)) == "C");
        Check("setlocale(LC_ALL, \"\") is the native locale, \"C\"", h.ReadNarrow(Call("setlocale", 0, Narrow(""))) == "C");
        Check("setlocale to an unavailable locale is NULL", Call("setlocale", 2, Narrow("French_France.1252")) == 0);
        Check("setlocale with a bad category is an invalid parameter", Terminates(() => r = Call("setlocale", 9, Narrow("C"))) && r == 0);
        Check("_configthreadlocale: query, enable, query, disable",
            Call("_configthreadlocale", 0) == 2 && Call("_configthreadlocale", 1) == 2 && Call("_configthreadlocale", 0) == 1 && Call("_configthreadlocale", 2) == 1);
        Check("__sys_nerr is 43", e.ReadUInt32(Call("__sys_nerr")) == 43);
        Call("_set_errno", 5);
        Check("_set_errno sets errno", Errno() == 5);
        Call("perror", Narrow("reading"));
        Check("perror writes \"s: message\"", s.Extras.Printed.Contains("reading: Input/output error"));
        Check("perror reaches the log as a whole line", s.Extras.StandardOutputLines.Contains("reading: Input/output error"));
        long iob = Call("__iob_func");
        long stdOut = iob + 0x20;
        // Lua's print: one fputs per argument, a tab between, then the newline - one line in the log.
        Call("fputs", Narrow("[string \"menu\"]:12: attempt to index nil"), stdOut);
        Call("fputs", Narrow("\t"), stdOut);
        Call("fputs", Narrow("x\n"), stdOut);
        Check("fputs(stdout) pieces reach the log as one line",
            s.Extras.StandardOutputLines.Contains("[string \"menu\"]:12: attempt to index nil\tx"), string.Join(" | ", s.Extras.StandardOutputLines));
        Check("fputs(stdout) still fails: an app has no console", Call("fputs", Narrow("y"), stdOut) == Eof);
        Call("fputc", '\n', stdOut);
        Check("fputc(stdout) ends the pending line", s.Extras.StandardOutputLines.Contains("y"));
        Call("fwrite", Narrow("from fwrite\n"), 1, 12, iob + 0x40);
        Check("fwrite(stderr) reaches the log", s.Extras.StandardOutputLines.Contains("from fwrite"));
        long random = h.Buffer(8);
        Check("rand_s returns 0", Call("rand_s", random) == 0 && Call("rand_s", random + 4) == 0);
        Check("rand_s gives different numbers", e.ReadUInt32(random) != e.ReadUInt32(random + 4));
        Check("rand_s(NULL) is an invalid parameter", Terminates(() => r = Call("rand_s", 0)) && r == 22);
        Check("_amsg_exit ends the process with its runtime error", Terminates(() => Call("_amsg_exit", 31)) && s.CrtTerminations[^1].Contains("R6031"));
        Check("_wassert ends the process naming the assertion",
            Terminates(() => Call("_wassert", Wide("x > 0"), Wide("game.cpp"), 42)) && s.CrtTerminations[^1].Contains("x > 0") && s.CrtTerminations[^1].Contains("line 42"));
        Check("_invalid_parameter_noinfo_noreturn ends the process", Terminates(() => Call("_invalid_parameter_noinfo_noreturn")));
        Check("bsearch over nothing is NULL", Call("bsearch", Narrow("k"), h.Buffer(4), 0, 4, 0x401001) == 0);
        Check("__uncaught_exception is 0 with nothing in flight", Call("__uncaught_exception") == 0);
        Check("getchar and _getch read EOF (no console)", Call("getchar") == Eof && Call("_getch") == Eof);

        // ---- type_info and RTTI ----
        long TypeInfo(string decorated)
        {
            long p = h.Buffer(8 + decorated.Length + 1);
            e.WriteMemory(p + 8, Encoding.ASCII.GetBytes(decorated + "\0"));
            return p;
        }

        long foo = TypeInfo(".?AVFoo@ns@@"), fooCopy = TypeInfo(".?AVFoo@ns@@"), bar = TypeInfo(".?AUBar@@");
        Check("type_info == compares names (copies in two modules are equal)", Call("??8type_info@@QBA_NABV0@@Z", foo, fooCopy) == 1 && Call("??8type_info@@QBA_NABV0@@Z", foo, bar) == 0);
        Check("type_info != is the inverse", Call("??9type_info@@QBA_NABV0@@Z", foo, bar) == 1);
        long node = h.Buffer(8);
        (string Decorated, string Name)[] names =
        [
            (".?AVFoo@ns@@", "class ns::Foo"),
            (".?AUBar@@", "struct Bar"),
            (".?AW4Color@gfx@@", "enum gfx::Color"),
            (".H", "int"),
            (".PAX", "void *"),
            (".PBD", "char const *"),
            (".?AV?$vector@HV?$allocator@H@std@@@std@@", "class std::vector<int,class std::allocator<int> >"),
            (".?AV?$Array@$02@@", "class Array<3>"),
            (".?AVInner@Outer@@", "class Outer::Inner"),
            (".?AV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@", "class std::basic_string<char,struct std::char_traits<char>,class std::allocator<char> >"),
        ];
        foreach ((string decorated, string want) in names)
        {
            long info = TypeInfo(decorated);
            long name = Call("?name@type_info@@QBAPBDPAU__type_info_node@@@Z", info, node);
            Check($"type_info::name({decorated})", h.ReadNarrow(name) == want, h.ReadNarrow(name));
        }

        long first = Call("?name@type_info@@QBAPBDPAU__type_info_node@@@Z", foo, node);
        Check("type_info::name caches in _M_data", Call("?name@type_info@@QBAPBDPAU__type_info_node@@@Z", foo, node) == first && Pointer(foo + 4) == first);
        Check("names are linked onto the module's list", Pointer(node + 4) != 0);
        Call("__clean_type_info_names_internal", node);
        Check("__clean_type_info_names_internal empties the list", Pointer(node + 4) == 0);

        long locator = h.Buffer(20), vtable = h.Buffer(16), instance = h.Buffer(8);
        e.WriteUInt32(locator + 12, (uint)foo);
        e.WriteUInt32(vtable, (uint)locator);
        e.WriteUInt32(instance, (uint)(vtable + 4));
        Check("__RTtypeid reads the type from the object's locator", Call("__RTtypeid", instance) == foo);

        // ---- std exceptions ----
        long ex = h.Buffer(12);
        Check("bad_cast(const char*) returns this", Call("??0bad_cast@std@@QAA@PBD@Z", ex, Narrow("bad cast")) == ex);
        Check("... with a vtable and a copied message", Pointer(ex) != 0 && h.ReadNarrow(Pointer(ex + 4)) == "bad cast" && e.ReadMemory(ex + 8, 1)[0] == 1);
        long ex2 = h.Buffer(12);
        Call("??0bad_cast@std@@QAA@ABV01@@Z", ex2, ex);
        Check("bad_cast's copy owns its own copy of the message", Pointer(ex2 + 4) != Pointer(ex + 4) && h.ReadNarrow(Pointer(ex2 + 4)) == "bad cast");
        Call("??1bad_cast@std@@UAA@XZ", ex2);
        Check("~bad_cast releases the message", Pointer(ex2 + 4) == 0);
        long message = Narrow("borrowed"), messageRef = h.Buffer(4), ex3 = h.Buffer(12);
        e.WriteUInt32(messageRef, (uint)message);
        Call("??0exception@std@@QAA@ABQBDH@Z", ex3, messageRef, 1);
        Check("exception(const char* const&, int) keeps the pointer without copying", Pointer(ex3 + 4) == message && e.ReadMemory(ex3 + 8, 1)[0] == 0);

        // ---- exception_ptr ----
        long ep = h.Buffer(8), ep2 = h.Buffer(8);
        e.WriteMemory(ep, [1, 2, 3, 4, 5, 6, 7, 8]);
        Call("?__ExceptionPtrCreate@@YAXPAX@Z", ep);
        Check("exception_ptr starts empty", Pointer(ep) == 0 && Call("?__ExceptionPtrToBool@@YA_NPBX@Z", ep) == 0);
        Call("?__ExceptionPtrCurrentException@@YAXPAX@Z", ep);
        Check("current_exception outside a catch is empty", Call("?__ExceptionPtrToBool@@YA_NPBX@Z", ep) == 0);

        // A ThrowInfo for a 12-byte class with no copy constructor: { attributes, unwind, compat,
        // catchable array }, everything image-relative.
        long imageBase = e.ImageBase;
        long typeDescriptor = TypeInfo(".?AVGameError@@");
        long catchable = h.Buffer(28);
        e.WriteUInt32(catchable + 4, (uint)(typeDescriptor - imageBase));
        e.WriteUInt32(catchable + 20, 12);
        long array = h.Buffer(8);
        e.WriteUInt32(array, 1);
        e.WriteUInt32(array + 4, (uint)(catchable - imageBase));
        long throwInfo = h.Buffer(16);
        e.WriteUInt32(throwInfo + 12, (uint)(array - imageBase));
        long thrownObject = h.Buffer(12);
        e.WriteMemory(thrownObject, Encoding.ASCII.GetBytes("ABCDEFGHIJK\0"));
        Call("?__ExceptionPtrCopyException@@YAXPAXPBX1@Z", ep, thrownObject, throwInfo);
        Check("make_exception_ptr holds something", Call("?__ExceptionPtrToBool@@YA_NPBX@Z", ep) == 1);
        Call("?__ExceptionPtrCopy@@YAXPAXPBX@Z", ep2, ep);
        Check("copying shares it", Pointer(ep2) == Pointer(ep));
        Call("?__ExceptionPtrDestroy@@YAXPAX@Z", ep);
        Check("destroying one leaves the copy", Call("?__ExceptionPtrToBool@@YA_NPBX@Z", ep2) == 1);   // the destroyed one's storage is unspecified
        Call("?__ExceptionPtrAssign@@YAXPAXPBX@Z", ep, ep2);
        Check("assigning shares it again", Pointer(ep) == Pointer(ep2));

        // ---- threads ----
        long threadId = h.Buffer(4);
        long thread = Call("_beginthreadex", 0, 0, 0x401001, 0x55, 0, threadId);
        Check("_beginthreadex returns a handle and a thread id", thread != 0 && e.ReadUInt32(threadId) > 0x1000, $"handle 0x{thread:X} id 0x{e.ReadUInt32(threadId):X}");
        Check("a running thread is not signalled", Call("WaitForSingleObject", thread, 0) == 0x102);
        Check("CloseHandle closes it", Call("CloseHandle", thread) == 1);
        long suspended = Call("_beginthreadex", 0, 0, 0x401001, 0x55, 4, threadId);
        Check("CREATE_SUSPENDED: ResumeThread returns the suspend count", Call("ResumeThread", suspended) == 1 && Call("ResumeThread", suspended) == 0);
        Check("_beginthreadex with no start is an invalid parameter", Terminates(() => r = Call("_beginthreadex", 0, 0, 0, 0, 0, 0)) && r == 0);
        Check("a non-thread handle still reaches the sync library", Call("WaitForSingleObject", 0x12345678, 0) == 0);

        // ---- streams ----
        long fpOut = h.Buffer(4);
        Check("fopen_s(w) creates a file", Call("fopen_s", fpOut, Narrow(Local + "t1.txt"), Narrow("w")) == 0 && Pointer(fpOut) != 0);
        long fp = Pointer(fpOut);
        foreach (char c in "a\r\nb")
        {
            Call("fputc", c, fp);
        }

        Call("fclose", fp);
        Check("fputc wrote the bytes", File.ReadAllText(Sandbox("t1.txt")) == "a\r\nb");
        fp = Call("fopen", Narrow(Local + "t1.txt"), Narrow("r"));
        Check("text mode reads CR LF as LF", Call("fgetc", fp) == 'a' && Call("fgetc", fp) == '\n' && Call("ftell", fp) == 3 && Call("fgetc", fp) == 'b');
        Check("fgetc at the end is EOF and sets feof", Call("fgetc", fp) == Eof && Call("feof", fp) == 1);
        Call("clearerr", fp);
        Check("clearerr clears end of file", Call("feof", fp) == 0);
        Call("fclose", fp);
        fp = Call("fopen", Narrow(Local + "t1.txt"), Narrow("rb"));
        Check("binary mode reads the CR", Call("fgetc", fp) == 'a' && Call("fgetc", fp) == '\r');
        Check("ungetc of the same character steps back", Call("ungetc", '\r', fp) == '\r' && Call("ftell", fp) == 1 && Call("fgetc", fp) == '\r');
        Check("ungetc of another character is read next", Call("ungetc", 'Z', fp) == 'Z' && Call("fgetc", fp) == 'Z' && Call("fgetc", fp) == '\n');
        Call("fclose", fp);

        File.WriteAllText(Sandbox("lines.txt"), "line1\r\nline2\nlast");
        fp = Call("fopen", Narrow(Local + "lines.txt"), Narrow("rt"));
        long line = h.Buffer(64);
        Check("fgets reads a line, newline translated", Call("fgets", line, 64, fp) == line && h.ReadNarrow(line) == "line1\n");
        Check("fgets reads the next", Call("fgets", line, 64, fp) == line && h.ReadNarrow(line) == "line2\n");
        Check("fgets stops at count - 1", Call("fgets", line, 3, fp) == line && h.ReadNarrow(line) == "la");
        Check("fgets reads the unterminated tail", Call("fgets", line, 64, fp) == line && h.ReadNarrow(line) == "st");
        Check("fgets at the end is NULL", Call("fgets", line, 64, fp) == 0);
        Call("rewind", fp);
        Check("rewind goes back to the start", Call("fgetc", fp) == 'l');
        long pos = h.Buffer(8);
        Check("fgetpos/fsetpos round-trip", Call("fgetpos", fp, pos) == 0 && BitConverter.ToInt64(e.ReadMemory(pos, 8)) == 1 && Call("fgetc", fp) == 'i' && Call("fsetpos", fp, pos) == 0 && Call("fgetc", fp) == 'i');
        Call("_fseeki64", fp, 0xDEAD, 7, 0, 0);
        Call("_ftelli64", fp);
        Check("_fseeki64/_ftelli64 with 64-bit positions", e.ReadRegister(Harness.R0) == 7 && h.R1Value == 0 && Call("fgetc", fp) == 'l');
        Check("fseek before the start is EINVAL", Call("fseek", fp, unchecked((uint)-100), 1) == Eof && Errno() == 22);
        Check("fwrite to a read-only stream fails and sets ferror", Call("fwrite", line, 1, 1, fp) == 0 && Call("ferror", fp) == 1);
        Call("clearerr", fp);
        Check("clearerr clears the error", Call("ferror", fp) == 0);
        Check("setvbuf accepts a valid mode", Call("setvbuf", fp, 0, 4, 0) == 0);
        Check("setvbuf with a bad mode is an invalid parameter", Terminates(() => r = Call("setvbuf", fp, 0, 99, 512)) && r == Eof);
        Call("fclose", fp);

        Check("fopen of a missing file is NULL with ENOENT", Call("fopen", Narrow("no/such/file.txt"), Narrow("r")) == 0 && Errno() == 2);
        Check("fopen with a bad mode is an invalid parameter", Terminates(() => r = Call("fopen", Narrow(Local + "x.txt"), Narrow("rw"))) && r == 0);

        File.WriteAllText(Sandbox("app.txt"), "12");
        fp = Call("fopen", Narrow(Local + "app.txt"), Narrow("a+"));
        Call("fseek", fp, 0, 0);
        Check("a+ reads from where it is asked", Call("fgetc", fp) == '1');
        Call("fputc", 'x', fp);
        Call("fwrite", Narrow("yz"), 1, 2, fp);
        Call("fclose", fp);
        Check("a+ writes at the end, fputc and fwrite alike", File.ReadAllText(Sandbox("app.txt")) == "12xyz", File.ReadAllText(Sandbox("app.txt")));
        fp = Call("fopen", Narrow(Local + "app.txt"), Narrow("a"));
        Check("plain append opens (FileMode.Append used to throw)", fp != 0);
        Call("fputs", Narrow("!"), fp);
        Call("fclose", fp);
        Check("and appends", File.ReadAllText(Sandbox("app.txt")) == "12xyz!");

        string assetDir = Path.GetDirectoryName(h.Image)!;
        string asset = Directory.EnumerateFiles(assetDir, "*", SearchOption.TopDirectoryOnly)
            .First(f => new FileInfo(f).Length is > 100 and < 1_000_000 && !f.EndsWith(".dll") && !f.EndsWith(".exe") && !f.Contains("original") && !f.Contains("wpr_"));
        string assetName = Path.GetFileName(asset);
        byte[] assetBytes = File.ReadAllBytes(asset);
        fp = Call("fopen", Narrow(assetName), Narrow("r+b"));
        Check("r+ of a packaged file opens a sandbox copy with its contents", fp != 0 && Call("fgetc", fp) == assetBytes[0]);
        Call("fclose", fp);
        Check("... and the package is untouched", File.ReadAllBytes(asset).SequenceEqual(assetBytes) && File.Exists(Sandbox(assetName)));

        fp = Call("tmpfile");
        Check("tmpfile opens a file", fp != 0 && Call("fputc", 'q', fp) == 'q');
        string[] temps = Directory.GetFiles(h.Sandbox, "t*.tmp");
        Call("fclose", fp);
        Check("which is removed when closed", temps.Length == 1 && !File.Exists(temps[0]));
        Check("tmpnam names a file", h.ReadNarrow(Call("tmpnam", 0)).StartsWith(@"\s"));
        long template = Narrow("saveXXXXXX");
        Check("_mktemp fills in the template", Call("_mktemp", template) == template && h.ReadNarrow(template) is var made && made.Length == 10 && made[4] == 'a' && made[5..].All(char.IsDigit));
        Check("_mktemp rejects a template without six X", Call("_mktemp", Narrow("saveXX")) == 0 && Errno() == 22);

        fp = Call("fopen", Narrow(Local + "u8.txt"), Narrow("w, ccs=UTF-8"));
        Call("fputwc", 0x20AC, fp);
        Call("fclose", fp);
        Check("ccs=UTF-8 writes a BOM and UTF-8", File.ReadAllBytes(Sandbox("u8.txt")).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF, 0xE2, 0x82, 0xAC }));
        fp = Call("fopen", Narrow(Local + "u8.txt"), Narrow("r, ccs=UTF-8"));
        Check("and reads it back", Call("fgetwc", fp) == 0x20AC && Call("fgetwc", fp) == 0xFFFF);
        Call("fclose", fp);
        fp = Call("_wfopen", Wide(Local + "w.bin"), Wide("w+b"));
        Call("fputwc", 0x1234, fp);
        Call("rewind", fp);
        Check("binary fputwc/fgetwc are two raw bytes", Call("fgetwc", fp) == 0x1234 && Call("ungetwc", 0x1234, fp) == 0x1234 && Call("fgetwc", fp) == 0x1234);
        Call("fclose", fp);
        Check("_wfopen_s reports ENOENT", Call("_wfopen_s", fpOut, Wide("missing.bin"), Wide("rb")) == 2 && Pointer(fpOut) == 0);

        // ---- descriptors ----
        const int oRdWr = 2, oCreat = 0x100, oExcl = 0x400, oBinary = 0x8000, oText = 0x4000, sRead = 0x100, sWrite = 0x80;
        long fd = Call("_open", Narrow(Local + "lo.bin"), oCreat | oRdWr | oBinary, sRead | sWrite);
        Check("_open creates a descriptor", fd is >= 3 and < 1000);
        Check("_write writes", Call("_write", fd, Narrow("hello"), 5) == 5);
        Call("_lseeki64", fd, 0xDEAD, 0, 0, 0);
        Check("_lseeki64 returns the new position", e.ReadRegister(Harness.R0) == 0 && h.R1Value == 0);
        long readBack = h.Buffer(8);
        Check("_read reads it back", Call("_read", fd, readBack, 5) == 5 && h.ReadNarrow(readBack) == "hello");
        long fd2 = Call("_dup", fd);
        Call("_telli64", fd2);
        Check("_dup shares the position", fd2 != fd && e.ReadRegister(Harness.R0) == 5);
        Check("closing the original leaves the duplicate working", Call("_close", fd) == 0 && Call("_write", fd2, Narrow("!"), 1) == 1);
        Check("_setmode returns the previous mode", Call("_setmode", fd2, oText) == oBinary && Call("_setmode", fd2, oBinary) == oText);
        Check("_locking succeeds", Call("_locking", fd2, 1, 10) == 0);
        Check("_dup2 onto a chosen number", Call("_dup2", fd2, 50) == 0 && Call("_close", 50) == 0);
        Check("_close", Call("_close", fd2) == 0);
        Check("the data is all there", File.ReadAllText(Sandbox("lo.bin")) == "hello!");

        // _get_osfhandle on a descriptor from _open: a real handle the Win32 file API accepts, owned
        // by the descriptor (SQLite's xSync in Spark2's mobile SDK is FlushFileBuffers(_get_osfhandle(fd))).
        long sfd = Call("_open", Narrow(Local + "os.bin"), oCreat | oRdWr | oBinary, sRead | sWrite);
        long osHandle = Call("_get_osfhandle", sfd);
        Check("_get_osfhandle of an _open descriptor is a handle, not -1", osHandle is not 0 and not 0xFFFFFFFF, $"0x{osHandle:X}");
        Check("...the same handle every time", Call("_get_osfhandle", sfd) == osHandle);
        Check("...FlushFileBuffers accepts it", Call("_write", sfd, Narrow("abc"), 3) == 3 && Call("FlushFileBuffers", osHandle) == 1);
        long written = h.Buffer(4);
        Check("...WriteFile through it lands in the same file at the shared position",
            Call("WriteFile", osHandle, Narrow("d"), 1, written, 0) == 1 && Call("_close", sfd) == 0 && File.ReadAllText(Sandbox("os.bin")) == "abcd");
        Check("...and _close closes it: FlushFileBuffers is then ERROR_INVALID_HANDLE",
            Call("FlushFileBuffers", osHandle) == 0 && Call("GetLastError") == 6);
        Check("_get_osfhandle of a closed descriptor is -1", Call("_get_osfhandle", sfd) == 0xFFFFFFFF);
        Check("_open with O_EXCL on an existing file is EEXIST", Call("_open", Narrow(Local + "lo.bin"), oCreat | oExcl | oRdWr, sRead | sWrite) == Eof && Errno() == 17);
        Check("_open without O_CREAT on a missing file is ENOENT", Call("_open", Narrow(Local + "none.bin"), oRdWr, 0) == Eof && Errno() == 2);
        Check("_close of a descriptor that is not open is an invalid parameter", Terminates(() => r = Call("_close", 999)) && r == Eof);
        Check("_setmode on stdout works (the CRT opens 0-2 as devices)", Call("_setmode", 1, oBinary) == oText);
        Check("_write to stdout fails: an app has no console", Call("_write", 1, Narrow("x"), 1) == Eof && Errno() == 9);

        long ro = Call("_creat", Narrow(Local + "ro.txt"), sRead);
        Call("_close", ro);
        Check("_creat without _S_IWRITE makes a read-only file", new FileInfo(Sandbox("ro.txt")).IsReadOnly);
        Check("_access(W_OK) on it is EACCES", Call("_access", Narrow(Local + "ro.txt"), 2) == Eof && Errno() == 13);
        Check("_chmod(_S_IWRITE) makes it writable", Call("_chmod", Narrow(Local + "ro.txt"), sRead | sWrite) == 0 && Call("_access", Narrow(Local + "ro.txt"), 6) == 0);
        Check("_waccess on a packaged file exists", Call("_waccess", Wide(assetName), 4) == 0);
        Check("but is not writable", Call("_access", Narrow(assetName), 2) == Eof && Errno() == 13);
        Check("_access on a missing file is ENOENT", Call("_access", Narrow("missing.dat"), 0) == Eof && Errno() == 2);
        Check("_access with mode 1 is an invalid parameter", Terminates(() => Call("_access", Narrow(assetName), 1)));

        // ---- paths ----
        File.WriteAllText(Sandbox("from.txt"), "x");
        Check("rename moves a file", Call("rename", Narrow(Local + "from.txt"), Narrow(Local + "to.txt")) == 0 && File.Exists(Sandbox("to.txt")) && !File.Exists(Sandbox("from.txt")));
        Check("rename onto an existing file is EACCES", Call("rename", Narrow(Local + "to.txt"), Narrow(Local + "app.txt")) == Eof && Errno() == 13);
        Check("rename of a missing file is ENOENT", Call("rename", Narrow(Local + "gone.txt"), Narrow(Local + "x.txt")) == Eof && Errno() == 2);
        Check("_wrename", Call("_wrename", Wide(Local + "to.txt"), Wide(Local + "back.txt")) == 0 && File.Exists(Sandbox("back.txt")));
        Check("_wmkdir creates a folder", Call("_wmkdir", Wide(Local + "d1")) == 0 && Directory.Exists(Sandbox("d1")));
        Check("_wmkdir again is EEXIST", Call("_wmkdir", Wide(Local + "d1")) == Eof && Errno() == 17);
        File.WriteAllText(Sandbox(@"d1\f.txt"), "x");
        Check("_rmdir of a folder with files is ENOTEMPTY", Call("_rmdir", Narrow(Local + "d1")) == Eof && Errno() == 41);
        File.Delete(Sandbox(@"d1\f.txt"));
        Check("_rmdir removes an empty folder", Call("_rmdir", Narrow(Local + "d1")) == 0 && !Directory.Exists(Sandbox("d1")));
        Check("_rmdir of a missing folder is ENOENT", Call("_rmdir", Narrow(Local + "d1")) == Eof && Errno() == 2);
        long times = h.Buffer(16);
        e.WriteUInt64(times, 1_000_000_000);
        e.WriteUInt64(times + 8, 1_000_000_000);
        Check("_utime64 sets the modification time",
            Call("_utime64", Narrow(Local + "back.txt"), times) == 0 && new DateTimeOffset(File.GetLastWriteTimeUtc(Sandbox("back.txt"))).ToUnixTimeSeconds() == 1_000_000_000);

        Check("_fullpath folds . and .. against the current folder", h.ReadNarrow(Call("_fullpath", 0, Narrow(@"a\..\b\.\c.txt"), 0)) == @"C:\Data\Programs\Install\b\c.txt");
        long full = h.Buffer(260);
        Check("_fullpath of a rooted path", Call("_fullpath", full, Narrow("/x/y"), 260) == full && h.ReadNarrow(full) == @"C:\x\y");
        Check("_fullpath that does not fit is NULL with ERANGE", Call("_fullpath", full, Narrow("abc"), 4) == 0 && Errno() == 34);
        long drive = h.Buffer(4), dir = h.Buffer(256), fname = h.Buffer(256), ext = h.Buffer(256);
        Call("_splitpath", Narrow(@"C:\dir\sub\file.tar.gz"), drive, dir, fname, ext);
        Check("_splitpath", h.ReadNarrow(drive) == "C:" && h.ReadNarrow(dir) == @"\dir\sub\" && h.ReadNarrow(fname) == "file.tar" && h.ReadNarrow(ext) == ".gz");
        Check("_splitpath_s", Call("_splitpath_s", Narrow("name.txt"), 0, 0, dir, 256, fname, 256, ext, 256) == 0 && h.ReadNarrow(dir) == "" && h.ReadNarrow(fname) == "name");
        Check("_splitpath_s with a component too long is ERANGE, all emptied",
            Terminates(() => r = Call("_splitpath_s", Narrow(@"C:\d\longname.txt"), drive, 4, dir, 256, fname, 4, ext, 256)) && r == 34 && h.ReadNarrow(dir) == "");
        long made2 = h.Buffer(64);
        Check("_makepath_s", Call("_makepath_s", made2, 64, Narrow("c"), Narrow(@"\dir"), Narrow("name"), Narrow("txt")) == 0 && h.ReadNarrow(made2) == @"c:\dir\name.txt");
        Check("_makepath_s too small is ERANGE", Terminates(() => r = Call("_makepath_s", made2, 4, 0, 0, Narrow("name"), Narrow("txt"))) && r == 34);

        // ---- throws (last: each one ends the harness's "run") ----
        Call("?__ExceptionPtrRethrow@@YAXPBX@Z", ep);
        Check("rethrow_exception throws the held object with its types", s.ThrowHistory.Count > 0 && s.ThrowHistory[^1].Contains("GameError"), s.ThrowHistory.LastOrDefault() ?? "");
        Call("__RTtypeid", 0);
        Check("typeid of NULL throws std::bad_typeid", s.ThrowHistory[^1].Contains("bad_typeid"), s.ThrowHistory[^1]);
        Call("?__ExceptionPtrRethrow@@YAXPBX@Z", h.Buffer(8));
        Check("rethrowing an empty exception_ptr throws std::bad_exception", s.ThrowHistory[^1].Contains("bad_exception"), s.ThrowHistory[^1]);
    }
}
