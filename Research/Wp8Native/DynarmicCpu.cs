using System.Runtime.InteropServices;

namespace WPR.Wp8Native;

/// <summary>
/// A dynarmic CPU, owned from C#. Guest memory, registers, traps and the run loop - the same
/// job Unicorn does for <see cref="ArmEmulator"/>, at thirty times the speed and under a
/// licence WPR can ship.
/// </summary>
/// <remarks>
/// Traps are <c>svc #0 ; bx r12</c>. dynarmic ends a block at the <c>svc</c> and calls
/// <see cref="TrapEntered"/> with the register file exact; the handler sets whatever it
/// likes and returns; the <c>bx r12</c> then performs the tail call exactly as it does under
/// Unicorn. No PC is written from inside a callback, which is the thing Unicorn's build could
/// not survive.
///
/// Memory the JIT may touch without asking is RWX and unwatched. Everything else - the trap
/// page (read+execute only, so a stray store into it is a fault, not a corrupted slot), the
/// null page (read as zero, never executed) and pages nobody has mapped - is answered by
/// <see cref="UnmappedAccess"/>, which may map and retry or decline and fault.
/// </remarks>
public sealed unsafe class DynarmicCpu : IDisposable
{
    private IntPtr _handle;
    private readonly DynarmicNative.Callbacks* _callbacks;

    // Pinned for the life of the CPU: the native side holds raw function pointers to them.
    private readonly DynarmicNative.SvcCallback _onSvc;
    private readonly DynarmicNative.UnmappedCallback _onUnmapped;
    private readonly DynarmicNative.ExceptionCallback _onException;

    private readonly uint* _regs;
    private readonly uint* _extRegs;

    public const uint PageSize = 0x1000;

    public DynarmicCpu()
    {
        _onSvc = HandleSvc;
        _onUnmapped = HandleUnmapped;
        _onException = HandleException;

        _callbacks = (DynarmicNative.Callbacks*)NativeMemory.AllocZeroed((nuint)sizeof(DynarmicNative.Callbacks));
        _callbacks->User = IntPtr.Zero;
        _callbacks->OnSvc = Marshal.GetFunctionPointerForDelegate(_onSvc);
        _callbacks->OnUnmapped = Marshal.GetFunctionPointerForDelegate(_onUnmapped);
        _callbacks->OnException = Marshal.GetFunctionPointerForDelegate(_onException);

        _handle = DynarmicNative.wprcpu_create(_callbacks);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("wprcpu_create failed");
        }

        _regs = DynarmicNative.wprcpu_regs(_handle);
        _extRegs = DynarmicNative.wprcpu_extregs(_handle);
    }

    /// <summary>The instruction after an <c>svc</c> - so the slot is <c>pc - 2</c>.</summary>
    public event Action<uint>? TrapEntered;

    /// <summary>
    /// Access to memory the JIT cannot serve. Return true after mapping or permitting it to
    /// have the access retried; false to fault the run.
    /// </summary>
    public Func<int, uint, int, bool>? UnmappedAccess { get; set; }

    /// <summary>Undefined instruction, decode error, interpreter fallback (kind -1).</summary>
    public Action<uint, int>? ExceptionRaised { get; set; }

    public bool Map(uint address, uint size, int prot) => DynarmicNative.wprcpu_map(_handle, address, size, prot) != 0;

    public bool Protect(uint address, uint size, int prot) => DynarmicNative.wprcpu_protect(_handle, address, size, prot) != 0;

    public bool IsMapped(uint address) => DynarmicNative.wprcpu_is_mapped(_handle, address) != 0;

    public bool TryRead(uint address, Span<byte> destination)
    {
        fixed (byte* p = destination)
        {
            return DynarmicNative.wprcpu_read(_handle, address, p, (uint)destination.Length) != 0;
        }
    }

    public bool TryWrite(uint address, ReadOnlySpan<byte> source)
    {
        fixed (byte* p = source)
        {
            return DynarmicNative.wprcpu_write(_handle, address, p, (uint)source.Length) != 0;
        }
    }

    public void Invalidate(uint address, uint size) => DynarmicNative.wprcpu_invalidate(_handle, address, size);

    /// <summary>r0-r15, live.</summary>
    public uint this[int register]
    {
        get => _regs[register];
        set => _regs[register] = value;
    }

    /// <summary>s0-s63 as words, live.</summary>
    public uint ExtReg(int index) => _extRegs[index];

    public void SetExtReg(int index, uint value) => _extRegs[index] = value;

    public uint Cpsr
    {
        get => DynarmicNative.wprcpu_get_cpsr(_handle);
        set => DynarmicNative.wprcpu_set_cpsr(_handle, value);
    }

    public uint Fpscr
    {
        get => DynarmicNative.wprcpu_get_fpscr(_handle);
        set => DynarmicNative.wprcpu_set_fpscr(_handle, value);
    }

    public void SetThreadPointers(uint tpidruro, uint tpidrurw) =>
        DynarmicNative.wprcpu_set_thread_pointers(_handle, tpidruro, tpidrurw);

    /// <summary>
    /// Runs from the current PC for at most <paramref name="budget"/> instructions.
    /// </summary>
    /// <remarks>
    /// Bit 0 of <paramref name="startAddress"/> selects Thumb, as it does for Unicorn - the T
    /// bit is set in CPSR here and an even PC is handed to the JIT.
    /// </remarks>
    public int Run(uint startAddress, ulong budget)
    {
        uint cpsr = Cpsr;
        Cpsr = (startAddress & 1) != 0 ? cpsr | 0x20u : cpsr & ~0x20u;
        _regs[15] = startAddress & ~1u;
        return DynarmicNative.wprcpu_run(_handle, budget);
    }

    /// <summary>Continues from the current PC and CPSR.</summary>
    public int Continue(ulong budget) => DynarmicNative.wprcpu_run(_handle, budget);

    public void Halt() => DynarmicNative.wprcpu_halt(_handle);

    public void Stop(string message) => DynarmicNative.wprcpu_stop(_handle, message);

    public string? StopMessage
    {
        get
        {
            IntPtr p = DynarmicNative.wprcpu_stop_message(_handle);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
        }
    }

    public ulong InstructionsRetired => DynarmicNative.wprcpu_instructions_retired(_handle);

    /// <summary>The bytes of one trap slot: <c>svc #0</c> then <c>bx r12</c>.</summary>
    public static ReadOnlySpan<byte> TrapSlot => [0x00, 0xDF, 0x60, 0x47];

    private void HandleSvc(IntPtr user, uint pc, uint swi)
    {
        try
        {
            TrapEntered?.Invoke(pc);
        }
        catch (Exception ex)
        {
            // Nothing may escape into the JIT. End the run with the reason instead.
            Stop($"trap handler threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private int HandleUnmapped(IntPtr user, int access, uint address, int size)
    {
        try
        {
            return UnmappedAccess?.Invoke(access, address, size) == true ? 1 : 0;
        }
        catch (Exception ex)
        {
            Stop($"unmapped-access handler threw {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    private void HandleException(IntPtr user, uint pc, int kind)
    {
        try
        {
            ExceptionRaised?.Invoke(pc, kind);
        }
        catch
        {
            // The run is ending anyway.
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            DynarmicNative.wprcpu_destroy(_handle);
            _handle = IntPtr.Zero;
        }

        NativeMemory.Free(_callbacks);
        GC.KeepAlive(_onSvc);
        GC.KeepAlive(_onUnmapped);
        GC.KeepAlive(_onException);
    }
}
