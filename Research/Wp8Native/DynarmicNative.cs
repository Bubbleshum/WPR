using System.Runtime.InteropServices;

namespace WPR.Wp8Native;

/// <summary>
/// P/Invoke surface of <c>wprcpu.dll</c>, the C shim over dynarmic. One-to-one with
/// <c>wprcpu.h</c>; nothing here has an opinion.
/// </summary>
/// <remarks>
/// The DLL is cross-compiled from WSL with mingw-w64 and statically linked - it imports only
/// <c>KERNEL32</c> and <c>msvcrt</c> - so it is dropped next to the executable like
/// <c>unicorn.dll</c> is, and like <c>unicorn.dll</c> it is gitignored. Unlike Unicorn it is
/// 0BSD, which is the point.
/// </remarks>
internal static unsafe class DynarmicNative
{
    private const string Library = "wprcpu";

    public const int ProtNone = 0;
    public const int ProtRead = 1;
    public const int ProtWrite = 2;
    public const int ProtExec = 4;
    public const int ProtAll = 7;

    public const int AccessRead = 0;
    public const int AccessWrite = 1;
    public const int AccessFetch = 2;

    public const int OutcomeBudget = 0;
    public const int OutcomeHalted = 1;
    public const int OutcomeFault = 2;
    public const int OutcomeStopped = 3;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SvcCallback(IntPtr user, uint pc, uint swi);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int UnmappedCallback(IntPtr user, int access, uint address, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ExceptionCallback(IntPtr user, uint pc, int kind);

    /// <summary>Mirror of <c>wprcpu_callbacks</c>. Function pointers, not delegates, so the
    /// layout is exactly four pointers.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks
    {
        public IntPtr User;
        public IntPtr OnSvc;
        public IntPtr OnUnmapped;
        public IntPtr OnException;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int wprcpu_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint wprcpu_last_fetch();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong wprcpu_fetch_count();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr wprcpu_create(Callbacks* callbacks);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void wprcpu_destroy(IntPtr cpu);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int wprcpu_map(IntPtr cpu, uint address, uint size, int prot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int wprcpu_protect(IntPtr cpu, uint address, uint size, int prot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int wprcpu_is_mapped(IntPtr cpu, uint address);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int wprcpu_read(IntPtr cpu, uint address, byte* destination, uint size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int wprcpu_write(IntPtr cpu, uint address, byte* source, uint size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void wprcpu_invalidate(IntPtr cpu, uint address, uint size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint* wprcpu_regs(IntPtr cpu);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint* wprcpu_extregs(IntPtr cpu);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint wprcpu_get_cpsr(IntPtr cpu);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void wprcpu_set_cpsr(IntPtr cpu, uint value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint wprcpu_get_fpscr(IntPtr cpu);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void wprcpu_set_fpscr(IntPtr cpu, uint value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void wprcpu_set_thread_pointers(IntPtr cpu, uint tpidruro, uint tpidrurw);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int wprcpu_run(IntPtr cpu, ulong budget);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void wprcpu_halt(IntPtr cpu);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong wprcpu_instructions_retired(IntPtr cpu);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void wprcpu_stop(IntPtr cpu, [MarshalAs(UnmanagedType.LPStr)] string message);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr wprcpu_stop_message(IntPtr cpu);
}
