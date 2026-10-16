using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The first wave (the runtime convergence plan, 1c and the top of the 1b to-do list): SList,
/// file mapping, _stat, the start-up imports, setjmp/longjmp, uncaught_exception and _Fiopen.
/// </summary>
public sealed class Wave1Suite : IContractSuite
{
    public string Name => "wave 1";

    public void Run(Harness h)
    {
        const int R0 = Harness.R0, R1 = Harness.R1, SP = Harness.SP;
        ArmEmulator e = h.E;
        HostStubs s = h.S;
        string exe = h.Image, sandbox = h.Sandbox;
        long stack = h.Stack;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Call(string fn, params long[] a) => h.Call(fn, a);
        int U16(long address) => h.U16(address);
        long Wide(string text) => h.Wide(text);
        long Narrow(string text) => h.Narrow(text);
        long Errno() => h.Errno();

        // ---- SList ----
        long head = e.AllocateHeap(16);
        e.WriteMemory(head, [1, 2, 3, 4, 5, 6, 7, 8]);
        Call("InitializeSListHead", head);
        Check("InitializeSListHead zeroes the header", e.ReadUInt32(head) == 0 && U16(head + 4) == 0 && U16(head + 6) == 0);
        long a = e.AllocateHeap(16), b = e.AllocateHeap(16), c = e.AllocateHeap(16);
        Check("push onto empty returns NULL", Call("InterlockedPushEntrySList", head, a) == 0);
        Check("push returns the previous head", Call("InterlockedPushEntrySList", head, b) == a);
        Check("pushed entry links to the old head", e.ReadUInt32(b) == a);
        Check("QueryDepthSList counts pushes", Call("QueryDepthSList", head) == 2, $"depth={U16(head + 4)}");
        Check("sequence advances per operation", U16(head + 6) == 2, $"seq={U16(head + 6)}");
        Check("pop returns the last pushed (LIFO)", Call("InterlockedPopEntrySList", head) == b);
        Check("pop decrements depth", U16(head + 4) == 1);
        Call("InterlockedPushEntrySList", head, c);
        long flushed = Call("InterlockedFlushSList", head);
        Check("flush returns the whole chain, newest first", flushed == c && e.ReadUInt32(c) == a && e.ReadUInt32(a) == 0);
        Check("flush empties the list", e.ReadUInt32(head) == 0 && U16(head + 4) == 0);
        Check("pop on empty returns NULL", Call("InterlockedPopEntrySList", head) == 0);

        // ---- file mapping ----
        string assetDir = Path.GetDirectoryName(exe)!;
        string asset = Directory.EnumerateFiles(assetDir, "*", SearchOption.AllDirectories)
            .First(f => new FileInfo(f).Length is > 5000 and < 5_000_000 && !f.EndsWith(".dll") && !f.EndsWith(".exe") && !f.Contains("original") && !f.Contains("wpr_"));
        string rel = Path.GetRelativePath(assetDir, asset).Replace('/', '\\');
        byte[] expected = File.ReadAllBytes(asset);
        long file = Call("CreateFile2", Wide(rel), 0x80000000, 1, 3, 0);
        Check($"CreateFile2 opens {rel}", file is not 0xFFFFFFFF and not 0, $"handle 0x{file:X}");

        // CreateFileMappingFromApp(file, NULL, PAGE_READONLY, (ULONG64)0 at words 4-5, NULL name at word 6).
        // r3 holds junk on purpose: a 64-bit argument skips it.
        long mapping = Call("CreateFileMappingFromApp", file, 0, 0x02, 0xDEAD, 0, 0, 0);
        Check("CreateFileMappingFromApp (size 0 = whole file) returns a handle", mapping != 0);
        long view = Call("MapViewOfFileFromApp", mapping, 0x04, 0, 0, 0);
        Check("MapViewOfFileFromApp returns a page-aligned view", view != 0 && (view & 0xFFF) == 0, $"0x{view:X}");
        Check("the view holds the file's bytes", view != 0 && e.ReadMemory(view, expected.Length).SequenceEqual(expected), $"{expected.Length} bytes");
        long view2 = Call("MapViewOfFileFromApp", mapping, 0x04, 4096, 0, 100);
        Check("a view at an offset holds that slice", view2 != 0 && e.ReadMemory(view2, 100).SequenceEqual(expected.Skip(4096).Take(100)));
        Check("a view past the end fails", Call("MapViewOfFileFromApp", mapping, 0x04, expected.Length, 0, 10) == 0);
        Check("UnmapViewOfFile succeeds", Call("UnmapViewOfFile", view) == 1);
        Check("UnmapViewOfFile twice fails", Call("UnmapViewOfFile", view) == 0);
        Check("CloseHandle on the mapping", Call("CloseHandle", mapping) == 1);
        Check("mapping a closed mapping fails", Call("MapViewOfFileFromApp", mapping, 0x04, 0, 0, 0) == 0);
        Call("CloseHandle", file);

        // A writable mapping is written back on unmap.
        string save = @"C:\Data\Users\DefApps\AppData\{E94059A2-135C-420E-8E60-BCDA5FC3EC30}\Local\save.bin";
        long wf = Call("CreateFile2", Wide(save), 0xC0000000, 0, 2, 0);
        long wm = Call("CreateFileMappingFromApp", wf, 0, 0x04, 0xDEAD, 64, 0, 0);
        long wv = Call("MapViewOfFileFromApp", wm, 0x06, 0, 0, 0);
        Check("writable mapping and view", wf is not 0xFFFFFFFF && wm != 0 && wv != 0, $"file 0x{wf:X} mapping 0x{wm:X} view 0x{wv:X}");
        if (wv != 0) e.WriteMemory(wv, Encoding.ASCII.GetBytes("written through a view"));
        Call("UnmapViewOfFile", wv);
        Call("CloseHandle", wm);
        Call("CloseHandle", wf);
        string saved = Path.Combine(sandbox, "save.bin");
        Check("a writable view is written back on unmap, at the mapping's size",
            File.Exists(saved) && File.ReadAllText(saved).StartsWith("written through a view") && new FileInfo(saved).Length == 64,
            File.Exists(saved) ? $"{new FileInfo(saved).Length} bytes" : "missing");

        // A pagefile-backed mapping.
        long pm = Call("CreateFileMappingFromApp", 0xFFFFFFFF, 0, 0x04, 0xDEAD, 4096, 0, 0);
        long pv = Call("MapViewOfFileFromApp", pm, 0x06, 0, 0, 0);
        Check("a pagefile mapping gives zeroed memory", pv != 0 && e.ReadMemory(pv, 4096).All(x => x == 0));
        Check("a pagefile mapping with no size fails", Call("CreateFileMappingFromApp", 0xFFFFFFFF, 0, 0x04, 0xDEAD, 0, 0, 0) == 0);

        // ---- _stat64 ----
        long st = e.AllocateHeap(64);
        e.WriteMemory(st, Enumerable.Repeat((byte)0xCC, 64).ToArray());
        long r = Call("_stat64", Narrow(rel), st);
        long size = BitConverter.ToInt64(e.ReadMemory(st + 24, 8));
        int mode = U16(st + 6);
        long mtime = BitConverter.ToInt64(e.ReadMemory(st + 40, 8));
        Check("_stat64 on an asset returns 0", r == 0);
        Check("_stat64 st_size is the file's size", size == expected.Length, $"{size} vs {expected.Length}");
        Check("_stat64 st_mode is a regular file", (mode & 0xF000) == 0x8000, $"0x{mode:X}");
        Check("_stat64 st_mtime is the file's", Math.Abs(mtime - new DateTimeOffset(File.GetLastWriteTimeUtc(asset)).ToUnixTimeSeconds()) <= 2);
        Check("_stat64 writes 56 bytes, no more", e.ReadMemory(st + 56, 8).All(x => x == 0xCC));
        r = Call("_stat64", Narrow(@"no\such\file.dat"), st);
        Check("_stat64 on a missing file returns -1 with ENOENT", r == 0xFFFFFFFF && Errno() == 2, $"r=0x{r:X} errno={Errno()}");
        string dir = Path.GetRelativePath(assetDir, Path.GetDirectoryName(asset)!);
        if (dir != ".")
        {
            r = Call("_stat64", Narrow(dir), st);
            mode = U16(st + 6);
            Check($"_stat64 on a package folder ({dir}) is a directory", r == 0 && (mode & 0xF000) == 0x4000, $"r={r} mode=0x{mode:X}");
        }

        e.WriteMemory(st, Enumerable.Repeat((byte)0xCC, 64).ToArray());
        r = Call("_wstat64i32", Wide(rel), st);
        Check("_wstat64i32 puts a 32-bit size at 20 and writes 48 bytes",
            r == 0 && BitConverter.ToInt32(e.ReadMemory(st + 20, 4)) == expected.Length && e.ReadMemory(st + 48, 8).All(x => x == 0xCC));

        // ---- the rest of this change ----
        Check("_onexit returns its argument", Call("_onexit", 0x1234) == 0x1234);
        Check("__dllonexit returns its argument", Call("__dllonexit", 0x5678, 0, 0) == 0x5678);
        Check("SafeToDefault no longer lists _onexit", !HostStubs.SafeToDefault.ContainsKey("_onexit"));
        Check("ObjectDisposedException is a known raise",
            HostStubs.PlatformRaise.TryParse("?__abi_WinRTraiseObjectDisposedException@@YAXXZ", 0, out HostStubs.PlatformRaise raise) && raise.HResult == 0x80000013);

        // ---- start-up imports (1b to-do, top) ----
        long argc = e.AllocateHeap(4), argv = e.AllocateHeap(4), env = e.AllocateHeap(4);
        Check("__getmainargs returns 0", Call("__getmainargs", argc, argv, env, 0, 0) == 0);
        long argv0 = e.ReadUInt32(e.ReadUInt32(argv));
        Check("__getmainargs: argc 1, argv { name, NULL }, env { NULL }",
            e.ReadUInt32(argc) == 1 && argv0 != 0 && e.ReadUInt32(e.ReadUInt32(argv) + 4) == 0 && e.ReadUInt32(e.ReadUInt32(env)) == 0,
            Encoding.ASCII.GetString(e.ReadMemory(argv0, 40)).Split('\0')[0]);
        e.WriteUInt32(argc, 99);
        long wargv = Call("?GetCmdArguments@Details@Platform@@YAPAPA_WPAH@Z", argc);
        Check("GetCmdArguments: argc 1, { wide name, NULL }",
            e.ReadUInt32(argc) == 1 && wargv != 0 && e.ReadUInt32(wargv) != 0 && e.ReadUInt32(wargv + 4) == 0,
            e.ReadUtf16String(e.ReadUInt32(wargv), 64));
        long box = Call("?GetIBoxArrayVtable@Details@Platform@@YAPAXPAX@Z", 0);
        Check("GetIBoxArrayVtable returns a vtable of code", box != 0 && Enumerable.Range(0, 7).All(i => (e.ReadUInt32(box + (i * 4)) & 1) == 1));
        Check("GetIBoxArrayVtable is the same vtable each time", Call("?GetIBoxArrayVtable@Details@Platform@@YAPAXPAX@Z", 0) == box);
        Check("CoInitializeEx: S_OK, then S_FALSE on the same thread", Call("CoInitializeEx", 0, 0) == 0 && Call("CoInitializeEx", 0, 0) == 1);
        Check("GetCurrentThread is the pseudo-handle -2", Call("GetCurrentThread") == 0xFFFFFFFE);
        Check("DisableThreadLibraryCalls succeeds", Call("DisableThreadLibraryCalls", 0x400000) == 1);
        Call("_lock", 8); Call("_unlock", 8);
        Check("_lock/_unlock dispatch without trapping", s.FirstUnimplementedImport is null);
        foreach (string safe in new[] { "?InitializeData@Details@Platform@@YAJH@Z", "?_Orphan_all@_Container_base0@std@@QAAXXZ", "__crtSetUnhandledExceptionFilter" })
        {
            Check($"{safe} is implemented, not allowlisted", s.IsImplemented(safe) && !HostStubs.SafeToDefault.ContainsKey(safe));
        }

        // ---- second wave: the trap2 stops ----
        const int R4 = 70, R11 = 77, R12 = 78, LR = 10;
        long jb = e.AllocateHeap(112);
        for (int i = 0; i < 8; i++) e.WriteRegister(R4 + i, 0x4000 + i);
        e.WriteRegister(LR, 0x00412345);
        e.Cpu.VfpWrite(16, 0x3F800000); e.Cpu.VfpWrite(31, 0xDEADBEEF);
        long savedSp = stack;
        Check("setjmp returns 0", Call("setjmp", jb, 0x7777) == 0);
        Check("setjmp records r4-r11, sp, the return address and d8-d15",
            e.ReadUInt32(jb + 4) == 0x4000 && e.ReadUInt32(jb + 32) == 0x4007 && e.ReadUInt32(jb + 36) == savedSp &&
            e.ReadUInt32(jb + 40) == 0x00412345 && e.ReadUInt32(jb + 48) == 0x3F800000 && e.ReadUInt32(jb + 108) == 0xDEADBEEF && e.ReadUInt32(jb) == 0x7777);
        for (int i = 0; i < 8; i++) e.WriteRegister(R4 + i, 0);
        e.Cpu.VfpWrite(16, 0); e.Cpu.VfpWrite(31, 0);
        e.WriteRegister(LR, 0x00999999);
        long elsewhere = e.AllocateHeap(0x100) + 0x80;
        e.WriteRegister(R0, jb); e.WriteRegister(R1, 0); e.WriteRegister(SP, elsewhere); e.WriteRegister(R12, 0x00999999);
        s.Dispatch("MSVCR110.dll!longjmp");
        Check("longjmp(buf, 0) returns 1 from setjmp's call site",
            e.ReadRegister(R0) == 1 && (e.ReadRegister(R12) & 0xFFFFFFFF) == 0x00412345, $"r0={e.ReadRegister(R0)} r12=0x{e.ReadRegister(R12):X}");
        Check("longjmp restores r4-r11, sp and d8-d15",
            e.ReadRegister(R4) == 0x4000 && e.ReadRegister(R11) == 0x4007 && (e.ReadRegister(SP) & 0xFFFFFFFF) == savedSp &&
            e.Cpu.VfpRead(16) == 0x3F800000 && e.Cpu.VfpRead(31) == 0xDEADBEEF);
        e.WriteRegister(R0, jb); e.WriteRegister(R1, 42);
        s.Dispatch("MSVCR110.dll!longjmp");
        Check("longjmp(buf, 42) returns 42", e.ReadRegister(R0) == 42);

        Check("uncaught_exception is false with nothing in flight", Call("?uncaught_exception@std@@YA_NXZ") == 0);

        long fp = Call("?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z", Narrow(rel), 0x01 | 0x20, 0x40);
        Check("_Fiopen(in|binary) opens the asset", fp != 0);
        long bufp = e.AllocateHeap(16);
        long got = Call("fread", bufp, 1, 16, fp);
        Check("and fread reads its first bytes", got == 16 && e.ReadMemory(bufp, 16).SequenceEqual(expected.Take(16)));
        long fpAte = Call("?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z", Narrow(rel), 0x01 | 0x04 | 0x20, 0x40);
        Check("_Fiopen(in|ate) starts at the end", fpAte != 0 && Call("ftell", fpAte) == expected.Length, $"ftell={Call("ftell", fpAte)}");
        Check("_Fiopen of a missing file is NULL", Call("?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z", Narrow("missing.txt"), 0x01, 0x40) == 0);
        long fpOut = Call("?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z", Narrow(@"C:\Data\Users\DefApps\AppData\{E94059A2-135C-420E-8E60-BCDA5FC3EC30}\Local\out.txt"), 0x02 | 0x10, 0x40);
        Check("_Fiopen(out|trunc) creates a file in the sandbox", fpOut != 0 && File.Exists(Path.Combine(sandbox, "out.txt")));
        Check("_Fiopen with a nonsense mode is NULL", Call("?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z", Narrow(rel), 0x10, 0x40) == 0);
    }
}
