using System.Collections.Concurrent;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The keyboard as a WP8 title sees it: <c>CoreWindow.KeyDown</c>, <c>KeyUp</c> and
    /// <c>CharacterReceived</c>, and the <c>GetKeyState</c>/<c>GetAsyncKeyState</c> queries.
    /// </summary>
    /// <remarks>
    /// <para>A phone had no keyboard, so few titles subscribe; Angry Birds Go! does, and until this
    /// the subscription was accepted by the discovery default with a zero token and never raised.
    /// What reaches a title here is a desktop keyboard (or a physical one on Android) through
    /// <c>Keyboard.GetState()</c> edges and <c>TextInputEXT</c>, injected by the host
    /// (<c>Wp8NativeGame</c>).</para>
    /// <para><b>Contract.</b> Each <c>add_</c> keeps every handler (not one per event, as the
    /// discovery default did), answers a distinct non-zero <c>EventRegistrationToken</c> and takes a
    /// reference on the delegate, because the caller releases its own as soon as the add returns.
    /// <c>remove_</c> with that token drops the handler and releases that reference; an unknown
    /// token is a no-op answering S_OK, as WinRT's event sources do. A null handler is
    /// <c>E_INVALIDARG</c>.</para>
    /// <para><b>Delivery</b> is on the guest's thread, at the next turn round its main loop - an
    /// exe title's <c>ProcessEvents</c>, a XAML component's frame - like pointer input, and every
    /// key event queued since the last turn goes out in that turn, in order. Each handler gets the
    /// CoreWindow as sender and one shared <c>KeyEventArgs</c> per event (so a <c>Handled</c> set by
    /// one handler is seen by the next). A handler that a previous handler removed during the same
    /// event is skipped rather than called: its reference has been released, so calling it would be
    /// a use-after-free, where WinRT's own snapshot would still hold one.</para>
    /// <para><b>Auto-repeat</b> is modelled here, because the host only sees key state once a frame.
    /// As on Windows, the key pressed last repeats: after <see cref="KeyRepeatDelay"/> it raises
    /// <c>KeyDown</c> again every <see cref="KeyRepeatInterval"/>, with <c>WasKeyDown</c> true. Pressing
    /// another key moves the repeat to it, and releasing the repeating key stops it (a key still held
    /// from before does not start again). Repeats come from <see cref="PumpKeyRepeat"/>, which the host
    /// calls once a frame; repeats that fell due between two calls are one <c>KeyDown</c> whose
    /// <c>RepeatCount</c> says how many, as Windows coalesces them for a slow app. GetKeyState and the
    /// lock keys are unchanged by a repeat. <c>CharacterReceived</c> is not synthesised: the host's text
    /// input repeats on its own (SDL, at the same platform rate), so a character that arrives once the
    /// repeating key has been held for half the delay is marked a repeat (<c>WasKeyDown</c> true).</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        /// <summary>ICoreWindow slots, by the documented member order (verified in the constructor).</summary>
        private const int SlotGetAsyncKeyState = InspectableSlots + 14;
        private const int SlotGetKeyState = InspectableSlots + 15;
        private const int SlotAddCharacterReceived = InspectableSlots + 22;
        private const int SlotRemoveCharacterReceived = InspectableSlots + 23;
        private const int SlotAddKeyDown = InspectableSlots + 28;
        private const int SlotRemoveKeyDown = InspectableSlots + 29;
        private const int SlotAddKeyUp = InspectableSlots + 30;
        private const int SlotRemoveKeyUp = InspectableSlots + 31;

        /// <summary>IUnknown::Release.</summary>
        private const int SlotRelease = 2;

        /// <summary>Windows.System.VirtualKey values this file needs by name.</summary>
        private const uint VirtualKeyMenu = 0x12, VirtualKeyCapitalLock = 0x14, VirtualKeyNumberKeyLock = 0x90, VirtualKeyScroll = 0x91;

        /// <summary>Windows.UI.Core.CoreVirtualKeyStates.</summary>
        private const uint KeyStateNone = 0, KeyStateDown = 1, KeyStateLocked = 2;

        private const int HResultInvalidArg = unchecked((int)0x80070057); // E_INVALIDARG

        /// <summary>What the host queued: a key going down or up, or a character typed.</summary>
        private enum KeyEventKind
        {
            Down,
            Up,
            Character,
        }

        /// <param name="Code">The VirtualKey for Down/Up, the UTF-16 code unit for Character.</param>
        /// <param name="Extended">CorePhysicalKeyStatus.IsExtendedKey.</param>
        /// <param name="MenuDown">CorePhysicalKeyStatus.IsMenuKeyDown: Alt was held when it happened.</param>
        /// <param name="RepeatCount">CorePhysicalKeyStatus.RepeatCount: 1, or the auto-repeats coalesced into this event.</param>
        /// <param name="Repeat">An auto-repeat: CorePhysicalKeyStatus.WasKeyDown is true.</param>
        private readonly record struct KeyEvent(KeyEventKind Kind, uint Code, bool Extended, bool MenuDown, uint RepeatCount = 1, bool Repeat = false);

        /// <summary>How long a key is held before it starts repeating. The host sets the platform's.</summary>
        public TimeSpan KeyRepeatDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>The time between repeats once a held key has started. The host sets the platform's.</summary>
        public TimeSpan KeyRepeatInterval { get; set; } = TimeSpan.FromMilliseconds(33);

        /// <summary>The clock key presses and repeats are timed by. Replaceable for tests.</summary>
        public Func<TimeSpan> KeyClock { get; set; } = () => KeyWatch.Elapsed;

        private static readonly System.Diagnostics.Stopwatch KeyWatch = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>The key that repeats (the last pressed, still held), or 0. Under <see cref="_keyGate"/>.</summary>
        private uint _repeatKey;
        private bool _repeatExtended;
        private TimeSpan _repeatDue;

        /// <summary>
        /// When <see cref="_repeatKey"/> went down. A character typed while it has been held for half
        /// the repeat delay or more is the host's own repeat; timing rather than counting, because the
        /// first character of a press can arrive before the frame that sees the key go down.
        /// </summary>
        private TimeSpan _repeatPressedAt;

        private readonly ConcurrentQueue<KeyEvent> _keyEvents = new();

        /// <summary>Handlers by the add_ slot of their event, in registration order.</summary>
        private readonly Dictionary<int, List<(long Token, long Handler)>> _keyHandlers = new()
        {
            [SlotAddKeyDown] = new(),
            [SlotAddKeyUp] = new(),
            [SlotAddCharacterReceived] = new(),
        };

        /// <summary>
        /// Token source for keyboard subscriptions. Starts well clear of the lifecycle events, whose
        /// tokens are their slot numbers, so a token handed to the wrong remove_ matches nothing.
        /// </summary>
        private long _nextKeyToken = 0x4B_0000;

        /// <summary>Keys down as of the event being delivered: what GetKeyState answers.</summary>
        private readonly HashSet<uint> _keysDelivered = new();

        /// <summary>Keys down as the host last reported them: what GetAsyncKeyState answers. Written on the host's thread.</summary>
        private readonly HashSet<uint> _keysInjected = new();

        /// <summary>Toggle keys (Caps, Num, Scroll Lock) currently on, by the presses delivered.</summary>
        private readonly HashSet<uint> _keysLocked = new();

        private readonly object _keyGate = new();
        private readonly List<string> _keyboardLog = new();

        /// <summary>Key events raised to the guest.</summary>
        public int KeyEventsDelivered { get; private set; }

        /// <summary>Subscriptions, removals and the first deliveries, for the per-game log.</summary>
        public IReadOnlyList<string> KeyboardLog => _keyboardLog;

        /// <summary>
        /// Queues a key going down or up. Safe from any thread; it reaches the guest's handlers at the
        /// next turn round its main loop.
        /// </summary>
        /// <param name="virtualKey">A <c>Windows.System.VirtualKey</c> (the Win32 VK code).</param>
        /// <param name="extended">
        /// Whether the key is an extended one (right Ctrl/Alt). Keys that are always extended - the
        /// arrows, the Insert/Delete/Home/End block, numpad Divide - are marked here regardless.
        /// </param>
        public void InjectKey(uint virtualKey, bool down, bool extended = false)
        {
            bool menu;
            extended = extended || IsAlwaysExtended(virtualKey);
            lock (_keyGate)
            {
                if (down)
                {
                    _keysInjected.Add(virtualKey);
                    _repeatKey = virtualKey;
                    _repeatExtended = extended;
                    _repeatPressedAt = KeyClock();
                    _repeatDue = _repeatPressedAt + KeyRepeatDelay;
                }
                else
                {
                    _keysInjected.Remove(virtualKey);
                    if (_repeatKey == virtualKey)
                    {
                        _repeatKey = 0;
                    }
                }

                menu = _keysInjected.Contains(VirtualKeyMenu);
                _keyEvents.Enqueue(new KeyEvent(down ? KeyEventKind.Down : KeyEventKind.Up, virtualKey, extended, menu));
            }
        }

        /// <summary>
        /// Queues the auto-repeat that has fallen due for the held key, if any: one <c>KeyDown</c> with
        /// <c>WasKeyDown</c> set and <c>RepeatCount</c> the number of repeats since the last call. The
        /// host calls it once a frame, after injecting that frame's key edges. Safe from any thread.
        /// </summary>
        /// <returns>The repeats queued (0 when none was due).</returns>
        public uint PumpKeyRepeat()
        {
            lock (_keyGate)
            {
                if (_repeatKey == 0)
                {
                    return 0;
                }

                TimeSpan now = KeyClock();
                if (now < _repeatDue)
                {
                    return 0;
                }

                TimeSpan interval = KeyRepeatInterval > TimeSpan.Zero ? KeyRepeatInterval : TimeSpan.FromMilliseconds(1);
                long count = 1 + ((now - _repeatDue).Ticks / interval.Ticks);
                _repeatDue += TimeSpan.FromTicks(interval.Ticks * count);
                uint repeats = (uint)Math.Min(count, 0xFFFF);
                _keyEvents.Enqueue(new KeyEvent(KeyEventKind.Down, _repeatKey, _repeatExtended, _keysInjected.Contains(VirtualKeyMenu), repeats, Repeat: true));
                return repeats;
            }
        }

        /// <summary>Queues a typed character (one UTF-16 code unit). Safe from any thread.</summary>
        public void InjectCharacter(uint code)
        {
            lock (_keyGate)
            {
                bool menu = _keysInjected.Contains(VirtualKeyMenu);
                bool repeat = _repeatKey != 0 && KeyClock() - _repeatPressedAt >= KeyRepeatDelay / 2;
                _keyEvents.Enqueue(new KeyEvent(KeyEventKind.Character, code, false, menu, Repeat: repeat));
            }
        }

        private string KeyEventName(int slot) => slot switch
        {
            SlotAddKeyDown => "KeyDown",
            SlotAddKeyUp => "KeyUp",
            _ => "CharacterReceived",
        };

        private void NoteKeyboard(string line)
        {
            if (_keyboardLog.Count < 64)
            {
                _keyboardLog.Add(line);
            }
        }

        /// <summary><c>HRESULT add_X(TypedEventHandler*, EventRegistrationToken*)</c>.</summary>
        private Action AddKeyHandler(int slot) => () =>
        {
            long handler = Arg(1);
            if (handler == 0)
            {
                Return(HResultInvalidArg);
                return;
            }

            long token = ++_nextKeyToken;
            _keyHandlers[slot].Add((token, handler));
            if (IsWritable(Arg(2), 8))
            {
                _emulator.WriteUInt64(Arg(2), (ulong)token);
            }

            NoteKeyboard($"{KeyEventName(slot)} subscribed, handler 0x{handler:X8}, token 0x{token:X}");

            // Ours to keep: the caller releases its reference once the add returns.
            AddRefThenReturn(handler, HResultOk);
        };

        /// <summary>
        /// <c>HRESULT remove_X(EventRegistrationToken)</c>. The token is 64 bits, so after <c>this</c>
        /// in r0 it is aligned into r2:r3; r1 is padding.
        /// </summary>
        private Action RemoveKeyHandler(int slot) => () =>
        {
            long token = (Arg(3) << 32) | (Arg(2) & 0xFFFFFFFFL);
            List<(long Token, long Handler)> handlers = _keyHandlers[slot];
            int index = handlers.FindIndex(h => h.Token == token);
            if (index < 0)
            {
                Return(HResultOk);
                return;
            }

            long handler = handlers[index].Handler;
            handlers.RemoveAt(index);
            NoteKeyboard($"{KeyEventName(slot)} unsubscribed, handler 0x{handler:X8}");
            ReleaseThenReturn(handler, HResultOk);
        };

        /// <summary>Calls IUnknown::Release on <paramref name="target"/>, then returns <paramref name="value"/> to the caller.</summary>
        private void ReleaseThenReturn(long target, long value)
        {
            long callerReturn = _emulator.ReturnAddress;
            long release = target == 0 ? 0 : _emulator.ReadUInt32(_emulator.ReadUInt32(target, 0) + (SlotRelease * 4), 0);
            if (!_emulator.IsExecutableCode(release))
            {
                Return(value);
                return;
            }

            _emulator.CallEmulated("Delegate::Release", release, [target], onReturn: () =>
            {
                Return(value);
                _emulator.ContinueAt(callerReturn);
            }, recycle: true);
        }

        /// <summary>
        /// <c>GetKeyState(VirtualKey, CoreVirtualKeyStates*)</c> as of the key event being delivered,
        /// or <c>GetAsyncKeyState</c> as the keyboard is now.
        /// </summary>
        private Action KeyState(bool async) => () =>
        {
            uint key = (uint)Arg(1);
            uint state;
            lock (_keyGate)
            {
                bool down = async ? _keysInjected.Contains(key) : _keysDelivered.Contains(key);
                state = (down ? KeyStateDown : KeyStateNone) | (_keysLocked.Contains(key) ? KeyStateLocked : KeyStateNone);
            }

            if (IsWritable(Arg(2), 4))
            {
                _emulator.WriteUInt32(Arg(2), state);
            }

            Return(HResultOk);
        };

        /// <summary>Applies a delivered event to what GetKeyState answers.</summary>
        private void ApplyKeyState(KeyEvent key)
        {
            lock (_keyGate)
            {
                if (key.Kind == KeyEventKind.Down)
                {
                    if (_keysDelivered.Add(key.Code) &&
                        key.Code is VirtualKeyCapitalLock or VirtualKeyNumberKeyLock or VirtualKeyScroll &&
                        !_keysLocked.Remove(key.Code))
                    {
                        _keysLocked.Add(key.Code);
                    }
                }
                else if (key.Kind == KeyEventKind.Up)
                {
                    _keysDelivered.Remove(key.Code);
                }
            }
        }

        /// <summary>
        /// Raises every queued key event to its handlers, or returns false if none was raised. As
        /// with <see cref="DeliverBack"/>, true means it has taken over the return path and will run
        /// <paramref name="continueWith"/> itself.
        /// </summary>
        private bool DeliverKeys(Action continueWith)
        {
            if (_keyEvents.IsEmpty)
            {
                return false;
            }

            var events = new List<KeyEvent>();
            while (_keyEvents.TryDequeue(out KeyEvent queued))
            {
                events.Add(queued);
            }

            int eventIndex = 0;
            int handlerIndex = 0;
            List<(long Token, long Handler)>? targets = null;
            long args = 0;
            bool started = false;

            void Next()
            {
                while (eventIndex < events.Count)
                {
                    KeyEvent key = events[eventIndex];
                    int slot = key.Kind switch
                    {
                        KeyEventKind.Down => SlotAddKeyDown,
                        KeyEventKind.Up => SlotAddKeyUp,
                        _ => SlotAddCharacterReceived,
                    };

                    if (targets is null)
                    {
                        // GetKeyState answers as of this event while its handlers run.
                        ApplyKeyState(key);
                        targets = [.. _keyHandlers[slot]];
                        handlerIndex = 0;
                        args = 0;
                    }

                    while (handlerIndex < targets.Count)
                    {
                        (long token, long handler) = targets[handlerIndex++];
                        if (!_keyHandlers[slot].Contains((token, handler)))
                        {
                            continue;   // removed by an earlier handler of this event
                        }

                        long invoke = _emulator.ReadUInt32(_emulator.ReadUInt32(handler, 0) + (SlotDelegateInvoke * 4), 0);
                        if (!_emulator.IsExecutableCode(invoke))
                        {
                            NoteKeyboard($"{KeyEventName(slot)}: handler 0x{handler:X8} has no Invoke");
                            continue;
                        }

                        if (args == 0)
                        {
                            args = MakeKeyArgs(key);
                            KeyEventsDelivered++;
                            NoteKeyboard($"{KeyEventName(slot)} 0x{key.Code:X2}");
                        }

                        started = true;
                        _emulator.CallEmulated($"CoreWindow::{KeyEventName(slot)}", invoke, [handler, _coreWindow, args], onReturn: Next, recycle: true);
                        return;
                    }

                    targets = null;
                    eventIndex++;
                }

                if (started)
                {
                    continueWith();
                }
            }

            Next();
            return started;
        }

        /// <summary>
        /// <c>Windows.UI.Core.CorePhysicalKeyStatus</c>: RepeatCount, ScanCode (UInt32 each), then
        /// IsExtendedKey, IsMenuKeyDown, WasKeyDown, IsKeyReleased (one byte each) - 12 bytes.
        /// </summary>
        private static byte[] PhysicalKeyStatus(KeyEvent key, uint scanCode)
        {
            byte[] status = new byte[12];
            BitConverter.GetBytes(key.RepeatCount).CopyTo(status, 0);
            BitConverter.GetBytes(scanCode).CopyTo(status, 4);
            status[8] = key.Extended ? (byte)1 : (byte)0;
            status[9] = key.MenuDown ? (byte)1 : (byte)0;
            status[10] = key.Kind == KeyEventKind.Up || key.Repeat ? (byte)1 : (byte)0;   // a key going up, or repeating, was down
            status[11] = key.Kind == KeyEventKind.Up ? (byte)1 : (byte)0;
            return status;
        }

        private long MakeKeyArgs(KeyEvent key) => key.Kind == KeyEventKind.Character
            ? Classes.CharacterReceivedEventArgs(key.Code, PhysicalKeyStatus(key, 0))
            : Classes.KeyEventArgs(key.Code, PhysicalKeyStatus(key, ScanCodeFor(key.Code)));

        /// <summary>Keys Windows always reports as extended: the navigation block, the arrows, the Windows and Apps keys, numpad Divide and Num Lock.</summary>
        private static bool IsAlwaysExtended(uint virtualKey) => virtualKey is
            >= 0x21 and <= 0x28 or      // PageUp, PageDown, End, Home, Left, Up, Right, Down
            0x2D or 0x2E or             // Insert, Delete
            0x5B or 0x5C or 0x5D or     // LeftWindows, RightWindows, Application
            0x6F or 0x90;               // Divide, NumberKeyLock

        private const string ScanRowQ = "QWERTYUIOP", ScanRowA = "ASDFGHJKL", ScanRowZ = "ZXCVBNM";

        /// <summary>
        /// The set-1 scan code a US keyboard reports for <paramref name="virtualKey"/>, which is what
        /// CorePhysicalKeyStatus.ScanCode carries; 0 for a key with no fixed one.
        /// </summary>
        internal static uint ScanCodeFor(uint virtualKey)
        {
            char c = (char)virtualKey;
            if (ScanRowQ.IndexOf(c) is var q and >= 0 && virtualKey is >= 'A' and <= 'Z')
            {
                return 0x10 + (uint)q;
            }

            if (ScanRowA.IndexOf(c) is var a and >= 0 && virtualKey is >= 'A' and <= 'Z')
            {
                return 0x1E + (uint)a;
            }

            if (ScanRowZ.IndexOf(c) is var z and >= 0 && virtualKey is >= 'A' and <= 'Z')
            {
                return 0x2C + (uint)z;
            }

            return virtualKey switch
            {
                >= '1' and <= '9' => 0x02 + (virtualKey - '1'),
                '0' => 0x0B,
                >= 0x70 and <= 0x79 => 0x3B + (virtualKey - 0x70),   // F1-F10
                0x7A => 0x57, 0x7B => 0x58,                          // F11, F12
                0x1B => 0x01, 0x08 => 0x0E, 0x09 => 0x0F, 0x0D => 0x1C, 0x20 => 0x39,
                0x10 => 0x2A, 0x11 => 0x1D, 0x12 => 0x38, 0x14 => 0x3A, 0x90 => 0x45, 0x91 => 0x46,
                0x24 or 0x67 => 0x47, 0x26 or 0x68 => 0x48, 0x21 or 0x69 => 0x49,   // Home/7, Up/8, PageUp/9
                0x25 or 0x64 => 0x4B, 0x65 => 0x4C, 0x27 or 0x66 => 0x4D,           // Left/4, 5, Right/6
                0x23 or 0x61 => 0x4F, 0x28 or 0x62 => 0x50, 0x22 or 0x63 => 0x51,   // End/1, Down/2, PageDown/3
                0x2D or 0x60 => 0x52, 0x2E or 0x6E => 0x53,                         // Insert/0, Delete/Decimal
                0x6A => 0x37, 0x6B => 0x4E, 0x6D => 0x4A, 0x6F => 0x35,             // numpad * + - /
                0x5B => 0x5B, 0x5C => 0x5C, 0x5D => 0x5D,
                0xBD => 0x0C, 0xBB => 0x0D, 0xDB => 0x1A, 0xDD => 0x1B, 0xBA => 0x27, 0xDE => 0x28,
                0xC0 => 0x29, 0xDC => 0x2B, 0xBC => 0x33, 0xBE => 0x34, 0xBF => 0x35,
                _ => 0,
            };
        }

        private sealed partial class SystemClasses
        {
            private sealed class KeyArgsState
            {
                public uint Code;
                public byte[] Status = [];
                public bool Handled;
            }

            private (string, Member) GetKeyHandled => ("get_Handled", c => c.RetBool(c.State<KeyArgsState>().Handled));

            private (string, Member) PutKeyHandled => ("put_Handled", c =>
            {
                c.State<KeyArgsState>().Handled = (c.P(0) & 0xFF) != 0;
                c.Ok();
            });

            /// <summary>Windows.UI.Core.KeyEventArgs: VirtualKey, KeyStatus, and Handled through ICoreWindowEventArgs.</summary>
            public long KeyEventArgs(uint virtualKey, byte[] status)
            {
                ComClass cls = Class("Windows.UI.Core.KeyEventArgs", [
                    I(
                        Meta("Windows.UI.Core.IKeyEventArgs"),
                        ("get_VirtualKey", c => c.RetU32(c.State<KeyArgsState>().Code)),
                        ("get_KeyStatus", c => c.RetBytes(c.State<KeyArgsState>().Status))),
                    I(CoreWindowEventArgs, GetKeyHandled, PutKeyHandled),
                ]);
                return New(cls, new KeyArgsState { Code = virtualKey, Status = status }).Primary;
            }

            /// <summary>Windows.UI.Core.CharacterReceivedEventArgs: KeyCode, KeyStatus, and Handled.</summary>
            public long CharacterReceivedEventArgs(uint code, byte[] status)
            {
                ComClass cls = Class("Windows.UI.Core.CharacterReceivedEventArgs", [
                    I(
                        Meta("Windows.UI.Core.ICharacterReceivedEventArgs"),
                        ("get_KeyCode", c => c.RetU32(c.State<KeyArgsState>().Code)),
                        ("get_KeyStatus", c => c.RetBytes(c.State<KeyArgsState>().Status))),
                    I(CoreWindowEventArgs, GetKeyHandled, PutKeyHandled),
                ]);
                return New(cls, new KeyArgsState { Code = code, Status = status }).Primary;
            }
        }
    }
}
