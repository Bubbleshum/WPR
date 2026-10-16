using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// <c>WaitForSingleObjectEx(GetCurrentThread(), ms, FALSE)</c>: WP8's Sleep. The store API has no
/// Sleep, so SDL's WinRT port (2020: My Country) and others delay by waiting on their own thread,
/// which is signalled only when it ends and so can only time out. It used to be answered as
/// signalled at once, never giving the CPU away: a worker polling with it starved the main thread
/// and the title presented nothing, for ever.
/// </summary>
public sealed class SleepOnSelfSuite : IContractSuite
{
    public string Name => "sleep on self";

    private const long CurrentThread = 0xFFFFFFFE;
    private const long WaitTimeout = 0x102;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        h.Check("GetCurrentThread is the (HANDLE)-2 pseudo-handle", h.Call("GetCurrentThread") == CurrentThread);
        h.Check("waiting on yourself with 0 ms times out (it is never signalled while you run)",
            h.Call("WaitForSingleObjectEx", CurrentThread, 0, 0) == WaitTimeout);
        h.Check("... with a timeout and no other thread to run, it times out too, at once",
            h.Call("WaitForSingleObjectEx", CurrentThread, 5, 0) == WaitTimeout);

        long array = h.Buffer(4);
        e.WriteUInt32(array, (uint)CurrentThread);
        h.Check("WaitForMultipleObjectsEx on yourself times out",
            h.Call("WaitForMultipleObjectsEx", 1, array, 0, 0, 0) == WaitTimeout);

        // With another runnable thread the wait is a sleep: the CPU goes to it.
        int other = e.CreateGuestThread("sleep-on-self test", e.AllocateHeap(16), 0);
        int switches = e.ThreadSwitches;
        h.Call("WaitForSingleObjectEx", CurrentThread, 50, 0);
        h.Check("a timed wait on yourself hands the CPU to another runnable thread",
            e.ThreadSwitches == switches + 1 && e.CurrentThreadId == other && e.IsThreadBlocked(1),
            $"switches {switches} -> {e.ThreadSwitches}, current thread {e.CurrentThreadId}");
    }
}
