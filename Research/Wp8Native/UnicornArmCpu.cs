using UnicornEngine;
using UnicornEngine.Const;

namespace WPR.Wp8Native;

/// <summary>
/// <see cref="IArmCpu"/> over Unicorn 2.1.3 - the engine the probe was written against.
/// </summary>
/// <remarks>
/// Traps are a code hook over the trap page and a <c>bx r12</c> in every slot. The hook fires
/// before the <c>bx</c>, which is exactly the contract: the handler writes registers, the
/// slot branches to r12. Every hook here is installed once and never removed - the binding
/// exposes no way to remove one - so a hook handler checks whether it is still wanted.
///
/// GPLv2, so this is the research harness and never ships; see the plan.
/// </remarks>
public sealed class UnicornArmCpu : IArmCpu
{
    private readonly Unicorn _uc;

    // Delegates the binding keeps raw pointers to. Fields so the GC cannot collect them.
    private readonly CodeHook _trapHook;
    private readonly EventMemHook _faultHook;
    private readonly List<Delegate> _keepAlive = new();

    private readonly Dictionary<long, string> _stopHooks = new();
    private readonly CodeHook _stopHook;
    private long _stopAt = -1;
    private string? _stopReason;

    public UnicornArmCpu()
    {
        _uc = new Unicorn(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
        _trapHook = OnTrap;
        _faultHook = OnFault;
        _stopHook = OnStopPoint;
        _uc.AddEventMemHook(_faultHook, Common.UC_HOOK_MEM_UNMAPPED | Common.UC_HOOK_MEM_PROT, null);
    }

    public CpuCapabilities Capabilities { get; } = new(
        Name: "Unicorn 2.1.3",
        CodeHooks: true,
        WriteHooks: true,
        BlockHooks: true,
        ExactRegistersInMemoryFaults: true,
        ExecuteProtection: true);

    public long RegRead(int register) => _uc.RegRead(register);

    public void RegWrite(int register, long value) => _uc.RegWrite(register, value);

    public void MemMap(long address, long size, int protection) => _uc.MemMap(address, size, protection);

    public void MemProtect(long address, long size, int protection) => _uc.MemProtect(address, size, protection);

    public bool IsMapped(long address)
    {
        try
        {
            _uc.MemRead(address, new byte[1]);
            return true;
        }
        catch (UnicornEngineException)
        {
            return false;
        }
    }

    public void MemRead(long address, byte[] destination)
    {
        try
        {
            _uc.MemRead(address, destination);
        }
        catch (UnicornEngineException ex)
        {
            throw new CpuMemoryException(address, ex.Message);
        }
    }

    public void MemWrite(long address, byte[] source)
    {
        try
        {
            _uc.MemWrite(address, source);
        }
        catch (UnicornEngineException ex)
        {
            throw new CpuMemoryException(address, ex.Message);
        }
    }

    public RunResult Run(long startAddress, long instructionBudget)
    {
        _stopReason = null;
        try
        {
            // Unicorn's own `until` parameter is not used: passing a real address to
            // uc_emu_start crashes the MinGW Windows build outright. -1 is never reached.
            _uc.EmuStart(startAddress, -1, 0, instructionBudget);
        }
        catch (UnicornEngineException ex)
        {
            return new RunResult(RunOutcome.Faulted, ex.Message);
        }

        return _stopReason is not null
            ? new RunResult(RunOutcome.Stopped, null)
            : new RunResult(RunOutcome.Budget, null);
    }

    public void Stop()
    {
        _stopReason ??= "stopped";
        _uc.EmuStop();
    }

    /// <summary>Thumb <c>bx r12</c>, then a padding halfword that is never executed.</summary>
    public ReadOnlySpan<byte> TrapSlot => [0x60, 0x47, 0x00, 0xBF];

    public void InstallTrapPage(long baseAddress, long size)
    {
        _uc.AddCodeHook(_trapHook, null, baseAddress, baseAddress + size);
    }

    public event Action<long>? TrapEntered;

    public Func<FaultKind, long, int, long, bool>? MemoryFault { get; set; }

    public bool TryAddCodeHook(long start, long end, Action<long> onInstruction)
    {
        CodeHook hook = (Unicorn _, long address, int _, object? _) => onInstruction(address);
        _keepAlive.Add(hook);
        _uc.AddCodeHook(hook, null, start, end);
        return true;
    }

    public bool TryAddWriteHook(long start, long end, Action<long, int, long> onWrite)
    {
        MemWriteHook hook = (Unicorn _, long address, int size, long value, object? _) => onWrite(address, size, value);
        _keepAlive.Add(hook);
        _uc.AddMemWriteHook(hook, null, start, end);
        return true;
    }

    public bool TryAddBlockHook(Action<long, int> onBlock)
    {
        BlockHook hook = (Unicorn _, long address, int size, object? _) => onBlock(address, size);
        _keepAlive.Add(hook);
        _uc.AddBlockHook(hook, null, 1, long.MaxValue);
        return true;
    }

    /// <summary>Unicorn does not count; the budget is the only figure it has.</summary>
    public long InstructionsRetired => 0;

    private void OnTrap(Unicorn uc, long address, int size, object? userData)
    {
        TrapEntered?.Invoke(address);
    }

    private bool OnFault(Unicorn uc, int eventType, long address, int size, long value, object? userData)
    {
        FaultKind kind =
            eventType == Common.UC_MEM_READ_UNMAPPED ? FaultKind.ReadUnmapped :
            eventType == Common.UC_MEM_WRITE_UNMAPPED ? FaultKind.WriteUnmapped :
            eventType == Common.UC_MEM_FETCH_UNMAPPED ? FaultKind.FetchUnmapped :
            eventType == Common.UC_MEM_READ_PROT ? FaultKind.ReadProtected :
            eventType == Common.UC_MEM_WRITE_PROT ? FaultKind.WriteProtected :
            FaultKind.FetchProtected;

        return MemoryFault?.Invoke(kind, address, size, value) ?? false;
    }

    private void OnStopPoint(Unicorn uc, long address, int size, object? userData)
    {
        if (address == _stopAt)
        {
            Stop();
        }
    }

    public void Dispose()
    {
        _uc.Dispose();
    }
}
