// Runs installed WP8 native titles headless for a bounded budget and reports the module loader's
// work: package DLLs mapped, relocations, DllMain results, unresolved imports, how far the run got.
//
//   Wp8ModuleProbe [<budget in millions of instructions>] [<product id or folder>...]
using WPR.Wp8Native;

long budget = 200_000_000;
List<string> wanted = new();
foreach (string arg in args)
{
    if (long.TryParse(arg, out long millions))
    {
        budget = millions * 1_000_000;
    }
    else
    {
        wanted.Add(arg);
    }
}

string apps = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR", "AppData");
IEnumerable<string> folders = wanted.Count > 0
    ? wanted.Select(w => Directory.Exists(w) ? w : Path.Combine(apps, w))
    : Directory.Exists(apps) ? Directory.GetDirectories(apps).OrderBy(d => d, StringComparer.Ordinal) : [];

string sandbox = Path.Combine(Path.GetTempPath(), "wp8-module-probe-sandbox");
Console.WriteLine($"module loader {(ArmEmulator.LoaderDisabled ? "DISABLED (WPR_WP8_NO_MODULES=1)" : "enabled")}, budget {budget:N0}");

foreach (string folder in folders)
{
    string? main = MainImage(folder);
    if (main is null)
    {
        continue;
    }

    // A fresh sandbox per title: the previous emulator may still hold its files open.
    HostStubs.SandboxRoot = Path.Combine(sandbox, $"{Path.GetFileName(folder)}-{Environment.ProcessId}");

    Console.WriteLine();
    Console.WriteLine($"== {Path.GetFileName(folder)}  {Path.GetFileName(main)}");
    try
    {
        PeImage image = PeImage.Load(main);
        using ArmEmulator e = new(image, folder, collectBlockStats: false);
        e.RecordCallSites = false;
        foreach (LoadedModule module in e.Modules.Skip(1))
        {
            Console.WriteLine($"   module    {module}");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? fault;
        if (image.IsDll)
        {
            XamlShell? shell = XamlShells.ForComponent(main);
            if (shell is null)
            {
                Console.WriteLine("   (a component with no XAML shell; not run)");
                continue;
            }

            WinmdReader metadata = WinmdReader.Load(Directory.GetFiles(folder, "*.winmd"));
            fault = e.Run(e.WinRt.StartComponent(image, metadata, shell), budget);
        }
        else
        {
            fault = e.RunEntryPoint(budget);
        }

        foreach (string line in e.ModuleLog.Where(l => !l.StartsWith("mapped ", StringComparison.Ordinal)))
        {
            Console.WriteLine($"   loader    {line}");
        }

        foreach (string line in e.InitialisationFailures.Concat(e.UnresolvedModuleImports).Concat(e.UnresolvedProcedures.Distinct()))
        {
            Console.WriteLine($"   UNRESOLVED {line}");
        }

        Console.WriteLine($"   stopped   {fault ?? e.StopReason ?? "budget exhausted"}  ({clock.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine($"   progress  {e.ProcessEventsCalls} frame(s), {e.WinRt.ComponentFrames} component frame(s), {e.GuestThreadsStarted} thread(s), {e.Cpu.InstructionsRetired:N0} instructions");
        foreach (string line in e.ThreadLog.Where(l => l.Contains("ended", StringComparison.Ordinal)).Take(4))
        {
            Console.WriteLine($"   thread    {line}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   host exception: {ex.GetType().Name}: {ex.Message}");
    }
}

return 0;

// The ARM image the launcher would run: an exe in the root, or a known XAML component DLL.
static string? MainImage(string folder)
{
    foreach (string exe in Directory.GetFiles(folder, "*.exe"))
    {
        if (IsArm(exe))
        {
            return exe;
        }
    }

    foreach (string component in XamlShells.ComponentDlls)
    {
        string path = Path.Combine(folder, component);
        if (File.Exists(path))
        {
            return path;
        }
    }

    return null;
}

static bool IsArm(string path)
{
    try
    {
        PeImage image = PeImage.Load(path);
        return image.Machine == PeImage.MachineArmNt && !image.IsManaged;
    }
    catch (Exception)
    {
        return false;
    }
}
