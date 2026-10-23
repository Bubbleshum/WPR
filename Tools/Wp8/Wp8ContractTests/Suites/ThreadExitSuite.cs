using System.Diagnostics;
using System.Reflection;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// A guest thread that ends while every other thread is blocked. "Nothing can run at this instant"
/// is a deadlock only when every wait is untimed: a thread in a timed wait runs again when its time
/// runs out. FMOD sleeps with a 2 ms <c>WaitForSingleObjectEx</c> on an event nobody sets, and a worker
/// that ended inside those 2 ms stopped Halo: Spartan Assault with "guest thread N exited and no
/// thread is runnable" (the top WPR Hub crash group of 0.2.06).
/// </summary>
public sealed class ThreadExitSuite : IContractSuite
{
    public string Name => "thread exit";

    private const long WaitTimeout = 0x102;
    private const long Infinite = 0xFFFFFFFF;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        MethodInfo exit = typeof(ArmEmulator).GetMethod("OnThreadExit", BindingFlags.NonPublic | BindingFlags.Instance)!;
        long neverSet = h.Call("CreateEventExW", 0, 0, 0, 0x1F0003);

        // Main waits 40 ms on an event nobody sets; the CPU goes to the worker, which then ends.
        int worker = e.CreateGuestThread("thread exit test", e.AllocateHeap(16), 0);
        h.Call("WaitForSingleObjectEx", neverSet, 40, 0);
        h.Check("a timed wait with another thread runnable hands the CPU over", e.CurrentThreadId == worker && e.IsThreadBlocked(1),
            $"current thread {e.CurrentThreadId}");

        Stopwatch clock = Stopwatch.StartNew();
        exit.Invoke(e, null);
        h.Check("a thread ending while the main thread is in a timed wait does not stop the run",
            e.StopReason is null, e.StopReason ?? "");
        h.Check("... the CPU goes back to the main thread once its wait has run out",
            e.CurrentThreadId == 1 && e.IsThreadFinished(worker), $"current thread {e.CurrentThreadId}");
        h.Check("... whose wait answers WAIT_TIMEOUT", (e.ReadRegister(Harness.R0) & 0xFFFFFFFF) == WaitTimeout,
            $"r0 0x{e.ReadRegister(Harness.R0):X}");
        h.Check("... after the time really passed (not answered early)", clock.ElapsedMilliseconds >= 30, $"{clock.ElapsedMilliseconds} ms");
        h.Check("... and it is counted", e.TimedWaitsWaitedOut == 1, $"{e.TimedWaitsWaitedOut}");

        // An event the worker sets before it ends: main is runnable at once, no waiting.
        long set = h.Call("CreateEventExW", 0, 0, 0, 0x1F0003);
        worker = e.CreateGuestThread("thread exit test 2", e.AllocateHeap(16), 0);
        h.Call("WaitForSingleObjectEx", set, 5000, 0);
        h.Call("SetEvent", set);
        clock.Restart();
        exit.Invoke(e, null);
        h.Check("a wait the ending thread satisfied resumes at once, signalled",
            e.StopReason is null && e.CurrentThreadId == 1 && (e.ReadRegister(Harness.R0) & 0xFFFFFFFF) == 0 && clock.ElapsedMilliseconds < 1000,
            $"r0 0x{e.ReadRegister(Harness.R0):X}, {clock.ElapsedMilliseconds} ms");

        // An untimed wait nobody can satisfy is still a deadlock, reported as one.
        worker = e.CreateGuestThread("thread exit test 3", e.AllocateHeap(16), 0);
        h.Call("WaitForSingleObjectEx", neverSet, Infinite, 0);
        exit.Invoke(e, null);
        h.Check("a thread ending while the rest wait for ever still stops the run",
            e.StopReason?.Contains("no thread is runnable", StringComparison.Ordinal) == true, e.StopReason ?? "(none)");

        MandreelShells(h);
    }

    /// <summary>
    /// Mandreel titles answer their native side from a thread of the managed page's own; the inferred
    /// page has to say so, or the first message is a deadlock (Fruit Ninja 2013, Jetpack Joyride).
    /// Checked for whichever are installed (the real data root, or WPR_DATA_ROOT's).
    /// </summary>
    private static void MandreelShells(Harness h)
    {
        string local = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR");
        List<string> folders = new[] { Environment.GetEnvironmentVariable("WPR_DATA_ROOT"), local }
            .Where(root => !string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root!, "AppData")))
            .SelectMany(root => Directory.GetDirectories(Path.Combine(root!, "AppData")))
            .Where(folder => File.Exists(Path.Combine(folder, "__MandreelWP8Direct3DInterop.winmd")))
            .ToList();
        if (folders.Count == 0)
        {
            Console.WriteLine("  (skipped Mandreel shells: none installed)");
            return;
        }

        foreach (string folder in folders)
        {
            XamlShell? shell = XamlShellInference.Infer(Path.Combine(folder, "PhoneDLL1.dll"), WinmdReader.Load(Directory.GetFiles(folder, "*.winmd")));
            h.Check($"{Path.GetFileName(folder)}: the inferred Mandreel page runs its message thread", shell?.MandreelMessages == true,
                shell is null ? "no shell" : XamlShellInference.Describe(shell));
        }
    }
}
