namespace WPR.Wp8Native;

/// <summary>How a guest access failed, for <see cref="IArmCpu.MemoryFault"/>.</summary>
public enum FaultKind
{
    ReadUnmapped,
    WriteUnmapped,
    FetchUnmapped,
    ReadProtected,
    WriteProtected,
    FetchProtected,
}

/// <summary>Why <see cref="IArmCpu.Run"/> came back.</summary>
public enum RunOutcome
{
    /// <summary>The instruction budget ran out.</summary>
    Budget,

    /// <summary>A hook or another thread asked for the run to end.</summary>
    Stopped,

    /// <summary>The CPU faulted and nothing handled it; <c>Fault</c> says how.</summary>
    Faulted,
}

public readonly record struct RunResult(RunOutcome Outcome, string? Fault);

/// <summary>A guest read or write the engine could not perform.</summary>
public sealed class CpuMemoryException(long address, string message) : Exception(message)
{
    public long Address { get; } = address;
}

/// <summary>
/// The CPU and its memory, as <see cref="ArmEmulator"/> needs them and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations: <see cref="UnicornArmCpu"/>, the research engine the probe was
/// written against, and <see cref="DynarmicArmCpu"/>, thirty times faster and under a licence
/// WPR can ship. Everything that means anything - the trap table, the allocator, the stubs,
/// the diagnostics - stays in <see cref="ArmEmulator"/>. What moves behind this seam is how a
/// trap is delivered, how a fault is reported, and which diagnostics the engine can offer.
/// </para>
/// <para>
/// Register identity is Unicorn's <c>UC_ARM_REG_*</c> numbering, deliberately. Forty call
/// sites across the stubs name registers that way and the numbers are as good a vocabulary as
/// any; the dynarmic implementation maps them.
/// </para>
/// <para>
/// Nothing may throw out of any callback. An engine calls these from inside its own dispatch
/// loop, and an exception there does not unwind to <see cref="Run"/> - it kills the process.
/// Implementations catch and <see cref="Stop"/>; callers should still not rely on that.
/// </para>
/// </remarks>
public interface IArmCpu : IDisposable
{
    /// <summary>What this engine can do beyond the essentials.</summary>
    CpuCapabilities Capabilities { get; }

    // ---- registers, in UC_ARM_REG_* numbering ----------------------------------------------
    long RegRead(int register);

    void RegWrite(int register, long value);

    // ---- VFP, for the hard-float calling convention ----------------------------------------
    /// <summary>
    /// Single-precision register <c>s{index}</c>, as raw bits. 0..63 covers all of d0..d31:
    /// <c>d{n}</c> is <c>s{2n}</c> (low word) and <c>s{2n+1}</c> (high word). Windows on ARM
    /// passes float and double arguments and results here, not in r0-r3.
    /// </summary>
    uint VfpRead(int index);

    void VfpWrite(int index, uint bits);

    // ---- guest memory, host side -----------------------------------------------------------
    /// <summary>Maps zero-filled pages. Throws if any page in the range is already mapped.</summary>
    void MemMap(long address, long size, int protection);

    void MemProtect(long address, long size, int protection);

    bool IsMapped(long address);

    /// <summary>Reads guest memory. Ignores protection. Throws <see cref="CpuMemoryException"/> if unmapped.</summary>
    void MemRead(long address, byte[] destination);

    /// <summary>Writes guest memory. Ignores protection, keeps translated code coherent.</summary>
    void MemWrite(long address, byte[] source);

    // ---- execution -------------------------------------------------------------------------
    /// <summary>
    /// Runs from <paramref name="startAddress"/> (bit 0 = Thumb) for at most
    /// <paramref name="instructionBudget"/> instructions. Not reentrant.
    /// </summary>
    RunResult Run(long startAddress, long instructionBudget);

    /// <summary>Ends the current run as soon as the engine can. Legal from callbacks and other threads.</summary>
    void Stop();

    // ---- traps: the only way emulated code reaches the host --------------------------------
    /// <summary>
    /// The bytes one 4-byte trap slot holds. The engine decides: <c>bx r12</c> for Unicorn,
    /// <c>svc #0 ; bx r12</c> for dynarmic. Either way the slot ends by branching to r12.
    /// </summary>
    ReadOnlySpan<byte> TrapSlot { get; }

    /// <summary>Declares the trap page so the engine can route entries into it.</summary>
    void InstallTrapPage(long baseAddress, long size);

    /// <summary>
    /// Fires when emulated code enters a trap slot, with the slot address, before the slot's
    /// branch to r12 executes. Registers are exact. The handler may write any register and
    /// r12 decides where execution continues - so a handler that does nothing returns to lr.
    /// </summary>
    event Action<long>? TrapEntered;

    // ---- faults ----------------------------------------------------------------------------
    /// <summary>
    /// A guest access the engine could not serve. Return true after mapping or permitting the
    /// page to have the access retried; false to fault the run. Registers are exact for a fetch
    /// and may be stale for a data access on engines without <see cref="CpuCapabilities.ExactRegistersInMemoryFaults"/>.
    /// </summary>
    Func<FaultKind, long, int, long, bool>? MemoryFault { get; set; }

    // ---- diagnostics the engine may or may not have ----------------------------------------
    /// <summary>Runs before every instruction in the range. Unsupported engines return false and do nothing.</summary>
    bool TryAddCodeHook(long start, long end, Action<long> onInstruction);

    /// <summary>Runs after every store into the range. Unsupported engines return false.</summary>
    bool TryAddWriteHook(long start, long end, Action<long, int, long> onWrite);

    /// <summary>Runs at every basic block. Unsupported engines return false.</summary>
    bool TryAddBlockHook(Action<long, int> onBlock);

    /// <summary>Instructions retired across all runs, where the engine counts them.</summary>
    long InstructionsRetired { get; }
}

public sealed record CpuCapabilities(
    string Name,
    bool CodeHooks,
    bool WriteHooks,
    bool BlockHooks,
    bool ExactRegistersInMemoryFaults,
    bool ExecuteProtection);
