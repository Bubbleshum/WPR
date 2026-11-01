using System.Diagnostics;
using System.Reflection;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// A thread that sleeps with <c>Concurrency::wait</c> (a yield here) must still get its turn while
/// threads woken by real waits are preferred. N.O.V.A. 3's sound streamer sat between two such
/// workers (the mixer, and a thread the mixer wakes), and the preference used to move the fair
/// round robin past it every time: it never ran again, and the game went silent from its first
/// mission on.
/// </summary>
public sealed class SchedulerFairnessSuite : IContractSuite
{
    public string Name => "scheduler fairness";

    private const long Infinite = 0xFFFFFFFF;
    private const string ConcRtWait = "?wait@Concurrency@@YAXI@Z";

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        FieldInfo budgetStart = typeof(ArmEmulator).GetField("_workersSince", BindingFlags.NonPublic | BindingFlags.Instance)!;

        long never = h.Call("CreateEventExW", 0, 0, 0, 0x1F0003);
        long wakeA = h.Call("CreateEventExW", 0, 0, 0, 0x1F0003);
        long wakeB = h.Call("CreateEventExW", 0, 0, 0, 0x1F0003);

        // In thread order: a worker, the yielding streamer, a worker the first one wakes, and a
        // yielding thread that wakes the first worker (the host's audio event, in the game).
        int a = e.CreateGuestThread("fairness worker A", e.AllocateHeap(16), 0);
        int streamer = e.CreateGuestThread("fairness streamer", e.AllocateHeap(16), 0);
        int b = e.CreateGuestThread("fairness worker B", e.AllocateHeap(16), 0);
        int waker = e.CreateGuestThread("fairness waker", e.AllocateHeap(16), 0);

        h.Call("WaitForSingleObjectEx", never, Infinite, 0);

        int streamerTurns = 0;
        int steps = 0;
        for (; steps < 120 && e.StopReason is null; steps++)
        {
            // Inside the frame's worker budget, where woken workers are preferred.
            budgetStart.SetValue(e, Stopwatch.GetTimestamp());
            int current = e.CurrentThreadId;
            if (current == a)
            {
                h.Call("SetEvent", wakeB);
                h.Call("WaitForSingleObjectEx", wakeA, Infinite, 0);
            }
            else if (current == b)
            {
                h.Call("WaitForSingleObjectEx", wakeB, Infinite, 0);
            }
            else if (current == streamer)
            {
                streamerTurns++;
                h.Call(ConcRtWait, 1);
            }
            else if (current == waker)
            {
                h.Call("SetEvent", wakeA);
                h.Call(ConcRtWait, 1);
            }
            else
            {
                break;
            }
        }

        h.Check("a yielding thread between two woken workers still gets turns",
            streamerTurns >= 10, $"{streamerTurns} turn(s) in {steps} switch(es){(e.StopReason is null ? "" : $", stopped: {e.StopReason}")}");
    }
}
