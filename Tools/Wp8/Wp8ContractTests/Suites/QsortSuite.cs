using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// MSVCR110's qsort (CrtExtras.cs): sorts in place through the image's comparator, which is a guest
/// call per comparison, so it runs on the emulator. Checks the order, that equal keys keep their
/// order, element sizes other than 4, and that a large table takes n log n comparisons rather than
/// n squared (GTA: San Andreas sorts a few thousand entries while loading).
/// </summary>
public sealed class QsortSuite : IContractSuite
{
    public string Name => "qsort";

    private const int R0 = Harness.R0, R1 = Harness.R1, R2 = Harness.R2, R3 = Harness.R3, SP = Harness.SP, LR = 10, R12 = 78;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        HostStubs s = h.S;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Reg(int r) => e.ReadRegister(r) & 0xFFFFFFFFL;

        bool returned = false;
        long sentinel = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("qsort test: returned", () =>
        {
            returned = true;
            e.Stop("returned to the contract test");
        }));

        // The comparator orders by the first int of each element; a host trap standing in for guest code.
        int comparisons = 0;
        long comparator = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("qsort test comparator", () =>
        {
            comparisons++;
            int a = unchecked((int)e.ReadUInt32(Reg(R0)));
            int b = unchecked((int)e.ReadUInt32(Reg(R1)));
            e.WriteRegister(R0, (uint)a.CompareTo(b));
        }));

        string? Sort(long array, int count, int size)
        {
            comparisons = 0;
            returned = false;
            e.WriteRegister(R0, array);
            e.WriteRegister(R1, count);
            e.WriteRegister(R2, size);
            e.WriteRegister(R3, comparator);
            e.WriteRegister(SP, h.Stack);
            e.WriteRegister(LR, sentinel);
            e.WriteRegister(R12, sentinel);
            s.Dispatch("MSVCR110.dll!qsort");
            long next = Reg(R12);
            if ((next & ~1L) == (sentinel & ~1L))
            {
                return null;
            }

            string? fault = e.Run(next, 200_000_000);
            return returned ? null : fault ?? e.StopReason ?? "the budget ran out";
        }

        long Table(int[] keys, int size)
        {
            long p = h.Buffer(keys.Length * size);
            for (int i = 0; i < keys.Length; i++)
            {
                e.WriteUInt32(p + ((long)i * size), (uint)keys[i]);
                for (int b = 4; b + 4 <= size; b += 4)
                {
                    e.WriteUInt32(p + ((long)i * size) + b, (uint)i);   // the original position
                }
            }

            return p;
        }

        int[] Keys(long p, int count, int size) =>
            Enumerable.Range(0, count).Select(i => unchecked((int)e.ReadUInt32(p + ((long)i * size)))).ToArray();

        // ---- a small table of ints ----
        int[] small = [5, -3, 9, 0, 9, 2, -7, 1];
        long t = Table(small, 4);
        string? fault = Sort(t, small.Length, 4);
        int[] got = Keys(t, small.Length, 4);
        Check("qsort sorts ints ascending and returns to its caller", fault is null && got.SequenceEqual(small.Order()),
            fault ?? string.Join(",", got));

        // ---- 12-byte elements, equal keys keep their order, odd count ----
        int[] keyed = [3, 1, 3, 2, 1, 3, 2];
        t = Table(keyed, 12);
        fault = Sort(t, keyed.Length, 12);
        var rows = Enumerable.Range(0, keyed.Length)
            .Select(i => (Key: (int)e.ReadUInt32(t + (i * 12)), From: (int)e.ReadUInt32(t + (i * 12) + 4), Tail: (int)e.ReadUInt32(t + (i * 12) + 8)))
            .ToArray();
        var want = keyed.Select((k, i) => (Key: k, From: i, Tail: i)).OrderBy(r => r.Key).ToArray();   // OrderBy is stable
        Check("qsort moves whole 12-byte elements and keeps equal keys in order", fault is null && rows.SequenceEqual(want),
            fault ?? string.Join(" ", rows.Select(r => $"{r.Key}@{r.From}/{r.Tail}")));

        // ---- trivial inputs do not call the comparator ----
        t = Table([42], 4);
        fault = Sort(t, 1, 4);
        Check("qsort of one element calls nothing", fault is null && comparisons == 0 && e.ReadUInt32(t) == 42);

        // ---- a large table: n log n comparisons ----
        var random = new Random(1234);
        int[] large = Enumerable.Range(0, 4000).Select(_ => random.Next(-100_000, 100_000)).ToArray();
        t = Table(large, 8);
        fault = Sort(t, large.Length, 8);
        got = Keys(t, large.Length, 8);
        int bound = large.Length * 13;   // ceil(log2 4000) = 12
        Check("qsort sorts 4000 elements", fault is null && got.SequenceEqual(large.Order()), fault ?? "");
        Check($"... in at most n log2 n comparisons ({comparisons} <= {bound})", comparisons <= bound);
    }
}
