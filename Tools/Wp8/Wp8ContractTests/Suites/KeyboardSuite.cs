using System.Collections;
using System.Reflection;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The CoreWindow keyboard (Keyboard.cs): add_/remove_ for KeyDown, KeyUp and CharacterReceived
/// (distinct tokens, a reference taken on add and released on remove, unknown tokens ignored, null
/// refused), delivery of an injected key to every registered handler with the CoreWindow as sender
/// and a KeyEventArgs carrying the VirtualKey, KeyStatus and Handled, GetKeyState/GetAsyncKeyState,
/// and that a removed handler is no longer called.
/// </summary>
public sealed class KeyboardSuite : IContractSuite
{
    public string Name => "keyboard";

    private const long SOk = 0;
    private const long EInvalidArg = 0x80070057;

    private Harness _h = null!;

    private ArmEmulator E => _h.E;

    private static readonly FieldInfo Traps = typeof(ArmEmulator).GetField("_traps", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo DeliverKeysMethod = typeof(WinRtRuntime).GetMethod("DeliverKeys", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static int Slot(string member) => WinRtSystemInterfaces.Slot("Windows.UI.Core.ICoreWindow", member);

    public void Run(Harness h)
    {
        _h = h;
        WinRtRuntime rt = E.WinRt;
        long window = (long)typeof(WinRtRuntime).GetField("_coreWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rt)!;

        h.Check("the keyboard slots agree with the WinRT metadata", rt.SlotDrift.Count == 0, string.Join("; ", rt.SlotDrift));

        // ---- add: a reference is taken, the token is distinct and non-zero ----
        var first = FakeDelegate(1);
        long firstToken = _h.Buffer(8);
        Invoke(window, Slot("add_KeyDown"), first.Handler, firstToken);
        var call = Called();
        h.Check("add_KeyDown AddRefs the handler before returning", call.Target == first.AddRef && call.Handler == first.Handler);
        h.Check("   and then answers S_OK", ReturnFromCall() == SOk);
        h.Check("   with a non-zero token", _h.U64(firstToken) != 0);

        var second = FakeDelegate(2, refCounted: false);
        long secondToken = _h.Buffer(8);
        h.Check("a second KeyDown handler is accepted too", Invoke(window, Slot("add_KeyDown"), second.Handler, secondToken) == SOk);
        h.Check("   with a token distinct from the first", _h.U64(secondToken) != 0 && _h.U64(secondToken) != _h.U64(firstToken));

        var up = FakeDelegate(3, refCounted: false);
        long upToken = _h.Buffer(8);
        h.Check("add_KeyUp accepts a handler", Invoke(window, Slot("add_KeyUp"), up.Handler, upToken) == SOk);
        h.Check("   with its own distinct token", _h.U64(upToken) != _h.U64(firstToken) && _h.U64(upToken) != _h.U64(secondToken));

        var chars = FakeDelegate(4, refCounted: false);
        long charToken = _h.Buffer(8);
        h.Check("add_CharacterReceived accepts a handler", Invoke(window, Slot("add_CharacterReceived"), chars.Handler, charToken) == SOk && _h.U64(charToken) != 0);

        h.Check("add_KeyDown with a null handler is E_INVALIDARG", Invoke(window, Slot("add_KeyDown"), 0, _h.Buffer(8)) == EInvalidArg);

        // ---- nothing queued: nothing raised ----
        h.Check("with nothing injected there is nothing to deliver", !Deliver(() => { }));

        // ---- KeyDown reaches every handler, in order, with the right args ----
        rt.InjectKey(0x41, down: true);   // VirtualKey.A
        bool continued = false;
        h.Check("an injected key is delivered", Deliver(() => continued = true));

        call = Called();
        h.Check("1. the first KeyDown handler is invoked", call.Target == first.Invoke && call.Handler == first.Handler);
        h.Check("   with the CoreWindow as sender", call.Sender == window);
        long args = call.Args;
        h.Check("   KeyEventArgs.VirtualKey is A (0x41)", Ret(args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_VirtualKey")) == 0x41);

        long status = _h.Buffer(12);
        Invoke(args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_KeyStatus"), status);
        h.Check("   KeyStatus: RepeatCount 1, ScanCode 0x1E (A)", _h.U32(status) == 1 && _h.U32(status + 4) == 0x1E, $"{_h.U32(status)} 0x{_h.U32(status + 4):X}");
        byte[] flags = E.ReadMemory(status + 8, 4);
        h.Check("   not extended, no Alt, WasKeyDown false, IsKeyReleased false", flags.All(b => b == 0), string.Join(",", flags));

        long down = _h.Buffer(4);
        Invoke(window, Slot("GetKeyState"), 0x41, down);
        h.Check("   GetKeyState(A) is Down while its KeyDown is raised", _h.U32(down) == 1);
        Invoke(window, Slot("GetAsyncKeyState"), 0x41, down);
        h.Check("   GetAsyncKeyState(A) is Down", _h.U32(down) == 1);

        long handled = QI(args, "Windows.UI.Core.ICoreWindowEventArgs");
        h.Check("   the args are ICoreWindowEventArgs; put_Handled(true) succeeds",
            handled != 0 && Invoke(handled, MemberSlot("Windows.UI.Core.ICoreWindowEventArgs", "put_Handled"), 1) == SOk);
        ReturnFromHandler();

        call = Called();
        h.Check("2. the second KeyDown handler is invoked next", call.Target == second.Invoke && call.Handler == second.Handler && call.Sender == window);
        h.Check("   with the same args, so it sees Handled", call.Args == args &&
            (Ret(QI(call.Args, "Windows.UI.Core.ICoreWindowEventArgs"), MemberSlot("Windows.UI.Core.ICoreWindowEventArgs", "get_Handled")) & 0xFF) == 1);
        ReturnFromHandler();
        h.Check("the main loop continues once every handler has run", continued);
        h.Check("the key-up handler was not raised for a key going down", rt.KeyEventsDelivered == 1);

        // ---- KeyUp, and a character, in one turn ----
        rt.InjectKey(0x41, down: false);
        rt.InjectCharacter('a');
        continued = false;
        h.Check("a key up and a character are delivered in the same turn", Deliver(() => continued = true));
        call = Called();
        h.Check("1. KeyUp is raised", call.Target == up.Invoke && call.Sender == window &&
            Ret(call.Args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_VirtualKey")) == 0x41);
        Invoke(call.Args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_KeyStatus"), status);
        h.Check("   KeyStatus: WasKeyDown and IsKeyReleased", E.ReadMemory(status + 10, 2).All(b => b == 1));
        Invoke(window, Slot("GetKeyState"), 0x41, down);
        h.Check("   GetKeyState(A) is None once it is up", _h.U32(down) == 0);
        ReturnFromHandler();
        call = Called();
        h.Check("2. CharacterReceived follows with KeyCode 'a'", call.Target == chars.Invoke &&
            Ret(call.Args, MemberSlot("Windows.UI.Core.ICharacterReceivedEventArgs", "get_KeyCode")) == 'a');
        ReturnFromHandler();
        h.Check("   and the loop continues", continued);

        // ---- extended keys and Alt ----
        rt.InjectKey(0x12, down: true, extended: true);   // right Alt
        rt.InjectKey(0x25, down: true);                   // Left arrow: always extended
        Deliver(() => { });
        call = Called();
        ReturnFromHandler();   // first handler, Menu
        ReturnFromHandler();   // second handler, Menu
        call = Called();
        Invoke(call.Args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_KeyStatus"), status);
        h.Check("Left arrow is reported extended, with Alt held and scan code 0x4B",
            E.ReadMemory(status + 8, 2).All(b => b == 1) && _h.U32(status + 4) == 0x4B, $"0x{_h.U32(status + 4):X}");
        ReturnFromHandler();
        ReturnFromHandler();
        rt.InjectKey(0x25, down: false);
        rt.InjectKey(0x12, down: false);
        Deliver(() => { });
        ReturnFromHandler();
        ReturnFromHandler();

        // ---- Caps Lock toggles the Locked state ----
        rt.InjectKey(0x14, down: true);
        rt.InjectKey(0x14, down: false);
        Deliver(() => { });
        ReturnFromHandler();
        ReturnFromHandler();
        ReturnFromHandler();
        Invoke(window, Slot("GetKeyState"), 0x14, down);
        h.Check("Caps Lock pressed and released is Locked, not Down", _h.U32(down) == 2);

        AutoRepeat(h, rt, window, first.Invoke, chars.Invoke);

        // ---- remove: released, and no longer called ----
        ulong token = _h.U64(firstToken);
        Invoke(window, Slot("remove_KeyDown"), 0, (long)(token & 0xFFFFFFFF), (long)(token >> 32));
        call = Called();
        h.Check("remove_KeyDown Releases the handler", call.Target == first.Release && call.Handler == first.Handler);
        h.Check("   and then answers S_OK", ReturnFromCall() == SOk);
        h.Check("removing the same token again is a harmless S_OK",
            Invoke(window, Slot("remove_KeyDown"), 0, (long)(token & 0xFFFFFFFF), (long)(token >> 32)) == SOk);
        h.Check("a KeyUp token does not remove a KeyDown handler",
            Invoke(window, Slot("remove_KeyDown"), 0, (long)(_h.U64(upToken) & 0xFFFFFFFF), (long)(_h.U64(upToken) >> 32)) == SOk);

        rt.InjectKey(0x20, down: true);   // Space
        h.Check("after removal a key is still delivered to the remaining handler", Deliver(() => { }));
        call = Called();
        h.Check("   which is the second handler: the removed one is skipped", call.Target == second.Invoke &&
            Ret(call.Args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_VirtualKey")) == 0x20);
        ReturnFromHandler();

        ulong second64 = _h.U64(secondToken);
        h.Check("remove_KeyDown of the last handler succeeds",
            Invoke(window, Slot("remove_KeyDown"), 0, (long)(second64 & 0xFFFFFFFF), (long)(second64 >> 32)) == SOk);
        rt.InjectKey(0x20, down: false);
        rt.InjectKey(0x20, down: true);
        bool ran = false;
        Deliver(() => { });   // KeyUp still has a handler
        ReturnFromHandler();
        rt.InjectKey(0x0D, down: true);
        h.Check("with no KeyDown handler left a key down raises nothing", !Deliver(() => ran = true) && !ran);

        ulong u = _h.U64(upToken), c = _h.U64(charToken);
        h.Check("remove_KeyUp and remove_CharacterReceived succeed",
            Invoke(window, Slot("remove_KeyUp"), 0, (long)(u & 0xFFFFFFFF), (long)(u >> 32)) == SOk &&
            Invoke(window, Slot("remove_CharacterReceived"), 0, (long)(c & 0xFFFFFFFF), (long)(c >> 32)) == SOk);
        rt.InjectKey(0x0D, down: false);
        rt.InjectCharacter('\r');
        h.Check("and then nothing is raised for them either", !Deliver(() => ran = true) && !ran);
        h.Check("the keyboard log records subscriptions and removals",
            rt.KeyboardLog.Count(l => l.Contains("unsubscribed", StringComparison.Ordinal)) == 4, string.Join(" | ", rt.KeyboardLog));

        // ---- the scan-code table ----
        h.Check("scan codes: Escape 0x01, Enter 0x1C, Space 0x39, Q 0x10, Z 0x2C, 0 0x0B, F12 0x58",
            Scan(0x1B) == 0x01 && Scan(0x0D) == 0x1C && Scan(0x20) == 0x39 && Scan('Q') == 0x10 && Scan('Z') == 0x2C && Scan('0') == 0x0B && Scan(0x7B) == 0x58);
    }

    /// <summary>
    /// Auto-repeat on a controlled clock: the delay, the interval, coalescing into RepeatCount,
    /// WasKeyDown on repeats, the last-pressed key taking the repeat over, release ending it for good,
    /// and characters marked as repeats only once the key has been held. Expects two KeyDown handlers,
    /// one KeyUp and one CharacterReceived, and leaves every key up.
    /// </summary>
    private void AutoRepeat(Harness h, WinRtRuntime rt, long window, long firstKeyDown, long characterHandler)
    {
        const uint B = 0x42, C = 0x43;
        Func<TimeSpan> savedClock = rt.KeyClock;
        (TimeSpan savedDelay, TimeSpan savedInterval) = (rt.KeyRepeatDelay, rt.KeyRepeatInterval);
        long ms = 0;
        rt.KeyClock = () => TimeSpan.FromMilliseconds(ms);
        rt.KeyRepeatDelay = TimeSpan.FromMilliseconds(500);
        rt.KeyRepeatInterval = TimeSpan.FromMilliseconds(50);
        long status = _h.Buffer(12);
        try
        {
            // One KeyDown event: both handlers, returning the status the first one saw.
            (uint Key, uint Repeat, bool WasDown, bool Released) KeyDown()
            {
                var call = Called();
                uint key = (uint)Ret(call.Args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_VirtualKey"));
                Invoke(call.Args, MemberSlot("Windows.UI.Core.IKeyEventArgs", "get_KeyStatus"), status);
                var seen = (key, (uint)_h.U32(status), E.ReadMemory(status + 10, 1)[0] == 1, E.ReadMemory(status + 11, 1)[0] == 1);
                bool firstHandler = call.Target == firstKeyDown;
                ReturnFromHandler();
                ReturnFromHandler();
                return firstHandler ? seen : default;
            }

            rt.InjectKey(B, down: true);
            Deliver(() => { });
            var press = KeyDown();
            h.Check("auto-repeat: the press itself is RepeatCount 1, WasKeyDown false", press is { Key: B, Repeat: 1, WasDown: false }, $"{press}");

            ms = 499;
            h.Check("   no repeat before the delay", rt.PumpKeyRepeat() == 0 && !Deliver(() => { }));

            ms = 500;
            h.Check("   at the delay one repeat is queued", rt.PumpKeyRepeat() == 1);
            bool continued = false;
            h.Check("   and delivered as a KeyDown", Deliver(() => continued = true));
            var repeat = KeyDown();
            h.Check("   for the held key, RepeatCount 1, WasKeyDown true, not released", repeat is { Key: B, Repeat: 1, WasDown: true, Released: false }, $"{repeat}");
            h.Check("   and the loop continues", continued);
            long state = _h.Buffer(4);
            Invoke(window, Slot("GetKeyState"), B, state);
            h.Check("   GetKeyState is still Down, not toggled", _h.U32(state) == 1);

            ms = 540;
            h.Check("   nothing between repeats", rt.PumpKeyRepeat() == 0 && !Deliver(() => { }));
            ms = 660;   // due at 550, 600, 650: three, coalesced
            h.Check("   repeats due since the last pump are coalesced", rt.PumpKeyRepeat() == 3);
            Deliver(() => { });
            repeat = KeyDown();
            h.Check("   into one KeyDown with RepeatCount 3", repeat is { Key: B, Repeat: 3, WasDown: true }, $"{repeat}");
            ms = 699;
            h.Check("   and the next is due on the original cadence (700)", rt.PumpKeyRepeat() == 0);

            // A character from the host's own repeat, once the key has been held.
            rt.InjectCharacter('b');
            Deliver(() => { });
            var call = Called();
            Invoke(call.Args, MemberSlot("Windows.UI.Core.ICharacterReceivedEventArgs", "get_KeyStatus"), status);
            h.Check("a character while the key repeats is CharacterReceived with WasKeyDown true",
                call.Target == characterHandler && E.ReadMemory(status + 10, 1)[0] == 1);
            ReturnFromHandler();

            // Another key takes the repeat over.
            ms = 700;
            rt.InjectKey(C, down: true);
            h.Check("pressing another key moves the repeat to it: nothing at once", rt.PumpKeyRepeat() == 0);
            Deliver(() => { });
            KeyDown();
            ms = 1199;
            h.Check("   B no longer repeats and C has its own delay", rt.PumpKeyRepeat() == 0);
            ms = 1200;
            h.Check("   then C repeats", rt.PumpKeyRepeat() == 1);
            Deliver(() => { });
            repeat = KeyDown();
            h.Check("   with C's VirtualKey", repeat is { Key: C, Repeat: 1, WasDown: true }, $"{repeat}");

            rt.InjectKey(C, down: false);
            Deliver(() => { });
            ReturnFromHandler();   // KeyUp
            ms = 3000;
            h.Check("releasing the repeating key ends it; B, still held, does not start again", rt.PumpKeyRepeat() == 0 && !Deliver(() => { }));

            rt.InjectKey(C, down: true);
            ms = 3010;
            rt.InjectCharacter('c');
            Deliver(() => { });
            KeyDown();
            call = Called();
            Invoke(call.Args, MemberSlot("Windows.UI.Core.ICharacterReceivedEventArgs", "get_KeyStatus"), status);
            h.Check("a character just after a press is not a repeat", call.Target == characterHandler && E.ReadMemory(status + 10, 1)[0] == 0);
            ReturnFromHandler();

            rt.InjectKey(C, down: false);
            rt.InjectKey(B, down: false);
            ms = 9000;
            h.Check("with every key up nothing repeats", rt.PumpKeyRepeat() == 0);
            Deliver(() => { });
            ReturnFromHandler();
            ReturnFromHandler();
        }
        finally
        {
            rt.KeyClock = savedClock;
            (rt.KeyRepeatDelay, rt.KeyRepeatInterval) = (savedDelay, savedInterval);
        }
    }

    private static uint Scan(uint vk) => (uint)typeof(WinRtRuntime).GetMethod("ScanCodeFor", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [vk])!;

    private static int MemberSlot(string iface, string member) => WinRtSystemInterfaces.Slot(iface, member);

    private bool Deliver(Action continueWith) => (bool)DeliverKeysMethod.Invoke(E.WinRt, [continueWith])!;

    /// <summary>
    /// A guest delegate whose Invoke (and, when <paramref name="refCounted"/>, AddRef and Release)
    /// is image code - never run here; r12 names whichever the runtime called.
    /// </summary>
    private (long Handler, long Invoke, long AddRef, long Release) FakeDelegate(int index, bool refCounted = true)
    {
        PeImage pe = PeImage.Load(_h.Image);
        long text = pe.ImageBase + pe.Sections.First(s => s.Name.StartsWith(".text", StringComparison.Ordinal)).VirtualAddress;
        long Code(int n) => (text + 0x10 + (n * 0x10)) | 1;
        long invoke = Code(index * 3);
        long addRef = refCounted ? Code((index * 3) + 1) : 0;
        long release = refCounted ? Code((index * 3) + 2) : 0;
        long vtable = _h.Buffer(16);
        E.WriteUInt32(vtable + 4, (uint)addRef);
        E.WriteUInt32(vtable + 8, (uint)release);
        E.WriteUInt32(vtable + 12, (uint)invoke);
        long handler = _h.Buffer(8);
        E.WriteUInt32(handler, (uint)vtable);
        return (handler, invoke, addRef, release);
    }

    /// <summary>The guest function the runtime just tail-called: (target, r0, r1, r2).</summary>
    private (long Target, long Handler, long Sender, long Args) Called()
    {
        _returnTrap = E.ReadRegister(10) & ~1L;
        return (E.ReadRegister(78), E.ReadRegister(Harness.R0) & 0xFFFFFFFFL, E.ReadRegister(Harness.R1) & 0xFFFFFFFFL, E.ReadRegister(Harness.R2) & 0xFFFFFFFFL);
    }

    /// <summary>The return trap set in lr for the guest function last called (calls made "inside" it overwrite lr).</summary>
    private long _returnTrap;

    /// <summary>The guest function returns 0: run the return trap the runtime set in lr.</summary>
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

    /// <summary>Returns from an AddRef/Release the runtime called, and answers what the stub then put in r0.</summary>
    private long ReturnFromCall()
    {
        ReturnFromHandler();
        return E.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
    }

    // Calling WinRT the way guest code does: through the vtable slot, with the trap's handler.

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
        E.WriteRegister(10, 0x1001);   // lr: somewhere to "return" to
        handler();
        return E.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
    }

    private long Ret(long self, int slot, params long[] words)
    {
        long result = _h.Buffer(8);
        return Invoke(self, slot, [.. words, result]) == SOk ? E.ReadUInt32(result) : 0;
    }

    private long QI(long self, string iface)
    {
        long iid = _h.Buffer(16);
        E.WriteMemory(iid, WinRtSystemInterfaces.Find(iface)!.Iid.ToByteArray());
        long result = _h.Buffer(8);
        return Invoke(self, 0, iid, result) == SOk ? E.ReadUInt32(result) : 0;
    }
}
