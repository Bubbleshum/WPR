using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// LoadPackagedLibrary / LoadLibraryExW / GetProcAddress / GetModuleHandle / FreeLibrary over DLLs
/// in package SUBFOLDERS, in the shape Spark2 (Assassin's Creed Pirates) uses them: Lua.dll's
/// <c>package.loadlib</c> converts each <c>package.cpath</c> candidate with mbstowcs_s and hands it to
/// <c>LoadPackagedLibrary(path, 0)</c> verbatim - <c>Spark2Projects\_Bink\LuaBink.dll</c>, or
/// <c>.\LuaBink.dll</c> from the default cpath - then <c>GetProcAddress(lib, "luaopen_x")</c>.
/// The package also carries a Lua module and a WinRT component that share a file name
/// (<c>Spark2Projects\_MobileSDK\LuaMobileSDK.dll</c> and <c>LuaMobileSDK.dll</c>), and x86
/// leftovers (wrap_oal.dll, OpenAL32.dll, binkw32.dll) that can never load on ARM.
/// Built from <see cref="SyntheticPe"/> images, so it needs no installed title.
/// </summary>
public sealed class PackagedLibrarySuite : IContractSuite
{
    private const ushort RootTag = 0x11, LuaTag = 0x21, DupRootTag = 0xD1, DupSubTag = 0xD2;
    private const uint Attach = 1, Detach = 0;

    private const string LuaPath = @"Spark2Projects\_Deep\_Deeper\LuaThing.dll";

    public string Name => "packaged library loader";

    public void Run(Harness h)
    {
        string package = Path.Combine(Path.GetTempPath(), $"wp8-packaged-library-suite-{Environment.ProcessId}");
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

    // ---------------------------------------------------------------------------------------------

    /// <summary>A relocatable DLL whose DllMain logs <paramref name="tag"/>; exports "Fn".</summary>
    private static SyntheticPe Dll(ushort tag, out uint fn)
    {
        SyntheticPe dll = new(0x10000000, dll: true);
        dll.EntryRva = dll.LogFunction(tag);
        fn = dll.Return();
        dll.Export("Fn", fn);
        dll.Va(dll.Here);   // a base relocation: every one of these is mapped away from 0x10000000
        return dll;
    }

    private static void Write(string package, string relative, byte[] image)
    {
        string path = Path.Combine(package, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image);
    }

    private static void Synthetic(Harness h, string package)
    {
        if (Directory.Exists(package))
        {
            Directory.Delete(package, recursive: true);
        }

        Directory.CreateDirectory(package);

        Write(package, "Root.dll", Dll(RootTag, out _).Build());
        Write(package, "Dup.dll", Dll(DupRootTag, out _).Build());
        Write(package, @"Sub\Dup.dll", Dll(DupSubTag, out _).Build());
        Write(package, @"Spark2Projects\_Other\LuaOther.dll", Dll(0x22, out _).Build());

        // The Lua module: a subfolder DLL that statically imports a package-root DLL.
        SyntheticPe lua = Dll(LuaTag, out _);
        uint luaopen = lua.Return();
        lua.Export("luaopen_thing", luaopen);
        lua.Import("Root.dll", "Fn");
        Write(package, LuaPath, lua.Build());

        // An x86 image, as wrap_oal.dll / OpenAL32.dll / binkw32.dll are.
        byte[] x86 = Dll(0x86, out _).Build();
        x86[0x44] = 0x4C;
        x86[0x45] = 0x01;
        Write(package, @"Spark2Projects\_Sound\wrap_oal.dll", x86);

        // A DllMain that refuses: movs r0, #0; bx lr.
        SyntheticPe refuses = new(0x10000000, dll: true);
        uint refuse = refuses.Align(4);
        refuses.Bytes(0x00, 0x20, 0x70, 0x47);
        refuses.Va(refuses.Here);
        refuses.EntryRva = refuse + 1;
        refuses.Export("Fn", refuse + 1);
        Write(package, @"Spark2Projects\_Bad\Refuses.dll", refuses.Build());

        SyntheticPe exe = new(0x400000, dll: false);
        exe.EntryRva = exe.Return();
        string exePath = Path.Combine(package, "Host.exe");
        File.WriteAllBytes(exePath, exe.Build());

        using ArmEmulator e = new(PeImage.Load(exePath), package, collectBlockStats: false);
        e.Cpu.MemMap(SyntheticPe.LogPage, 0x1000, 3);
        ModuleLoaderSuite.Guest g = new(e);
        e.Run(e.StartWithModuleInitialisers(g.Sentinel), 1_000_000);

        // ---- a module in a subfolder ----
        ClearLog(e);
        long lib = g.CallGuest("LoadPackagedLibrary", g.Wide(LuaPath), 0);
        LoadedModule? module = e.ModuleByHandle(lib);
        LoadedModule? root = e.Modules.FirstOrDefault(m => m.Name == "Root.dll");
        h.Check("LoadPackagedLibrary(\"Spark2Projects\\_Deep\\_Deeper\\LuaThing.dll\") maps the subfolder DLL", lib != 0 && module is { Name: "LuaThing.dll" }, $"0x{lib:X8} {string.Join(" | ", e.ModuleLog.TakeLast(4))}");
        if (module is null || root is null)
        {
            return;
        }

        h.Check("...and control returned to the caller", g.Returned);
        h.Check("...its GetModuleFileName is the subfolder path in the install folder",
            e.ModuleFileName(lib) == ArmEmulator.InstallFolder + LuaPath, e.ModuleFileName(lib) ?? "null");
        h.Check("...its static import of a package-ROOT DLL bound to that DLL's export",
            e.ReadUInt32(module.ImageBase + module.Image.Imports.Single(i => i.Dll == "Root.dll").IatSlotRva) == root.ImageBase + root.Image.Exports["Fn"]);
        h.Check("...DllMain(PROCESS_ATTACH): the dependency's first, then the module's",
            Log(e).SequenceEqual([Entry(RootTag, Attach), Entry(LuaTag, Attach)]), Describe(Log(e)));
        h.Check("...one reference, not pinned", module.ReferenceCount == 1 && !module.Pinned);

        // ---- the same file by other spellings: one module, more references, no second DllMain ----
        ClearLog(e);
        h.Check("forward slashes name the same module (a reference added)",
            g.CallGuest("LoadPackagedLibrary", g.Wide("Spark2Projects/_Deep/_Deeper/LuaThing.dll"), 0) == lib && module.ReferenceCount == 2);
        h.Check("any case, mixed separators and no extension name the same module",
            g.CallGuest("LoadPackagedLibrary", g.Wide(@"spark2projects/_DEEP\_deeper\LUATHING"), 0) == lib && module.ReferenceCount == 3);
        h.Check("\".\" and empty segments are folded (.\\a\\.\\b\\\\c.dll)",
            g.CallGuest("LoadPackagedLibrary", g.Wide(@".\Spark2Projects\.\_Deep\\_Deeper\LuaThing.dll"), 0) == lib && module.ReferenceCount == 4);
        h.Check("...and none of those ran DllMain again", Log(e).Count == 0, Describe(Log(e)));

        // ---- lookups ----
        h.Check("GetModuleHandleW(base name) finds the subfolder module", g.Call("GetModuleHandleW", g.Wide("luathing.dll")) == lib);
        h.Check("GetModuleHandleW(its package path) finds it", g.Call("GetModuleHandleW", g.Wide(@"Spark2Projects\_Deep\_Deeper\LuaThing.dll")) == lib);
        h.Check("GetModuleHandleW(its install-folder path) finds it", g.Call("GetModuleHandleW", g.Wide(ArmEmulator.InstallFolder + LuaPath)) == lib);
        h.Check("GetModuleHandleW(another folder, same file name) is NULL, ERROR_MOD_NOT_FOUND",
            g.Call("GetModuleHandleW", g.Wide(@"Spark2Projects\LuaThing.dll")) == 0 && g.LastError() == 126);

        long luaopenAddress = g.Call("GetProcAddress", lib, g.Narrow("luaopen_thing"));
        h.Check("GetProcAddress(lib, \"luaopen_thing\") is the relocated export", luaopenAddress == module.ImageBase + luaopen, $"0x{luaopenAddress:X8}");
        uint ordinal = module.Image.ExportsByOrdinal.First(kv => kv.Value == luaopen).Key;
        h.Check("GetProcAddress by ordinal agrees", g.Call("GetProcAddress", lib, ordinal) == module.ImageBase + luaopen, $"#{ordinal}");
        h.Check("GetProcAddress of a missing export is NULL, ERROR_PROC_NOT_FOUND",
            g.Call("GetProcAddress", lib, g.Narrow("luaopen_missing")) == 0 && g.LastError() == 127);

        // ---- what is refused, and with what ----
        int mapped = e.Modules.Count;
        foreach ((string path, string what) in new[]
        {
            (@"C:\Data\Programs\Install\" + LuaPath, "an absolute path, even into the package"),
            (@"\Spark2Projects\_Deep\_Deeper\LuaThing.dll", "a rooted path"),
            (@"\\server\share\LuaThing.dll", "a UNC path"),
            (@"C:LuaThing.dll", "a drive-relative path"),
            (@"Spark2Projects\..\Root.dll", "a \"..\" segment inside the package"),
            (@"..\Root.dll", "a path leaving the package"),
            ("", "an empty name"),
        })
        {
            h.Check($"LoadPackagedLibrary refuses {what}: NULL, ERROR_INVALID_PARAMETER",
                g.Call("LoadPackagedLibrary", g.Wide(path), 0) == 0 && g.LastError() == 87);
        }

        h.Check("LoadPackagedLibrary(NULL) is ERROR_INVALID_PARAMETER", g.Call("LoadPackagedLibrary", 0, 0) == 0 && g.LastError() == 87);
        h.Check("LoadPackagedLibrary with Reserved != 0 is ERROR_INVALID_PARAMETER", g.Call("LoadPackagedLibrary", g.Wide(LuaPath), 1) == 0 && g.LastError() == 87);
        h.Check("a bare name is looked for in the package root only (LuaOther.dll is in a subfolder): ERROR_MOD_NOT_FOUND",
            g.Call("LoadPackagedLibrary", g.Wide("LuaOther.dll"), 0) == 0 && g.LastError() == 126);
        h.Check("a missing file in an existing folder is ERROR_MOD_NOT_FOUND",
            g.Call("LoadPackagedLibrary", g.Wide(@"Spark2Projects\_Deep\Nope.dll"), 0) == 0 && g.LastError() == 126);
        h.Check("a missing folder is ERROR_MOD_NOT_FOUND",
            g.Call("LoadPackagedLibrary", g.Wide(@"Spark2Projects\_Nope\LuaThing.dll"), 0) == 0 && g.LastError() == 126);
        h.Check("a folder is not a module: ERROR_MOD_NOT_FOUND",
            g.Call("LoadPackagedLibrary", g.Wide(@"Spark2Projects\_Deep\_Deeper."), 0) == 0 && g.LastError() == 126);
        h.Check("an x86 image (wrap_oal.dll) is NULL, ERROR_BAD_EXE_FORMAT, and nothing is mapped",
            g.Call("LoadPackagedLibrary", g.Wide(@"Spark2Projects\_Sound\wrap_oal.dll"), 0) == 0 && g.LastError() == 193 && e.Modules.Count == mapped);
        h.Check("...LoadLibraryExW agrees", g.Call("LoadLibraryExW", g.Wide(@"Spark2Projects\_Sound\wrap_oal.dll"), 0, 0) == 0 && g.LastError() == 193);

        ClearLog(e);
        h.Check("a DllMain returning FALSE: NULL, ERROR_DLL_INIT_FAILED",
            g.CallGuest("LoadPackagedLibrary", g.Wide(@"Spark2Projects\_Bad\Refuses.dll"), 0) == 0 && g.LastError() == 1114);
        h.Check("...and the module is not left loaded", e.Modules.All(m => m.Name != "Refuses.dll") && g.Call("GetModuleHandleW", g.Wide("Refuses.dll")) == 0);

        // ---- two files with one name: Spark2's LuaMobileSDK.dll ----
        ClearLog(e);
        long sub = g.CallGuest("LoadPackagedLibrary", g.Wide(@"Sub\Dup.dll"), 0);
        long rootDup = g.CallGuest("LoadPackagedLibrary", g.Wide(@".\Dup.dll"), 0);
        h.Check("Sub\\Dup.dll and .\\Dup.dll (the root file) are two modules", sub != 0 && rootDup != 0 && sub != rootDup, $"0x{sub:X8} 0x{rootDup:X8}");
        h.Check("...each ran its own DllMain", Log(e).SequenceEqual([Entry(DupSubTag, Attach), Entry(DupRootTag, Attach)]), Describe(Log(e)));
        h.Check("...each reports its own file",
            e.ModuleFileName(sub) == ArmEmulator.InstallFolder + @"Sub\Dup.dll" && e.ModuleFileName(rootDup) == ArmEmulator.InstallFolder + "Dup.dll");
        h.Check("...and naming either path again returns that one",
            g.CallGuest("LoadPackagedLibrary", g.Wide("sub/dup"), 0) == sub && g.CallGuest("LoadPackagedLibrary", g.Wide(@".\DUP.DLL"), 0) == rootDup);

        // ---- LoadLibraryExW with an install-folder path is the same module ----
        h.Check("LoadLibraryExW(install folder + package path) returns the same module, a reference added",
            g.CallGuest("LoadLibraryExW", g.Wide(ArmEmulator.InstallFolder + LuaPath), 0, 0) == lib && module.ReferenceCount == 5);

        // ---- FreeLibrary: references, then DETACH, dependants before dependencies ----
        ClearLog(e);
        for (int i = 0; i < 4; i++)
        {
            g.CallGuest("FreeLibrary", lib);
        }

        h.Check("four of five FreeLibrary calls leave it loaded, no DllMain",
            !module.Unloaded && module.ReferenceCount == 1 && Log(e).Count == 0, $"refs={module.ReferenceCount} {Describe(Log(e))}");
        long rootFn = root.ImageBase + root.Image.Exports["Fn"];
        h.Check("the last FreeLibrary returns TRUE", g.CallGuest("FreeLibrary", lib) == 1);
        h.Check("...DllMain(PROCESS_DETACH) for the module, then for the dependency it alone held",
            Log(e).SequenceEqual([Entry(LuaTag, Detach), Entry(RootTag, Detach)]), Describe(Log(e)));
        h.Check("...both are unloaded", module.Unloaded && root.Unloaded && !e.IsExecutableCode(rootFn));
        h.Check("...GetModuleHandleW no longer finds it", g.Call("GetModuleHandleW", g.Wide("LuaThing.dll")) == 0 && g.LastError() == 126);
        h.Check("...GetProcAddress on the stale handle is NULL, ERROR_MOD_NOT_FOUND",
            g.Call("GetProcAddress", lib, g.Narrow("luaopen_thing")) == 0 && g.LastError() == 126);
        h.Check("...FreeLibrary on it again is FALSE, ERROR_MOD_NOT_FOUND", g.Call("FreeLibrary", lib) == 0 && g.LastError() == 126);

        ClearLog(e);
        long again = g.CallGuest("LoadPackagedLibrary", g.Wide("Spark2Projects/_Deep/_Deeper/LuaThing.dll"), 0);
        LoadedModule? reloaded = e.ModuleByHandle(again);
        h.Check("it loads again, both DllMains run again, and its export resolves",
            reloaded is { Unloaded: false } && Log(e).SequenceEqual([Entry(RootTag, Attach), Entry(LuaTag, Attach)])
            && g.Call("GetProcAddress", again, g.Narrow("luaopen_thing")) == reloaded.ImageBase + luaopen,
            Describe(Log(e)));
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
