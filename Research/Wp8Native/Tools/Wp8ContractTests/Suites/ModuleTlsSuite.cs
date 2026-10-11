using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// Static TLS, TLS callbacks, forwarders loaded at GetProcAddress time, and unload range reuse
/// (Research/Wp8Native/ModuleTls.cs, ModuleLoader.cs). No installed title has a TLS callback, a
/// DLL with TLS or a runtime forwarder, so those are driven by a synthetic package written by
/// <see cref="SyntheticPe"/>; the TEB/TLS layout is also checked against a real title whose
/// Game.exe reads <c>__declspec(thread)</c> data 1,847 times.
/// </summary>
public sealed class ModuleTlsSuite : IContractSuite
{
    private const int R12 = 78;

    private static readonly string Apps = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR", "AppData");

    public string Name => "module TLS";

    public void Run(Harness h)
    {
        RealTitle(h);

        string package = Path.Combine(Path.GetTempPath(), $"wp8-tls-suite-{Environment.ProcessId}");
        try
        {
            Synthetic(h, package);
        }
        finally
        {
            try
            {
                Directory.Delete(package, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static uint Text(string four) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(four));

    // ---------------------------------------------------------------------------------------------

    private static void RealTitle(Harness h)
    {
        string exe = Path.Combine(Apps, "8e162189-0baf-431e-966a-0d050eac9aab", "Game.exe");
        if (!File.Exists(exe))
        {
            Console.WriteLine("SKIP  the Game.exe title with static TLS is not installed");
            return;
        }

        using ArmEmulator e = new(PeImage.Load(exe), Path.GetDirectoryName(exe)!, collectBlockStats: false);
        LoadedModule main = e.Modules[0];
        ModuleTls? tls = main.Tls;
        h.Check("Game.exe: its TLS directory gives the main image index 0", tls is { Index: 0 }, string.Join(" | ", e.TlsLog));
        if (tls is null)
        {
            return;
        }

        h.Check("Game.exe: _tls_index in the image holds 0", tls.IndexAddress != 0 && e.ReadUInt32(tls.IndexAddress) == 0);
        long teb = e.ThreadEnvironmentBlock(1);
        h.Check("Game.exe: the main thread's TEB is at 0xA1000000, Self points at it", teb == 0xA1000000 && e.ReadUInt32(teb + 0x18) == teb, $"0x{teb:X8}");
        long array = e.ReadUInt32(teb + 0x2C);
        long block = array == 0 ? 0 : e.ReadUInt32(array);
        h.Check("Game.exe: TEB+0x2C -> slot 0 is the main thread's block", block != 0 && block == e.TlsBlockOf(1, main), $"array 0x{array:X8} block 0x{block:X8}");

        int offset = main.Image.RvaToOffset((uint)(tls.TemplateStart - main.ImageBase)) ?? -1;
        byte[] template = offset < 0 ? [] : main.Image.Raw.Slice(offset, tls.TemplateLength).ToArray();
        h.Check($"Game.exe: the block is a copy of the {tls.TemplateLength}-byte template",
            template.Length == tls.TemplateLength && tls.TemplateLength > 0 && e.ReadMemory(block, tls.TemplateLength).AsSpan().SequenceEqual(template));
        h.Check("Game.exe: no TLS callbacks (none in the directory)", tls.Callbacks.Count == 0);
    }

    // ---------------------------------------------------------------------------------------------

    private static void Synthetic(Harness h, string package)
    {
        if (Directory.Exists(package))
        {
            Directory.Delete(package, recursive: true);
        }

        Directory.CreateDirectory(package);

        // A.dll: static import of the exe; TLS "AAAA" + 8 zero bytes, two callbacks, a DllMain.
        SyntheticPe a = new(0x10000000, dll: true);
        uint aIndex = a.TlsIndexSlot();
        uint aRead = a.TlsReader(aIndex);
        uint aWrite = a.TlsWriter(aIndex);
        a.EntryRva = a.LogFunction(0xAD);
        uint aCallback1 = a.LogFunction(0xA1);
        uint aCallback2 = a.LogFunction(0xA2);
        a.Export("ARead", aRead);
        a.Export("AWrite", aWrite);
        a.Tls(aIndex, Encoding.ASCII.GetBytes("AAAA"), 8, aCallback1, aCallback2);
        File.WriteAllBytes(Path.Combine(package, "A.dll"), a.Build());

        // B.dll: only reached through D's forwarder; TLS "BBBB", one callback, a DllMain.
        SyntheticPe b = new(0x10000000, dll: true);
        uint bIndex = b.TlsIndexSlot();
        uint bTarget = b.TlsReader(bIndex);
        b.EntryRva = b.LogFunction(0xBD);
        uint bCallback = b.LogFunction(0xB1);
        b.Export("Target", bTarget);
        b.Tls(bIndex, Encoding.ASCII.GetBytes("BBBB"), 4, bCallback);
        File.WriteAllBytes(Path.Combine(package, "B.dll"), b.Build());

        // D.dll: loaded at runtime; "Fwd" is forwarded to B.Target.
        SyntheticPe d = new(0x10000000, dll: true);
        d.EntryRva = d.LogFunction(0xDD);
        d.ExportForwarder("Fwd", "B.Target");
        d.Va(d.Here);   // one base relocation: without any, a DLL whose preferred base is taken cannot load
        File.WriteAllBytes(Path.Combine(package, "D.dll"), d.Build());

        // The exe: TLS "EXE1", one callback; imports A.
        SyntheticPe exe = new(0x400000, dll: false);
        uint exeIndex = exe.TlsIndexSlot();
        uint exeRead = exe.TlsReader(exeIndex);
        exe.EntryRva = exe.Return();
        uint exeCallback = exe.LogFunction(0xE0);
        exe.Import("A.dll", "ARead");
        exe.Tls(exeIndex, Encoding.ASCII.GetBytes("EXE1"), 4, exeCallback);
        string exePath = Path.Combine(package, "TlsHost.exe");
        File.WriteAllBytes(exePath, exe.Build());

        using ArmEmulator e = new(PeImage.Load(exePath), package, collectBlockStats: false);
        e.Cpu.MemMap(SyntheticPe.LogPage, 0x1000, 3);
        ModuleLoaderSuite.Guest g = new(e);

        LoadedModule main = e.Modules[0];
        LoadedModule? aModule = e.Modules.FirstOrDefault(m => m.Name == "A.dll");
        h.Check("TLS: the exe and its statically imported A.dll are mapped", main.Name == "TlsHost.exe" && aModule is not null, string.Join("; ", e.Modules));
        if (aModule is null)
        {
            return;
        }

        long aBase = aModule.ImageBase;
        h.Check("TLS: indices in load order - the exe 0, A.dll 1 - written to each _tls_index",
            main.Tls is { Index: 0 } && aModule.Tls is { Index: 1 } && e.ReadUInt32(main.ImageBase + exeIndex) == 0 && e.ReadUInt32(aBase + aIndex) == 1,
            string.Join(" | ", e.TlsLog));
        h.Check("TLS: A.dll's callbacks are read from its directory, in order",
            aModule.TlsCallbacks.SequenceEqual([aBase + aCallback1, aBase + aCallback2]));

        long teb = e.ThreadEnvironmentBlock(1);
        long array = e.ReadUInt32(teb + 0x2C);
        h.Check("TLS: the main TEB's ThreadLocalStoragePointer holds both blocks, each a copy of its template plus zero fill",
            array != 0 && e.ReadUInt32(e.ReadUInt32(array)) == Text("EXE1") && e.ReadUInt32(e.ReadUInt32(array) + 4) == 0
            && e.ReadUInt32(e.ReadUInt32(array + 4)) == Text("AAAA") && e.ReadUInt32(e.ReadUInt32(array + 4) + 8) == 0);
        h.Check("TLS: TEB client id is the GetCurrentThreadId value", e.ReadUInt32(teb + 0x24) == g.Call("GetCurrentThreadId"));

        // ---- start-up: per DLL, TLS callbacks then DllMain; then the exe's own callbacks ----
        long start = e.StartWithModuleInitialisers(g.Sentinel);
        h.Check("start-up begins at A.dll's first TLS callback, not its DllMain", start == aBase + aCallback1, $"0x{start:X8}");
        string? fault = g.RunUntilReturn(start);
        h.Check("start-up: A1, A2, A's DllMain, then the exe's callback, all PROCESS_ATTACH, then the entry",
            fault is null && g.Returned && Log(e).SequenceEqual([Entry(0xA1, 1), Entry(0xA2, 1), Entry(0xAD, 1), Entry(0xE0, 1)]),
            fault ?? Describe(Log(e)));
        h.Check("start-up: A.dll is attached", aModule.ProcessAttached);

        // ---- guest code reads its TLS the way MSVC compiles it ----
        h.Check("guest: the exe reads its own TLS through TPIDRURW/TEB+0x2C/_tls_index", g.Invoke(main.ImageBase + exeRead) == Text("EXE1"));
        long boundRead = e.ReadUInt32(main.ImageBase + main.Image.Imports.Single().IatSlotRva);
        h.Check("guest: A.dll's export, called through the exe's IAT, reads A's block", boundRead == aBase + aRead && g.Invoke(boundRead) == Text("AAAA"));
        g.Invoke(aBase + aWrite, 0x11111111);
        h.Check("guest: a write to A's TLS on the main thread reads back", g.Invoke(aBase + aRead) == 0x11111111);

        h.Check("DisableThreadLibraryCalls on a module with TLS succeeds and changes nothing",
            g.Call("DisableThreadLibraryCalls", aBase) == 1 && !aModule.ThreadCallsDisabled);

        // ---- a second thread: its own TEB and blocks; THREAD_ATTACH/DETACH with TLS callbacks ----
        ClearLog(e);
        int thread = e.CreateGuestThread("tls test", aBase + aWrite, 0x22222222);
        long threadTeb = e.ThreadEnvironmentBlock(thread);
        long threadBlock = e.TlsBlockOf(thread, aModule);
        h.Check("thread: a new thread gets its own TEB and a fresh copy of A's template",
            threadTeb != 0 && threadTeb != teb && e.ReadUInt32(threadTeb + 0x18) == threadTeb
            && threadBlock != 0 && threadBlock != e.TlsBlockOf(1, aModule) && e.ReadUInt32(threadBlock) == Text("AAAA"));

        bool backOnMain = false;
        e.RunOtherThreads(() =>
        {
            backOnMain = true;
            e.Stop("back on the main thread");
        });
        string? threadFault = e.Run(e.ReadRegister(R12), 50_000_000);
        h.Check("thread: it ran to completion and the scheduler came back", threadFault is null && backOnMain && e.IsThreadFinished(thread), threadFault ?? e.StopReason ?? "");
        h.Check("thread: THREAD_ATTACH (A's callbacks, A's DllMain, the exe's callback) then DETACH in reverse",
            Log(e).SequenceEqual([
                Entry(0xA1, 2), Entry(0xA2, 2), Entry(0xAD, 2), Entry(0xE0, 2),
                Entry(0xE0, 3), Entry(0xA1, 3), Entry(0xA2, 3), Entry(0xAD, 3)]),
            Describe(Log(e)));
        h.Check("thread: its write landed in its own block, and the main thread's is untouched",
            e.ReadUInt32(threadBlock) == 0x22222222 && g.Invoke(aBase + aRead) == 0x11111111);

        // ---- a runtime load, then a forwarder to a DLL that is not loaded yet ----
        ClearLog(e);
        long dHandle = g.CallGuest("LoadPackagedLibrary", g.Wide("D.dll"), 0);
        LoadedModule? dModule = e.ModuleByHandle(dHandle);
        h.Check("LoadPackagedLibrary(D.dll): relocated, its DllMain ran", dModule is not null && dHandle != 0x10000000 && Log(e).SequenceEqual([Entry(0xDD, 1)]), $"0x{dHandle:X8} {Describe(Log(e))} {string.Join(" | ", e.ModuleLog.TakeLast(3))}");
        if (dModule is null)
        {
            return;
        }

        ClearLog(e);
        long forwarded = g.CallGuest("GetProcAddress", dHandle, g.Narrow("Fwd"));
        LoadedModule? bModule = e.Modules.FirstOrDefault(m => m.Name == "B.dll");
        h.Check("GetProcAddress(D, \"Fwd\") loads B.dll and returns B!Target", bModule is not null && forwarded == bModule.ImageBase + bTarget, $"0x{forwarded:X8}");
        if (bModule is null)
        {
            return;
        }

        long bBase = bModule.ImageBase;
        h.Check("...B's TLS callback and DllMain ran (PROCESS_ATTACH, dynamic) before it returned",
            g.Returned && bModule.ProcessAttached && Log(e).SequenceEqual([Entry(0xB1, 1), Entry(0xBD, 1)]), Describe(Log(e)));
        h.Check("...B is held by D (one reference, not pinned)", !bModule.Pinned && bModule.ReferenceCount == 1 && dModule.Dependencies.Contains(bModule));
        h.Check("...B got TLS index 2 and the running thread a block: the forwarded function reads \"BBBB\"",
            bModule.Tls is { Index: 2 } && e.ReadUInt32(bBase + bIndex) == 2 && g.Invoke(forwarded) == Text("BBBB"));
        ClearLog(e);
        h.Check("GetProcAddress again: same address, nothing initialised twice",
            g.CallGuest("GetProcAddress", dHandle, g.Narrow("Fwd")) == forwarded && Log(e).Count == 0);

        // ---- FreeLibrary to zero: detach, TLS released, ranges reusable ----
        ClearLog(e);
        h.Check("FreeLibrary(D) unloads D and B: D's DllMain, then B's callback and DllMain, PROCESS_DETACH",
            g.CallGuest("FreeLibrary", dHandle) == 1 && dModule.Unloaded && bModule.Unloaded
            && Log(e).SequenceEqual([Entry(0xDD, 0), Entry(0xB1, 0), Entry(0xBD, 0)]),
            Describe(Log(e)));
        h.Check("...B's TLS index is released and its slot cleared", e.ReadUInt32(e.ReadUInt32(teb + 0x2C) + 8) == 0 && e.TlsLog.Any(l => l == "B.dll: TLS index 2 released"));
        h.Check("...its range faults: the unloaded code is not code", !e.IsExecutableCode(forwarded));

        long dAgain = g.CallGuest("LoadPackagedLibrary", g.Wide("D.dll"), 0);
        h.Check("LoadPackagedLibrary(D.dll) again reuses the range it left", dAgain == dHandle, $"0x{dAgain:X8} vs 0x{dHandle:X8}");
        long forwardedAgain = g.CallGuest("GetProcAddress", dAgain, g.Narrow("Fwd"));
        LoadedModule? bAgain = e.Modules.FirstOrDefault(m => m.Name == "B.dll");
        h.Check("...and B comes back at its old range with index 2 and a fresh template",
            bAgain is not null && bAgain != bModule && bAgain.ImageBase == bBase && forwardedAgain == forwarded
            && bAgain.Tls is { Index: 2 } && g.Invoke(forwardedAgain) == Text("BBBB"));
        h.Check("no TLS directory was rejected", !e.TlsLog.Any(l => l.Contains("outside", StringComparison.Ordinal) || l.Contains("not in the image", StringComparison.Ordinal)), string.Join(" | ", e.TlsLog));
    }

    private static uint Entry(ushort tag, uint reason) => tag | (reason << 16);

    private static List<uint> Log(ArmEmulator e)
    {
        uint count = e.ReadUInt32(SyntheticPe.LogPage);
        return Enumerable.Range(1, (int)Math.Min(count, 64)).Select(i => e.ReadUInt32(SyntheticPe.LogPage + (i * 4))).ToList();
    }

    private static void ClearLog(ArmEmulator e) => e.WriteUInt32(SyntheticPe.LogPage, 0);

    private static string Describe(List<uint> log) => string.Join(", ", log.Select(x => $"{x & 0xFFFF:X2}/{x >> 16}"));
}
