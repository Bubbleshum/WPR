using System.Text;
using WPR.Wp8Native;

/// <summary>
/// <c>--load</c>: the DLLs a title brings in with LoadPackagedLibrary at run time (Spark2's Lua
/// modules under Spark2Projects\), which the import-driven start-up never reaches. Each load is
/// made the way guest code makes it - the import handler, then its DllMains run as guest calls -
/// and each module mapped is checked import by import against what the host implements.
/// </summary>
internal static class LoadProbe
{
    private const int R0 = 66, R1 = 67, SP = 12, LR = 10, R12 = 78;
    private const long Budget = 200_000_000;

    public static int Run(string folder, string mainImage, string[] libraries)
    {
        HostStubs.SandboxRoot = Path.Combine(Path.GetTempPath(), "wp8-module-probe-sandbox", $"load-{Environment.ProcessId}");
        PeImage image = PeImage.Load(Path.Combine(folder, mainImage));
        using ArmEmulator e = new(image, folder, collectBlockStats: false);
        e.RecordCallSites = false;

        bool returned = false;
        long sentinel = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("load probe: returned", () =>
        {
            returned = true;
            e.Stop("returned to the load probe");
        }));
        long stack = e.AllocateHeap(0x10000) + 0xF000;

        void Setup(long r0, long r1)
        {
            e.WriteRegister(R0, r0);
            e.WriteRegister(R1, r1);
            e.WriteRegister(SP, stack);
            e.WriteRegister(LR, sentinel);
            e.WriteRegister(R12, sentinel);
        }

        string? RunTo(long start)
        {
            returned = false;
            string? fault = e.Run(start, Budget);
            return returned ? null : fault ?? e.StopReason ?? "budget exhausted";
        }

        // Start-up: the statically imported package DLLs' DllMains.
        Setup(0, 0);
        long start = e.StartWithModuleInitialisers(sentinel);
        string? startFault = start == sentinel ? null : RunTo(start);
        Console.WriteLine($"start-up   {e.Modules.Count - 1} package DLL(s) {(startFault is null ? "initialised" : "FAULT " + startFault)}");
        foreach (string failure in e.InitialisationFailures)
        {
            Console.WriteLine($"   FAILED  {failure}");
        }

        foreach (string library in libraries)
        {
            int before = e.ModuleLog.Count;
            byte[] name = Encoding.Unicode.GetBytes(library + "\0");
            long pointer = e.AllocateHeap(name.Length);
            e.WriteMemory(pointer, name);

            Setup(pointer, 0);
            e.Stubs.Dispatch("PhoneAppModelHost.dll!LoadPackagedLibrary");
            long next = e.ReadRegister(R12);
            string? fault = (next & ~1L) == (sentinel & ~1L) ? null : RunTo(next);
            long handle = e.ReadRegister(R0) & 0xFFFFFFFFL;
            Setup(0, 0);
            e.Stubs.Dispatch("kernel32.dll!GetLastError");
            long error = e.ReadRegister(R0) & 0xFFFFFFFFL;

            Console.WriteLine();
            Console.WriteLine($"== LoadPackagedLibrary(\"{library}\") = 0x{handle:X8}" + (handle == 0 ? $", GetLastError {error}" : string.Empty) + (fault is null ? string.Empty : $"  FAULT {fault}"));
            foreach (string line in e.ModuleLog.Skip(before))
            {
                Console.WriteLine($"   loader   {line}");
            }

            // What Lua's package.loadlib does next: GetProcAddress(lib, "luaopen_<name>").
            if (e.ModuleByHandle(handle) is { } loaded)
            {
                foreach (string export in loaded.Image.Exports.Keys.Where(k => k.StartsWith("luaopen_", StringComparison.Ordinal)))
                {
                    byte[] ascii = Encoding.ASCII.GetBytes(export + "\0");
                    long namePointer = e.AllocateHeap(ascii.Length);
                    e.WriteMemory(namePointer, ascii);
                    Setup(handle, namePointer);
                    e.Stubs.Dispatch("api-ms-win-core-libraryloader-l1-1-1.dll!GetProcAddress");
                    long address = e.ReadRegister(R0) & 0xFFFFFFFFL;
                    bool right = address == loaded.ImageBase + loaded.Image.Exports[export];
                    Console.WriteLine($"   GetProcAddress({export}) = 0x{address:X8}{(right ? string.Empty : "  WRONG")}");
                }
            }
        }

        Console.WriteLine();
        foreach (LoadedModule module in e.Modules.Skip(1))
        {
            List<string> missing = module.Image.Imports
                .Where(i => ArmEmulator.IsSystemDll(i.Dll) && !e.Stubs.IsImplemented(i.Dll, i.Name))
                .Select(i => i.FullName)
                .ToList();
            List<string> unbound = module.Image.Imports
                .Where(i => !ArmEmulator.IsSystemDll(i.Dll) && e.TrapNameAt(e.ReadUInt32(module.ImageBase + i.IatSlotRva) & ~1L) is not null)
                .Select(i => i.FullName)
                .ToList();
            Console.WriteLine($"module     {module.GuestPath}  imports={module.Image.Imports.Count} attached={module.ProcessAttached}" +
                (missing.Count == 0 && unbound.Count == 0 ? "  all implemented / bound" : string.Empty));
            foreach (string import in missing)
            {
                Console.WriteLine($"   NOT IMPLEMENTED {import}");
            }

            foreach (string import in unbound)
            {
                Console.WriteLine($"   NOT BOUND       {import}");
            }
        }

        foreach (string line in e.UnresolvedModuleImports.Concat(e.UnresolvedProcedures.Distinct()))
        {
            Console.WriteLine($"UNRESOLVED {line}");
        }

        foreach ((string function, int count) in e.Stubs.DefaultedCalls)
        {
            Console.WriteLine($"DEFAULTED  {function} x{count}");
        }

        if (e.Stubs.FirstUnimplementedImport is { } trapped)
        {
            Console.WriteLine($"TRAPPED    {trapped} (from 0x{e.Stubs.FirstUnimplementedCaller:X8})");
        }

        Console.WriteLine($"instructions {e.Cpu.InstructionsRetired:N0}");
        return 0;
    }
}
