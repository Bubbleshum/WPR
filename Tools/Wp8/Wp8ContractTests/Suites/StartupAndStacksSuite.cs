using System.Reflection;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The CRT's static initialiser walk (_initterm over a table as large as Halo: Spartan Assault's
/// 37,303 entries) and what counts as a stack address once guest threads exist.
/// </summary>
public sealed class StartupAndStacksSuite : IContractSuite
{
    public string Name => "startup and stacks";

    private const int SP = Harness.SP, LR = 10, R12 = 78;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);

        bool returned = false;
        long sentinel = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("startup test: returned", () =>
        {
            returned = true;
            e.Stop("returned to the contract test");
        }));

        int runs = 0;
        long initialiser = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("startup test: initialiser", () => runs++));

        long RunInitTerm(long first, long last)
        {
            e.WriteRegister(Harness.R0, first);
            e.WriteRegister(Harness.R1, last);
            e.WriteRegister(SP, h.Stack);
            e.WriteRegister(LR, sentinel);
            e.WriteRegister(R12, sentinel);
            h.S.Dispatch("MSVCR110.dll!_initterm");
            long next = e.ReadRegister(R12) & 0xFFFFFFFFL;
            returned = false;
            if ((next & ~1L) != (sentinel & ~1L))
            {
                e.Run(next, 200_000_000);
            }

            return e.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
        }

        // 20,000 entries is 80 KB, past the 64 KB the walk used to reject as implausible; the
        // gaps are the zero padding a real __xc_a..__xc_z carries.
        const int entries = 20_000;
        long table = e.AllocateHeap(entries * 4);
        for (int i = 0; i < entries; i++)
        {
            e.WriteUInt32(table + (i * 4), i % 10 == 9 ? 0u : (uint)initialiser);
        }

        RunInitTerm(table, table + (entries * 4));
        Check("_initterm runs every initialiser of an 80 KB table, skipping the zero entries", runs == entries - (entries / 10) && returned,
            $"ran {runs}, returned {returned}");
        Check("and the count is reported", h.E.StaticInitialisersRun >= entries - (entries / 10));

        runs = 0;
        long result = RunInitTerm(table, 0x7FFF_0000);
        Check("a table whose end is not mapped is rejected, running nothing", runs == 0 && result == 0, $"ran {runs}");

        // ---- stacks ----
        long heapBlock = e.AllocateHeap(64);
        Check("a heap block is not a stack address", !ArmEmulator.IsStackAddress(heapBlock));
        int thread = e.CreateGuestThread("stack test", initialiser, 0);
        var stacks = (System.Collections.IList)typeof(ArmEmulator)
            .GetField("GuestThreadStacks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        (long low, long high) = ((long, long))stacks[stacks.Count - 1]!;
        Check($"a guest thread's stack (thread {thread}) counts as stack, end to end",
            ArmEmulator.IsStackAddress(low) && ArmEmulator.IsStackAddress(high - 4) && !ArmEmulator.IsStackAddress(high));
        Check("the main stack still does", ArmEmulator.IsStackAddress(0x50000000L + 0x100));
    }
}
