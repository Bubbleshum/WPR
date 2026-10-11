using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The CPU's memory contract, run as real guest code on dynarmic, plus the Win32 heap family.
/// </summary>
/// <remarks>
/// The straddling-access checks are the regression guard for MONOPOLY Millionaire's native abort:
/// a word that crosses from one wprcpu_map block into another used to be read by the JIT's inline
/// page-table path straight off the end of the first block's host memory, fault inside JIT code,
/// and kill the process with dynarmic's "iter != fastmem_patch_info.end()" assertion. If that
/// regresses, this suite does not fail - the whole test process dies, which is just as loud.
/// </remarks>
public sealed class CpuMemorySuite : IContractSuite
{
    public string Name => "cpu memory and heap";

    // Free guest address space: above the TEB, below nothing anyone maps.
    private const long DataPage = 0xB0000000;
    private const long NextPage = DataPage + 0x1000;
    private const long CodePage = 0xB0010000;
    private const int ProtAll = 7;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        const int R0 = Harness.R0, R1 = Harness.R1, R2 = Harness.R2;

        // ---- an access straddling two separately mapped pages ----
        // Two maps, so two host allocations: the bytes after the first page's host memory are not
        // the second page's.
        e.Cpu.MemMap(DataPage, 0x1000, ProtAll);
        e.Cpu.MemMap(NextPage, 0x1000, ProtAll);
        e.Cpu.MemMap(CodePage, 0x1000, ProtAll);
        e.WriteMemory(DataPage + 0xFFE, [0x11, 0x22]);
        e.WriteMemory(NextPage, [0x33, 0x44]);

        // ldr r0, [r1] ; str r2, [r1, #0] ... then spin until the budget runs out.
        e.WriteMemory(CodePage, [0x08, 0x68, 0x0A, 0x60, 0xFE, 0xE7]);
        e.WriteRegister(R1, DataPage + 0xFFE);
        e.WriteRegister(R2, 0xDDCCBBAA);
        string? fault = e.Run(CodePage | 1, 200);

        h.Check("straddling run did not fault", fault is null, fault ?? "");
        h.Check("straddling word load assembles both pages", (e.ReadRegister(R0) & 0xFFFFFFFFL) == 0x44332211,
            $"r0=0x{e.ReadRegister(R0) & 0xFFFFFFFFL:X8}");
        byte[] stored = [.. e.ReadMemory(DataPage + 0xFFE, 2), .. e.ReadMemory(NextPage, 2)];
        h.Check("straddling word store lands on both pages", stored.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }),
            Convert.ToHexString(stored));

        // ---- the Win32 heap ----
        long heap = h.Call("GetProcessHeap");
        h.Check("GetProcessHeap returns a handle", heap != 0);
        long block = h.Call("HeapAlloc", heap, 0, 64);
        h.Check("HeapAlloc returns a block", block != 0);
        h.Check("HeapAlloc's block is zeroed", block != 0 && e.ReadMemory(block, 64).All(b => b == 0));
        h.Check("HeapSize reports at least the request", block != 0 && h.Call("HeapSize", heap, 0, block) >= 64);
        e.WriteMemory(block, [1, 2, 3, 4]);
        long grown = h.Call("HeapReAlloc", heap, 0, block, 256);
        h.Check("HeapReAlloc keeps the contents", grown != 0 && e.ReadMemory(grown, 4).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        h.Check("HeapFree succeeds", h.Call("HeapFree", heap, 0, grown) == 1);
    }
}
