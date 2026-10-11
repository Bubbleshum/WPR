using System.Buffers.Binary;
using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The multi-module loader (Research/Wp8Native/ModuleLoader.cs) against real installed packages,
/// read-only: Modern Combat 4's component imports PushNotificationWP8.dll (both prefer 0x10000000,
/// so the DLL must be relocated) and can LoadPackagedLibrary its IGPLib.dll; a title whose Game.exe
/// imports fmodex_arm.dll at its free preferred base. Each gets its own emulator; a title that is
/// not installed is skipped, not failed.
/// </summary>
public sealed class ModuleLoaderSuite : IContractSuite
{
    private const int R0 = 66, R1 = 67, R2 = 68, R3 = 69, SP = 12, LR = 10, R12 = 78;

    private static readonly string Apps = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR", "AppData");

    public string Name => "module loader";

    public void Run(Harness h)
    {
        ModernCombat(h);
        Fmod(h);
    }

    // ---------------------------------------------------------------------------------------------

    private static void ModernCombat(Harness h)
    {
        string dir = Path.Combine(Apps, "0cb4e588-5759-4150-bef0-6866345fa26b");
        string component = Path.Combine(dir, "MC4HybridComponent.dll");
        if (!File.Exists(component))
        {
            Console.WriteLine("SKIP  Modern Combat 4 is not installed");
            return;
        }

        using ArmEmulator e = new(PeImage.Load(component), dir, collectBlockStats: false);
        Guest g = new(e);

        LoadedModule main = e.Modules[0];
        h.Check("MC4: the component is the main module, at its preferred base", main.IsMain && main.ImageBase == 0x10000000 && main.Name == "MC4HybridComponent.dll", main.ToString());

        LoadedModule? push = e.Modules.FirstOrDefault(m => m.Name.Equals("PushNotificationWP8.dll", StringComparison.OrdinalIgnoreCase));
        h.Check("MC4: PushNotificationWP8.dll (imported by the component) is mapped", push is not null, string.Join("; ", e.Modules));
        if (push is null)
        {
            return;
        }

        h.Check("MC4: modules do not overlap", NoOverlap(e.Modules));
        h.Check("MC4: IGPLib.dll (not imported) is not loaded at start-up", e.Modules.All(m => !m.Name.Equals("IGPLib.dll", StringComparison.OrdinalIgnoreCase)));
        h.Check("MC4: the DLL's preferred base was taken, so it was relocated", push.ImageBase != push.Image.ImageBase && push.ImageBase % 0x10000 == 0, push.ToString());
        h.Check("MC4: relocations were applied", push.RelocationsApplied > 100, $"{push.RelocationsApplied}");
        h.Check("MC4: a statically imported DLL is pinned", push.Pinned);
        CheckRelocations(h, e, push, "MC4");

        int peHeader = BinaryPrimitives.ReadInt32LittleEndian(push.Image.Raw[0x3C..]);
        h.Check("MC4: the mapped header's ImageBase is the actual base", e.ReadUInt32(push.ImageBase + peHeader + 24 + 28) == push.ImageBase);

        // Every import of the component from the DLL now holds the export's address.
        List<ImportedFunction> fromPush = main.Image.Imports.Where(i => i.Dll.Equals(push.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        int bound = fromPush.Count(i => e.ReadUInt32(main.ImageBase + i.IatSlotRva) == ExpectedExport(push, i.Name));
        h.Check($"MC4: all {fromPush.Count} IAT entries for the DLL hold its exports' addresses", fromPush.Count > 0 && bound == fromPush.Count, $"{bound}/{fromPush.Count}");
        long firstExport = fromPush.Count > 0 ? e.ReadUInt32(main.ImageBase + fromPush[0].IatSlotRva) : 0;
        h.Check("MC4: a bound export is executable code in the DLL", e.IsExecutableCode(firstExport) && push.Contains(firstExport), $"0x{firstExport:X8}");
        h.Check("MC4: the system imports are still host-stub traps", e.TrapNameAt(e.ReadUInt32(main.ImageBase + main.Image.Imports.First(i => !i.Dll.Equals(push.Name, StringComparison.OrdinalIgnoreCase)).IatSlotRva) & ~1L) is not null);
        h.Check("MC4: TrappedImportCount excludes the bound imports", e.TrappedImportCount == main.Image.Imports.Count + push.Image.Imports.Count - fromPush.Count, $"{e.TrappedImportCount}");

        // Unwinding and C++ EH resolve against the DLL's own .pdata and image.
        long functionStart = e.Unwinder.FunctionStart(firstExport & ~1L);
        h.Check("MC4: the unwinder finds the DLL export's function in the DLL's .pdata", functionStart != 0 && push.Contains(functionStart), $"0x{functionStart:X8}");
        CheckFrameBase(h, e, push, "MC4");

        // ---- the library-loader imports ----
        h.Check("GetModuleHandleW(NULL) is the main image", g.Call("GetModuleHandleW", 0) == main.ImageBase);
        h.Check("GetModuleHandleW finds a loaded DLL case-insensitively, \".dll\" appended", g.Call("GetModuleHandleW", g.Wide("pushnotificationwp8")) == push.ImageBase);
        h.Check("GetModuleHandleW of a package DLL not loaded is NULL, ERROR_MOD_NOT_FOUND", g.Call("GetModuleHandleW", g.Wide("IGPLib.dll")) == 0 && g.LastError() == 126);
        long api = g.Call("GetModuleHandleW", g.Wide(main.Image.Imports.First(i => i.Dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase)).Dll));
        h.Check("GetModuleHandleW of an imported system DLL is a non-NULL handle", api != 0);
        long getLastError = g.Call("GetProcAddress", api, g.Narrow("GetLastError"));
        h.Check("GetProcAddress(system DLL, implemented) is a host-stub trap", getLastError != 0 && (e.TrapNameAt(getLastError & ~1L) ?? "").EndsWith("!GetLastError"), e.TrapNameAt(getLastError & ~1L) ?? "none");
        h.Check("GetProcAddress(system DLL, unknown) is NULL, ERROR_PROC_NOT_FOUND", g.Call("GetProcAddress", api, g.Narrow("NoSuchFunctionAnywhere")) == 0 && g.LastError() == 127);

        (string exportName, uint exportRva) = push.Image.Exports.First(x => !push.Image.IsForwarder(x.Value));
        h.Check($"GetProcAddress(DLL, \"{exportName}\") is the relocated export", g.Call("GetProcAddress", push.ImageBase, g.Narrow(exportName)) == push.ImageBase + exportRva);
        uint ordinal = push.Image.ExportsByOrdinal.First(x => x.Value == exportRva).Key;
        h.Check("GetProcAddress by ordinal (high word zero) agrees", g.Call("GetProcAddress", push.ImageBase, ordinal) == push.ImageBase + exportRva, $"#{ordinal}");
        h.Check("GetProcAddress of a missing export is NULL, ERROR_PROC_NOT_FOUND", g.Call("GetProcAddress", push.ImageBase, g.Narrow("NoSuchExport")) == 0 && g.LastError() == 127);
        h.Check("GetProcAddress on a non-module is NULL, ERROR_MOD_NOT_FOUND", g.Call("GetProcAddress", 0x12340000, g.Narrow(exportName)) == 0 && g.LastError() == 126);

        long handleOut = g.Buffer(4);
        h.Check("GetModuleHandleExW(FROM_ADDRESS|UNCHANGED_REFCOUNT) finds the DLL from a code address",
            g.Call("GetModuleHandleExW", 6, firstExport, handleOut) == 1 && e.ReadUInt32(handleOut) == push.ImageBase);
        h.Check("GetModuleHandleExW(FROM_ADDRESS) of an address in no module fails with ERROR_MOD_NOT_FOUND, *out NULL",
            g.Call("GetModuleHandleExW", 6, 0x08000000, handleOut) == 0 && e.ReadUInt32(handleOut) == 0 && g.LastError() == 126);
        h.Check("GetModuleHandleExW(PIN|UNCHANGED_REFCOUNT) is ERROR_INVALID_PARAMETER",
            g.Call("GetModuleHandleExW", 3, 0, handleOut) == 0 && g.LastError() == 87);

        long name = g.Buffer(520);
        long length = g.Call("GetModuleFileNameW", push.ImageBase, name, 260);
        string path = e.ReadUtf16String(name, 260);
        h.Check("GetModuleFileNameW gives the DLL's path in the install folder, length without NUL",
            path.EndsWith(@"\PushNotificationWP8.dll", StringComparison.Ordinal) && length == path.Length, path);
        h.Check("GetModuleFileNameW(NULL) names the main image", e.ReadUtf16String(name, 260) is not null && g.Call("GetModuleFileNameW", 0, name, 260) > 0 && e.ReadUtf16String(name, 260).EndsWith(@"\MC4HybridComponent.dll", StringComparison.Ordinal));
        long truncated = g.Call("GetModuleFileNameW", push.ImageBase, name, 5);
        h.Check("GetModuleFileNameW truncates to nSize-1 + NUL, returns nSize, ERROR_INSUFFICIENT_BUFFER",
            truncated == 5 && g.LastError() == 122 && e.ReadUtf16String(name, 10) == path[..4]);

        h.Check("FreeLibrary on a statically loaded DLL succeeds and leaves it loaded",
            g.Call("FreeLibrary", push.ImageBase) == 1 && g.Call("GetModuleHandleW", g.Wide("PushNotificationWP8.dll")) == push.ImageBase);
        h.Check("FreeLibrary on a non-module fails, ERROR_MOD_NOT_FOUND", g.Call("FreeLibrary", 0x12340000) == 0 && g.LastError() == 126);

        // ---- start-up: the DLL's DllMain runs, as a guest call, before the image's own entry ----
        long sentinel = g.Sentinel;
        long start = e.StartWithModuleInitialisers(sentinel);
        h.Check("start-up begins at the DLL's DllMain", start == push.EntryPoint, $"0x{start:X8}");
        string? fault = g.RunUntilReturn(start);
        h.Check("DllMain(DLL_PROCESS_ATTACH) of the DLL ran and returned TRUE, then control reached the image entry",
            fault is null && push.ProcessAttached && g.Returned && e.ModuleLog.Any(l => l.StartsWith("DllMain(PushNotificationWP8.dll, DLL_PROCESS_ATTACH) returned TRUE")),
            fault ?? e.StopReason ?? string.Join(" | ", e.ModuleLog));
        h.Check("no start-up initialisation failures", e.InitialisationFailures.Count == 0, string.Join(" | ", e.InitialisationFailures));

        // ---- LoadPackagedLibrary / FreeLibrary with a real DllMain ----
        h.Check("LoadPackagedLibrary with Reserved != 0 is ERROR_INVALID_PARAMETER", g.Call("LoadPackagedLibrary", g.Wide("IGPLib.dll"), 1) == 0 && g.LastError() == 87);
        h.Check("LoadPackagedLibrary of an absolute path is ERROR_INVALID_PARAMETER", g.Call("LoadPackagedLibrary", g.Wide(@"C:\IGPLib.dll"), 0) == 0 && g.LastError() == 87);
        h.Check("LoadPackagedLibrary leaving the package is ERROR_INVALID_PARAMETER", g.Call("LoadPackagedLibrary", g.Wide(@"..\IGPLib.dll"), 0) == 0 && g.LastError() == 87);
        h.Check("LoadPackagedLibrary of a missing file is NULL, ERROR_MOD_NOT_FOUND", g.Call("LoadPackagedLibrary", g.Wide("NoSuchLibrary.dll"), 0) == 0 && g.LastError() == 126);

        long igp = g.CallGuest("LoadPackagedLibrary", g.Wide("igplib"), 0);
        LoadedModule? igpModule = e.ModuleByHandle(igp);
        h.Check("LoadPackagedLibrary(\"igplib\") maps IGPLib.dll and returns its base", igp != 0 && igpModule is { Name: "IGPLib.dll" }, $"0x{igp:X8} {string.Join("; ", e.ModuleLog.TakeLast(3))}");
        if (igpModule is null)
        {
            return;
        }

        h.Check("...without overlapping anything", NoOverlap(e.Modules));
        h.Check("...relocated (its preferred base is taken too)", igpModule.ImageBase != igpModule.Image.ImageBase && igpModule.RelocationsApplied > 0);
        CheckRelocations(h, e, igpModule, "IGPLib");
        h.Check("...its DllMain(DLL_PROCESS_ATTACH, NULL) ran and returned TRUE", igpModule.ProcessAttached && e.ModuleLog.Any(l => l.StartsWith("DllMain(IGPLib.dll, DLL_PROCESS_ATTACH) returned TRUE (dynamic load)")));
        h.Check("...one reference, not pinned", igpModule.ReferenceCount == 1 && !igpModule.Pinned);
        h.Check("...control went back to the caller", g.Returned);
        h.Check("LoadPackagedLibrary again returns the same handle and adds a reference", g.CallGuest("LoadPackagedLibrary", g.Wide("IGPLIB.DLL"), 0) == igp && igpModule.ReferenceCount == 2);
        h.Check("GetModuleHandleExW(name) adds a reference", g.Call("GetModuleHandleExW", 0, g.Wide("IGPLib.dll"), handleOut) == 1 && e.ReadUInt32(handleOut) == igp && igpModule.ReferenceCount == 3);
        h.Check("FreeLibrary drops a reference and keeps it mapped", g.CallGuest("FreeLibrary", igp) == 1 && igpModule.ReferenceCount == 2 && !igpModule.Unloaded);
        g.CallGuest("FreeLibrary", igp);
        long igpExport = igpModule.ImageBase + igpModule.Image.Exports.Values.First();
        h.Check("the last FreeLibrary runs DllMain(DLL_PROCESS_DETACH) and unloads it",
            g.CallGuest("FreeLibrary", igp) == 1 && igpModule.Unloaded && !igpModule.ProcessAttached && e.ModuleLog.Any(l => l.StartsWith("unloaded IGPLib.dll")),
            string.Join(" | ", e.ModuleLog.TakeLast(3)));
        h.Check("...after which GetModuleHandleW no longer finds it and its code is not code", g.Call("GetModuleHandleW", g.Wide("IGPLib.dll")) == 0 && !e.IsExecutableCode(igpExport));
        h.Check("FreeLibrary of an unloaded handle fails", g.Call("FreeLibrary", igp) == 0 && g.LastError() == 126);
        long again = g.CallGuest("LoadPackagedLibrary", g.Wide("IGPLib.dll"), 0);
        h.Check("it can be loaded again, at a fresh range", again != 0 && again != igp && NoOverlap(e.Modules), $"0x{again:X8}");
        h.Check("DisableThreadLibraryCalls marks the module", g.Call("DisableThreadLibraryCalls", again) == 1 && e.ModuleByHandle(again)!.ThreadCallsDisabled);
    }

    // ---------------------------------------------------------------------------------------------

    private static void Fmod(Harness h)
    {
        string dir = Path.Combine(Apps, "8e162189-0baf-431e-966a-0d050eac9aab");
        string exe = Path.Combine(dir, "Game.exe");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(dir, "fmodex_arm.dll")))
        {
            Console.WriteLine("SKIP  the fmodex_arm.dll title is not installed");
            return;
        }

        using ArmEmulator e = new(PeImage.Load(exe), dir, collectBlockStats: false);
        LoadedModule main = e.Modules[0];
        LoadedModule? fmod = e.Modules.FirstOrDefault(m => m.Name.Equals("fmodex_arm.dll", StringComparison.OrdinalIgnoreCase));
        h.Check("FMOD: Game.exe is the main module at 0x400000, fmodex_arm.dll is mapped", main.ImageBase == 0x400000 && fmod is not null, string.Join("; ", e.Modules));
        if (fmod is null)
        {
            return;
        }

        h.Check("FMOD: its preferred base was free, so it is there unrelocated", fmod.ImageBase == fmod.Image.ImageBase && fmod.RelocationsApplied == 0);
        h.Check("FMOD: modules do not overlap", NoOverlap(e.Modules));
        List<ImportedFunction> imports = main.Image.Imports.Where(i => i.Dll.Equals(fmod.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        int bound = imports.Count(i => e.ReadUInt32(main.ImageBase + i.IatSlotRva) == ExpectedExport(fmod, i.Name));
        h.Check($"FMOD: all {imports.Count} of Game.exe's FMOD imports are bound to FMOD's exports", imports.Count > 0 && bound == imports.Count, $"{bound}/{imports.Count}");
        h.Check("FMOD: none of them is a host-stub trap any more", imports.All(i => e.TrapNameAt(e.ReadUInt32(main.ImageBase + i.IatSlotRva) & ~1L) is null));
        CheckFrameBase(h, e, fmod, "FMOD");

        Guest g = new(e);
        long start = e.StartWithModuleInitialisers(g.Sentinel);
        string? fault = g.RunUntilReturn(start);
        h.Check("FMOD: DllMain(DLL_PROCESS_ATTACH) ran before the exe's entry point and returned TRUE",
            fault is null && fmod.ProcessAttached && g.Returned,
            fault ?? e.StopReason ?? string.Join(" | ", e.ModuleLog));
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>The address an import of <paramref name="name"/> from a module must be bound to.</summary>
    private static long ExpectedExport(LoadedModule module, string name)
    {
        uint rva = name.StartsWith('#') ? module.Image.ExportsByOrdinal[uint.Parse(name[1..])] : module.Image.Exports[name];
        return module.ImageBase + rva;
    }

    private static bool NoOverlap(IReadOnlyList<LoadedModule> modules)
    {
        for (int i = 0; i < modules.Count; i++)
        {
            for (int j = i + 1; j < modules.Count; j++)
            {
                if (modules[i].ImageBase < modules[j].ImageBase + modules[j].MappedSize && modules[j].ImageBase < modules[i].ImageBase + modules[i].MappedSize)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a relocated word (HIGHLOW) and a relocated MOVW/MOVT pair out of guest memory and
    /// compares them with the file's value plus the load delta.
    /// </summary>
    private static void CheckRelocations(Harness h, ArmEmulator e, LoadedModule module, string label)
    {
        PeImage image = module.Image;
        uint delta = (uint)(module.ImageBase - image.ImageBase);
        (uint Rva, int Type)? highLow = null, mov32 = null;
        Dictionary<int, int> types = new();
        uint cursor = image.BaseRelocationRva, end = cursor + image.BaseRelocationSize;
        while (cursor + 8 <= end)
        {
            uint page = image.ReadUInt32AtRva(cursor)!.Value, size = image.ReadUInt32AtRva(cursor + 4)!.Value;
            if (size < 8)
            {
                break;
            }

            for (uint at = cursor + 8; at + 2 <= cursor + size; at += 2)
            {
                ushort entry = (ushort)(image.ReadUInt32AtRva(at & ~3u)!.Value >> (int)((at & 2) * 8));
                int type = entry >> 12;
                types[type] = types.GetValueOrDefault(type) + 1;
                uint rva = page + (uint)(entry & 0xFFF);
                if (type == 3) highLow ??= (rva, type);
                if (type is 5 or 7) mov32 ??= (rva, type);
            }

            cursor += size;
        }

        Console.WriteLine($"      {label}: relocation types {string.Join(", ", types.OrderBy(t => t.Key).Select(t => $"{t.Key}x{t.Value}"))}");

        if (highLow is { } word)
        {
            uint file = image.ReadUInt32AtRva(word.Rva)!.Value;
            h.Check($"{label}: a HIGHLOW-relocated pointer reads as its file value + delta", e.ReadUInt32(module.ImageBase + word.Rva) == unchecked(file + delta),
                $"0x{module.ImageBase + word.Rva:X8}: file 0x{file:X8} mem 0x{e.ReadUInt32(module.ImageBase + word.Rva):X8}");
        }

        if (mov32 is { } pair)
        {
            uint fileValue = Mov32(image.ReadUInt32AtRva(pair.Rva)!.Value, image.ReadUInt32AtRva(pair.Rva + 4)!.Value);
            uint memValue = Mov32(e.ReadUInt32(module.ImageBase + pair.Rva), e.ReadUInt32(module.ImageBase + pair.Rva + 4));
            h.Check($"{label}: a MOVW/MOVT-relocated (type {pair.Type}) address loads its file value + delta", memValue == unchecked(fileValue + delta),
                $"file 0x{fileValue:X8} mem 0x{memValue:X8}");
        }
        else
        {
            h.Check($"{label}: the image has MOVW/MOVT relocations to check", false, string.Join(",", types.Keys));
        }
    }

    private static uint Mov32(uint movw, uint movt)
    {
        static ushort Imm(uint instruction) => ArmEmulatorImm((ushort)instruction, (ushort)(instruction >> 16));
        return Imm(movw) | ((uint)Imm(movt) << 16);
    }

    private static ushort ArmEmulatorImm(ushort first, ushort second)
        => (ushort)(((first & 0xF) << 12) | (((first >> 10) & 1) << 11) | (((second >> 12) & 7) << 8) | (second & 0xFF));

    /// <summary>
    /// C++ EH reads every FuncInfo RVA against the image of the frame it belongs to: a frame in the
    /// DLL with a C++ handler is described from the DLL's FuncInfo, and the same RVA read against the
    /// main image finds no FuncInfo.
    /// </summary>
    private static void CheckFrameBase(Harness h, ArmEmulator e, LoadedModule module, string label)
    {
        PeImage image = module.Image;
        for (uint i = 0; i < image.ExceptionDirectorySize / 8; i++)
        {
            uint begin = e.ReadUInt32(module.ImageBase + image.ExceptionDirectoryRva + (i * 8));
            uint unwind = e.ReadUInt32(module.ImageBase + image.ExceptionDirectoryRva + (i * 8) + 4);
            if ((unwind & 3) != 0)
            {
                continue;
            }

            long record = module.ImageBase + unwind;
            uint header = e.ReadUInt32(record);
            if (((header >> 20) & 1) == 0)
            {
                continue;
            }

            int scopes = (int)((header >> 23) & 0x1F), words = (int)((header >> 28) & 0xF);
            long cursor = record + 4;
            if ((header >> 23) == 0)
            {
                uint extended = e.ReadUInt32(cursor);
                scopes = (int)(extended & 0xFFFF);
                words = (int)((extended >> 16) & 0xFF);
                cursor += 4;
            }

            if (((header >> 21) & 1) == 0)
            {
                cursor += scopes * 4L;
            }

            uint handlerData = e.ReadUInt32(cursor + (words * 4L) + 4);
            uint magic = e.ReadUInt32(module.ImageBase + handlerData, 0);
            if (magic is not (0x19930520 or 0x19930521 or 0x19930522))
            {
                continue;
            }

            UnwoundFrame inModule = new(module.ImageBase + (begin & ~1u) + 2, begin, 0, true, "xdata") { HandlerDataRva = handlerData, ImageBase = module.ImageBase };
            string described = e.ExceptionModel.DescribeFrame(inModule);
            UnwoundFrame mainRelative = inModule with { ImageBase = e.ImageBase };
            string wrong = e.ExceptionModel.DescribeFrame(mainRelative);
            h.Check($"{label}: a C++ frame in the DLL is described from the DLL's own FuncInfo",
                described.StartsWith("FuncInfo", StringComparison.Ordinal) && !wrong.StartsWith("FuncInfo", StringComparison.Ordinal),
                $"{described.Split('\n')[0]} / against the main image: {wrong.Split('\n')[0]}");
            return;
        }

        Console.WriteLine($"SKIP  {label}: the DLL has no C++ handler data to check frame bases with");
    }

    /// <summary>Calls import handlers on one emulator the way a trap does; runs any guest code they start.</summary>
    private sealed class Guest
    {
        private readonly ArmEmulator _e;
        private readonly long _stack;

        public Guest(ArmEmulator e)
        {
            _e = e;
            _stack = e.AllocateHeap(0x1000) + 0x800;

            // Where a guest call made from a handler comes back to: a trap slot that stops the CPU.
            // (A stop-at address would do, but the JIT engine has no code hooks.)
            Sentinel = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("contract test: returned", () =>
            {
                Returned = true;
                _e.Stop("returned to the contract test");
            }));
        }

        /// <summary>A Thumb address that is a stop point: the return address handed to every call.</summary>
        public long Sentinel { get; }

        /// <summary>The last run reached <see cref="Sentinel"/>.</summary>
        public bool Returned { get; private set; }

        /// <summary>Runs guest code from <paramref name="start"/> until it returns to <see cref="Sentinel"/>; the fault, or null.</summary>
        public string? RunUntilReturn(long start)
        {
            Returned = false;
            string? fault = _e.Run(start, 50_000_000);
            return fault ?? (Returned ? null : _e.StopReason ?? "the budget ran out before the call returned");
        }

        public long Call(string function, params long[] args)
        {
            Setup(args);
            _e.Stubs.Dispatch("kernel32.dll!" + function);
            return _e.ReadRegister(R0) & 0xFFFFFFFFL;
        }

        /// <summary>A call whose handler may continue in guest code (DllMain): runs it to the return.</summary>
        public long CallGuest(string function, params long[] args)
        {
            Setup(args);
            _e.Stubs.Dispatch("kernel32.dll!" + function);
            long next = _e.ReadRegister(R12);
            Returned = (next & ~1L) == (Sentinel & ~1L);
            if (!Returned)
            {
                string? fault = RunUntilReturn(next);
                if (fault is not null)
                {
                    Console.WriteLine($"      guest fault during {function}: {fault}");
                    return -1;
                }
            }

            return _e.ReadRegister(R0) & 0xFFFFFFFFL;
        }

        private void Setup(long[] args)
        {
            int[] registers = [R0, R1, R2, R3];
            for (int i = 0; i < args.Length && i < 4; i++) _e.WriteRegister(registers[i], args[i]);
            _e.WriteRegister(SP, _stack);

            // What EnterTrap does before a handler: lr is the caller, r12 defaults to it.
            _e.WriteRegister(LR, Sentinel);
            _e.WriteRegister(R12, Sentinel);
        }

        public long LastError() => Call("GetLastError");

        public long Wide(string text)
        {
            long p = _e.AllocateHeap((text.Length * 2) + 2);
            _e.WriteMemory(p, Encoding.Unicode.GetBytes(text + "\0"));
            return p;
        }

        public long Narrow(string text)
        {
            long p = _e.AllocateHeap(text.Length + 1);
            _e.WriteMemory(p, Encoding.ASCII.GetBytes(text + "\0"));
            return p;
        }

        public long Buffer(int size)
        {
            long p = _e.AllocateHeap(size);
            _e.WriteMemory(p, new byte[size]);
            return p;
        }
    }
}
