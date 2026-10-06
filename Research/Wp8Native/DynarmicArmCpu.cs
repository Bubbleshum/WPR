using UnicornEngine.Const;

namespace WPR.Wp8Native;

/// <summary>
/// <see cref="IArmCpu"/> over dynarmic through <see cref="DynarmicCpu"/>. The engine WPR can
/// ship, and the fast one.
/// </summary>
/// <remarks>
/// <para>
/// Traps are <c>svc #0 ; bx r12</c>: the JIT ends the block at the <c>svc</c>, the handler
/// runs with exact registers, and the <c>bx r12</c> does the tail call. No PC is written from a
/// callback.
/// </para>
/// <para>
/// Register numbering is Unicorn's, translated here. FPEXC and CPACR writes are accepted and
/// ignored - VFP is always on under dynarmic and there is nothing to enable. CP15 c13 is the
/// thread pointer, which is real here for the first time.
/// </para>
/// <para>
/// No per-instruction, per-store or per-block hooks: a JIT has no place to put them. Callers
/// check <see cref="Capabilities"/> and degrade - the block sampler reports "not counted", trace
/// points are refused, and the heap-execution guard becomes execute protection, which is what it
/// always should have been.
/// </para>
/// </remarks>
public sealed class DynarmicArmCpu : IArmCpu
{
    private readonly DynarmicCpu _cpu = new();
    private uint _tpidruro;
    private uint _tpidrurw;
    private bool _stopped;

    public DynarmicArmCpu()
    {
        _cpu.TrapEntered += pc => TrapEntered?.Invoke(pc - 2);
        _cpu.UnmappedAccess = OnUnmapped;
        _cpu.ExceptionRaised = (pc, kind) => _lastFault ??= kind == -1
            ? $"interpreter fallback at 0x{pc:X8}"
            : $"CPU exception {kind} at 0x{pc:X8}";
    }

    private string? _lastFault;

    public CpuCapabilities Capabilities { get; } = new(
        Name: "dynarmic",
        CodeHooks: false,
        WriteHooks: false,
        BlockHooks: false,
        ExactRegistersInMemoryFaults: false,
        ExecuteProtection: true);

    public long RegRead(int register)
    {
        if (register >= Arm.UC_ARM_REG_R0 && register <= Arm.UC_ARM_REG_R12)
        {
            return _cpu[register - Arm.UC_ARM_REG_R0];
        }

        if (register == Arm.UC_ARM_REG_SP) return _cpu[13];
        if (register == Arm.UC_ARM_REG_LR) return _cpu[14];
        if (register == Arm.UC_ARM_REG_PC) return _cpu[15];
        if (register == Arm.UC_ARM_REG_CPSR) return _cpu.Cpsr;
        if (register == Arm.UC_ARM_REG_FPSCR) return _cpu.Fpscr;
        if (register == Arm.UC_ARM_REG_FPEXC) return 0x40000000L;
        if (register == Arm.UC_ARM_REG_C1_C0_2) return 0xF00000L;
        if (register == Arm.UC_ARM_REG_C13_C0_3) return _tpidruro;
        if (register == Arm.UC_ARM_REG_C13_C0_2) return _tpidrurw;
        throw new ArgumentOutOfRangeException(nameof(register), register, "no dynarmic mapping");
    }

    public void RegWrite(int register, long value)
    {
        uint word = (uint)value;
        if (register >= Arm.UC_ARM_REG_R0 && register <= Arm.UC_ARM_REG_R12)
        {
            _cpu[register - Arm.UC_ARM_REG_R0] = word;
        }
        else if (register == Arm.UC_ARM_REG_SP) _cpu[13] = word;
        else if (register == Arm.UC_ARM_REG_LR) _cpu[14] = word;
        else if (register == Arm.UC_ARM_REG_PC) _cpu[15] = word;
        else if (register == Arm.UC_ARM_REG_CPSR) _cpu.Cpsr = word;
        else if (register == Arm.UC_ARM_REG_FPSCR) _cpu.Fpscr = word;
        else if (register == Arm.UC_ARM_REG_FPEXC || register == Arm.UC_ARM_REG_C1_C0_2)
        {
            // VFP is always enabled here; there is nothing to switch on.
        }
        else if (register == Arm.UC_ARM_REG_C13_C0_3)
        {
            _tpidruro = word;
            _cpu.SetThreadPointers(_tpidruro, _tpidrurw);
        }
        else if (register == Arm.UC_ARM_REG_C13_C0_2)
        {
            _tpidrurw = word;
            _cpu.SetThreadPointers(_tpidruro, _tpidrurw);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(register), register, "no dynarmic mapping");
        }
    }

    public uint VfpRead(int index) => _cpu.ExtReg(index);

    public void VfpWrite(int index, uint bits) => _cpu.SetExtReg(index, bits);

    public void MemMap(long address, long size, int protection)
    {
        // Unicorn and the shim agree on READ=1 WRITE=2 EXEC=4.
        if (!_cpu.Map((uint)address, (uint)size, protection))
        {
            throw new CpuMemoryException(address, $"cannot map 0x{address:X8}+0x{size:X}: overlapping or unaligned");
        }
    }

    public void MemProtect(long address, long size, int protection)
    {
        if (!_cpu.Protect((uint)address, (uint)size, protection))
        {
            throw new CpuMemoryException(address, $"cannot protect 0x{address:X8}+0x{size:X}: not mapped");
        }
    }

    public bool IsMapped(long address) => _cpu.IsMapped((uint)address);

    public void MemRead(long address, byte[] destination)
    {
        if (!_cpu.TryRead((uint)address, destination))
        {
            throw new CpuMemoryException(address, $"read of {destination.Length} bytes at 0x{address:X8} touches unmapped memory");
        }
    }

    public void MemWrite(long address, byte[] source)
    {
        if (!_cpu.TryWrite((uint)address, source))
        {
            throw new CpuMemoryException(address, $"write of {source.Length} bytes at 0x{address:X8} touches unmapped memory");
        }
    }

    public RunResult Run(long startAddress, long instructionBudget)
    {
        _stopped = false;
        _lastFault = null;
        int outcome = _cpu.Run((uint)startAddress, (ulong)instructionBudget);
        return outcome switch
        {
            DynarmicNative.OutcomeBudget => new RunResult(RunOutcome.Budget, null),
            DynarmicNative.OutcomeHalted => new RunResult(RunOutcome.Stopped, null),
            DynarmicNative.OutcomeStopped => new RunResult(RunOutcome.Stopped, null),
            _ => new RunResult(RunOutcome.Faulted, _lastFault ?? "the CPU faulted"),
        };
    }

    public void Stop()
    {
        _stopped = true;
        _cpu.Halt();
    }

    public ReadOnlySpan<byte> TrapSlot => DynarmicCpu.TrapSlot;

    public void InstallTrapPage(long baseAddress, long size)
    {
        // Nothing to install: the slots raise svc, and svc is routed unconditionally.
    }

    public event Action<long>? TrapEntered;

    public Func<FaultKind, long, int, long, bool>? MemoryFault { get; set; }

    public bool TryAddCodeHook(long start, long end, Action<long> onInstruction) => false;

    public bool TryAddWriteHook(long start, long end, Action<long, int, long> onWrite) => false;

    public bool TryAddBlockHook(Action<long, int> onBlock) => false;

    public long InstructionsRetired => (long)_cpu.InstructionsRetired;

    private bool OnUnmapped(int access, uint address, int size)
    {
        bool mapped = _cpu.IsMapped(address);
        FaultKind kind = access switch
        {
            DynarmicNative.AccessRead => mapped ? FaultKind.ReadProtected : FaultKind.ReadUnmapped,
            DynarmicNative.AccessWrite => mapped ? FaultKind.WriteProtected : FaultKind.WriteUnmapped,
            _ => mapped ? FaultKind.FetchProtected : FaultKind.FetchUnmapped,
        };

        // The value being stored is not available from the shim; nothing in the emulator reads it
        // except the trap-page write diagnostic, which only needs the address.
        return MemoryFault?.Invoke(kind, address, size, 0) ?? false;
    }

    public void Dispose() => _cpu.Dispose();
}
