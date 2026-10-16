using System.Collections;
using System.Reflection;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// CoreWindow pointer events: every handler subscribed to an event is raised, in subscription
/// order (Halo: Spartan Assault subscribes Pressed/Moved/Released twice, its Scaleform menus and its
/// game input); each add_ hands back its own token and remove_ drops just that handler; and
/// IPointerPointProperties.IsPrimary is the pointer that went down with nothing else in contact,
/// whatever its id (the desktop's synthetic finger is not id 1).
/// </summary>
public sealed class PointerSuite : IContractSuite
{
    public string Name => "pointer events";

    private const long SOk = 0;

    private Harness _h = null!;

    private ArmEmulator E => _h.E;

    private static readonly FieldInfo Traps = typeof(ArmEmulator).GetField("_traps", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo DeliverInputMethod = typeof(WinRtRuntime).GetMethod(
        "DeliverInput", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(Action), typeof(long)])!;

    private static int Slot(string member) => WinRtSystemInterfaces.Slot("Windows.UI.Core.ICoreWindow", member);

    private long _window;

    public void Run(Harness h)
    {
        _h = h;
        WinRtRuntime rt = E.WinRt;
        _window = (long)typeof(WinRtRuntime).GetField("_coreWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rt)!;

        var menus = FakeDelegate(11);
        var game = FakeDelegate(12);
        var released = FakeDelegate(13);
        long menusToken = _h.Buffer(8), gameToken = _h.Buffer(8), releasedToken = _h.Buffer(8);
        h.Check("add_PointerPressed accepts a first handler", Invoke(_window, Slot("add_PointerPressed"), menus.Handler, menusToken) == SOk);
        h.Check("   and a second", Invoke(_window, Slot("add_PointerPressed"), game.Handler, gameToken) == SOk);
        Invoke(_window, Slot("add_PointerReleased"), released.Handler, releasedToken);
        h.Check("   each with its own non-zero token", _h.U64(menusToken) != 0 && _h.U64(gameToken) != 0 && _h.U64(menusToken) != _h.U64(gameToken),
            $"0x{_h.U64(menusToken):X} 0x{_h.U64(gameToken):X}");

        // ---- a press reaches both handlers, in subscription order ----
        rt.InjectPointer(WinRtRuntime.PointerKind.Pressed, 300, 258, 0x7FFFFFFF);
        bool continued = false;
        h.Check("an injected press is delivered", Deliver(() => continued = true));
        var call = Called();
        h.Check("1. the first PointerPressed handler is raised", call.Target == menus.Invoke && call.Handler == menus.Handler && call.Sender == _window);
        h.Check("   IsPrimary for a lone finger, whatever its id (here the synthetic 0x7FFFFFFF)", IsPrimary(call.Args), _why);
        ReturnFromHandler();
        call = Called();
        h.Check("2. then the second", call.Target == game.Invoke && call.Handler == game.Handler);
        ReturnFromHandler();
        h.Check("   and the loop continues after both", continued);

        // ---- a second finger while the first is down is not primary ----
        rt.InjectPointer(WinRtRuntime.PointerKind.Pressed, 900, 500, 5);
        Deliver(() => { });
        call = Called();
        h.Check("a second finger down while the first is held is not primary", !IsPrimary(call.Args));
        ReturnFromHandler();
        ReturnFromHandler();

        // ---- both lift: the next lone finger is primary again ----
        rt.InjectPointer(WinRtRuntime.PointerKind.Released, 300, 258, 0x7FFFFFFF);
        Deliver(() => { });
        call = Called();
        h.Check("the primary's release is raised to the Released handler, still primary", call.Target == released.Invoke && IsPrimary(call.Args));
        ReturnFromHandler();
        rt.InjectPointer(WinRtRuntime.PointerKind.Released, 900, 500, 5);
        Deliver(() => { });
        ReturnFromHandler();

        // ---- remove_ drops just that handler ----
        long token = (long)_h.U64(menusToken);
        h.Check("remove_PointerPressed(first token) answers S_OK",
            Invoke(_window, Slot("remove_PointerPressed"), 0, token & 0xFFFFFFFF, token >>> 32) == SOk);
        rt.InjectPointer(WinRtRuntime.PointerKind.Pressed, 100, 100, 5);
        continued = false;
        Deliver(() => continued = true);
        call = Called();
        h.Check("after the remove only the remaining handler is raised", call.Target == game.Invoke);
        h.Check("   and a new lone finger is primary", IsPrimary(call.Args));
        ReturnFromHandler();
        h.Check("   and nothing after it", continued);
        rt.InjectPointer(WinRtRuntime.PointerKind.Released, 100, 100, 5);
        Deliver(() => { });
        ReturnFromHandler();
    }

    private bool IsPrimary(long args)
    {
        long point = Ret(args, WinRtSystemInterfaces.Slot("Windows.UI.Core.IPointerEventArgs", "get_CurrentPoint"));
        long properties = Ret(point, WinRtSystemInterfaces.Slot("Windows.UI.Input.IPointerPoint", "get_Properties"));
        long primary = Ret(properties, WinRtSystemInterfaces.Slot("Windows.UI.Input.IPointerPointProperties", "get_IsPrimary"));
        _why = $"point 0x{point:X} properties 0x{properties:X} IsPrimary 0x{primary:X}";
        return (primary & 0xFF) == 1;
    }

    private string _why = "";

    private bool Deliver(Action continueWith) => (bool)DeliverInputMethod.Invoke(E.WinRt, [continueWith, _window])!;

    /// <summary>A guest delegate whose Invoke is image code - never run here; r12 names what the runtime called.</summary>
    private (long Handler, long Invoke) FakeDelegate(int index)
    {
        PeImage pe = PeImage.Load(_h.Image);
        long text = pe.ImageBase + pe.Sections.First(s => s.Name.StartsWith(".text", StringComparison.Ordinal)).VirtualAddress;
        long invoke = (text + 0x10 + (index * 0x30)) | 1;
        long vtable = _h.Buffer(16);
        E.WriteUInt32(vtable + 12, (uint)invoke);
        long handler = _h.Buffer(8);
        E.WriteUInt32(handler, (uint)vtable);
        return (handler, invoke);
    }

    private long _returnTrap;

    private (long Target, long Handler, long Sender, long Args) Called()
    {
        _returnTrap = E.ReadRegister(10) & ~1L;
        return (E.ReadRegister(78), E.ReadRegister(Harness.R0) & 0xFFFFFFFFL, E.ReadRegister(Harness.R1) & 0xFFFFFFFFL, E.ReadRegister(Harness.R2) & 0xFFFFFFFFL);
    }

    private void ReturnFromHandler()
    {
        long lr = _returnTrap;
        _returnTrap = 0;
        if (lr == 0)
        {
            Called();
            lr = _returnTrap;
        }

        object trap = ((IDictionary)Traps.GetValue(E)!)[lr] ?? throw new InvalidOperationException($"lr 0x{lr:X} is not a return trap");
        Action handler = (Action)trap.GetType().GetProperty("Handler")!.GetValue(trap)!;
        E.WriteRegister(Harness.R0, 0);
        handler();
    }

    private long Invoke(long self, int slot, params long[] words)
    {
        long function = E.ReadUInt32(E.ReadUInt32(self) + (slot * 4)) & ~1L;
        object trap = ((IDictionary)Traps.GetValue(E)!)[function] ?? throw new InvalidOperationException($"slot {slot} of 0x{self:X} is not a trap");
        Action handler = (Action)trap.GetType().GetProperty("Handler")!.GetValue(trap)!;

        int[] registers = [Harness.R0, Harness.R1, Harness.R2, Harness.R3];
        E.WriteRegister(Harness.R0, self);
        for (int i = 0; i < words.Length && i < 3; i++)
        {
            E.WriteRegister(registers[i + 1], words[i]);
        }

        for (int i = 3; i < words.Length; i++)
        {
            E.WriteUInt32(_h.Stack + ((i - 3) * 4), (uint)words[i]);
        }

        E.WriteRegister(Harness.SP, _h.Stack);
        E.WriteRegister(10, 0x1001);
        handler();
        return E.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
    }

    private long Ret(long self, int slot, params long[] words)
    {
        // On the stack: the pointer properties only write an out-parameter that is a stack address.
        long result = 0x50000000 + 0x2000;   // the guest main stack's deep end, never reached here
        E.WriteUInt32(result, 0);
        return Invoke(self, slot, [.. words, result]) == SOk ? E.ReadUInt32(result) : 0;
    }
}
