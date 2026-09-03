using System.Globalization;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Synthesises COM objects inside emulated memory whose methods execute on the host.
    /// </summary>
    /// <remarks>
    /// This is the technique the whole WP8 approach depends on, and it is the reason the
    /// import census is misleading in a good way: the image imports <c>d3d11.dll</c> once,
    /// but calls hundreds of D3D methods. All of them arrive through vtables rather than
    /// through the import table.
    ///
    /// A COM object is just a pointer to a vtable, and a vtable is just an array of
    /// function pointers. Neither has to be real code. So an object is built as:
    ///
    ///     object  -> [ vtable pointer ][ refcount ]
    ///     vtable  -> [ &amp;trap0 ][ &amp;trap1 ][ &amp;trap2 ] ...
    ///
    /// where each trap address is a slot in the emulator's trap page. When emulated code
    /// does the usual <c>ldr r1,[r0] ; ldr r2,[r1,#n] ; blx r2</c>, it branches into the
    /// trap page, the host runs the method, and <c>bx lr</c> returns. The emulated side
    /// cannot tell the difference between this and a real WinRT object.
    ///
    /// Every WinRT interface inherits IInspectable, so slots 0-5 are always the same:
    /// QueryInterface, AddRef, Release, GetIids, GetRuntimeClassName, GetTrustLevel. An
    /// interface's own methods start at slot 6.
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private const int HResultOk = 0;
        private const int HResultFail = unchecked((int)0x80004005); // E_FAIL
        private const int HResultNotImplemented = unchecked((int)0x80004001); // E_NOTIMPL
        private const int HResultNoInterface = unchecked((int)0x80004002);    // E_NOINTERFACE
        private const int HResultClassNotRegistered = unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG

        /// <summary>Number of IInspectable slots every WinRT vtable begins with.</summary>
        private const int InspectableSlots = 6;

        private readonly ArmEmulator _emulator;
        private readonly HStringHeap _strings;
        private readonly Dictionary<string, long> _factories = new(StringComparer.Ordinal);
        private readonly List<string> _unimplementedCalls = new();

        public WinRtRuntime(ArmEmulator emulator, HStringHeap strings)
        {
            _emulator = emulator;
            _strings = strings;

            RegisterMemoryManager();
            RegisterCoreApplicationProbe();

            // Asked for by the image's CreateView. Registered blind, to find out what the
            // app model actually needs before any of it is written properly.
            // Slot 12 is get_ResolutionScale by the documented member order, and the image
            // calls it three times during Run. The discovery default would answer it with a
            // placeholder object pointer, which is a plausible-looking scale factor of
            // about 1.6 billion; the enum value is what it actually wants.
            _factories["Windows.Graphics.Display.DisplayProperties"] = CreateDiscoveryObject(
                "IDisplayPropertiesStatics",
                slotCount: 20,
                known: new Dictionary<int, (string, Action)>
                {
                    // DisplayOrientations get_CurrentOrientation(): None 0, Landscape 1,
                    // Portrait 2, LandscapeFlipped 4, PortraitFlipped 8. The image asked for
                    // landscape - put_AutoRotationPreferences(5) is Landscape|LandscapeFlipped -
                    // and it rotates every pointer position itself according to what this
                    // answers. Unimplemented, the discovery default wrote a placeholder object
                    // pointer here, so the game read its orientation as a number in the
                    // billions, and a switch on that has no case. Taps landed nowhere.
                    [InspectableSlots + 0] = ("get_CurrentOrientation", () =>
                        ReturnUInt32(Rotation switch { "none" => 2u, "cw" => 4u, _ => 1u })),

                    // The device is portrait-native: WVGA is 480x800 with the buttons below.
                    [InspectableSlots + 1] = ("get_NativeOrientation", () => ReturnUInt32(2)),

                    [InspectableSlots + 6] = ("get_ResolutionScale", () =>
                    {
                        // ResolutionScale.Scale100Percent. WVGA is unscaled.
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), 100);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 7] = ("get_LogicalDpi", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteSingle(Arg(1), 96f);
                        }

                        Return(HResultOk);
                    }),
                });
            // The WP7/WP8 bezel Back button. Asked for from inside IFrameworkView::Initialize,
            // where the image subscribes to BackPressed along with its other lifecycle events.
            RegisterDiscoveryClass(
                "Windows.Phone.UI.Input.HardwareButtons", "IHardwareButtonsStatics", slotCount: 8);

            RegisterApplicationData();
            RegisterThreadPool();
            RegisterStore();
            RegisterHostInformation();
            RegisterAppModelObjects();
        }

        /// <summary>
        /// Vtable slots that were called but have no implementation, recorded as
        /// <c>Class::slotN</c>. This is the probe's to-do list: it is discovered by running
        /// the image rather than guessed from documentation.
        /// </summary>
        public IReadOnlyList<string> UnimplementedCalls => _unimplementedCalls;

        /// <summary>
        /// Resolves a runtime class to an activation factory object in emulated memory, or
        /// returns null when the class is not implemented.
        /// </summary>
        public long? GetActivationFactory(string className)
        {
            if (_factories.TryGetValue(className, out long factory))
            {
                return factory;
            }

            // An unknown class gets a shaped stand-in rather than a refusal.
            //
            // Refusing is the *correct* WinRT answer and the image is written to handle it -
            // vccorlib turns it into a ClassNotRegisteredException, which a game catches to
            // fall back to an offline path. That only works if the exception is delivered,
            // and this probe cannot deliver a C++/CX throw: the raise stub returns, the
            // caller carries on with a null factory it was promised it would never see, and
            // the run ends on a null vtable a few instructions later.
            //
            // So the choice is not between "correct" and "lenient", it is between stopping
            // at every class this probe has not got to yet and carrying on with a stand-in
            // whose every call is logged. The stand-in loses nothing: the report names the
            // class and every slot the image called on it, which is the list of what to
            // implement next.
            // The five Microsoft.Xbox interfaces this title binds are implemented for real,
            // against the metadata the game ships in its own XAP. See XboxRuntime.cs.
            if (CreateXboxClass(className) is { } xbox)
            {
                _factories[className] = xbox;
                return xbox;
            }

            // Xbox Live is not something this probe can stand in for: it is a signed-in
            // account, a service and a set of asynchronous callbacks, and the honest answer is
            // that none of them are here. Saying so is not a degradation - the image is
            // written for it, and its own scripts carry the path: `disableXBOX`,
            // `hideLoadingInitXBOX`, `enableXBOX done`. Recovered from the heap, because the
            // scripts ship encrypted - see ScriptDumper.
            //
            // Answering S_OK with a placeholder is what kept the title screen on LOADING: the
            // loading state machine rests until the Xbox callbacks arrive (`restUntilCallback`,
            // `loadingScreenCallbacks`), and a stand-in that always succeeds promises they are
            // coming.
            // Opt-in, with WPR_XBOX=fail. It was worth trying and it did not help: the image
            // takes its sign-in failure path, builds the "Please sign in at Xbox.com" message
            // - and stops on exactly the same title screen. Since it changes what the image
            // believes about itself for no gain, succeeding stays the default.
            bool refuse = className.StartsWith("Microsoft.Xbox", StringComparison.Ordinal) &&
                          string.Equals(
                              Environment.GetEnvironmentVariable("WPR_XBOX"), "fail",
                              StringComparison.OrdinalIgnoreCase);

            long stand = CreateDiscoveryObject(className, slotCount: 40, failing: refuse);
            _factories[className] = stand;
            _improvised.Add(className);
            return stand;
        }

        private readonly List<string> _improvised = new();

        /// <summary>Classes answered with a stand-in because nothing implements them.</summary>
        public IReadOnlyList<string> ImprovisedClasses => _improvised;

        /// <summary>Which vtable slots the image called, and with what. See <see cref="VtableProfile"/>.</summary>
        public VtableProfile Slots { get; } = new();

        public static int ClassNotRegistered => HResultClassNotRegistered;

        // ---------------------------------------------------------------------------
        // Windows.Phone.System.Memory.MemoryManager
        //
        // The smallest real WinRT surface in the image: a static class whose statics
        // interface adds just two read-only properties on top of IInspectable. Small
        // enough to be unambiguous, real enough to prove the mechanism.
        // ---------------------------------------------------------------------------

        /// <summary>Committed bytes reported to the app. A plausible mid-game figure.</summary>
        public const ulong ProcessCommittedBytes = 48UL * 1024 * 1024;

        /// <summary>The WP8 per-app memory cap on a 1 GB device.</summary>
        public const ulong ProcessCommittedLimit = 180UL * 1024 * 1024;

        /// <summary>Vtable slot of <c>get_ProcessCommittedBytes</c> on IMemoryManagerStatics.</summary>
        public const int SlotProcessCommittedBytes = InspectableSlots + 0;

        /// <summary>Vtable slot of <c>get_ProcessCommittedLimit</c> on IMemoryManagerStatics.</summary>
        public const int SlotProcessCommittedLimit = InspectableSlots + 1;

        public const string MemoryManagerClass = "Windows.Phone.System.Memory.MemoryManager";

        private void RegisterMemoryManager()
        {
            long factory = CreateObject(
                "IMemoryManagerStatics",
                ("get_ProcessCommittedBytes", () => ReturnUInt64(ProcessCommittedBytes)),
                ("get_ProcessCommittedLimit", () => ReturnUInt64(ProcessCommittedLimit)));

            _factories["Windows.Phone.System.Memory.MemoryManager"] = factory;
        }

        /// <summary>
        /// Returns a UINT64 through the out-parameter in r1 - the shape every WinRT
        /// property getter uses: <c>HRESULT get_Foo(IInspectable* this, UINT64* value)</c>.
        /// </summary>
        private void ReturnUInt64(ulong value)
        {
            long outPointer = Arg(1);
            if (outPointer != 0)
            {
                _emulator.WriteUInt64(outPointer, value);
            }

            Return(HResultOk);
        }

        // ---------------------------------------------------------------------------
        // Windows.ApplicationModel.Core.CoreApplication
        //
        // Not implemented, deliberately. It is the first class the image asks for, so
        // handing back an object with the right shape but no behaviour lets execution
        // continue far enough to reveal which method it calls first.
        // ---------------------------------------------------------------------------

        private const int CoreApplicationProbeSlots = 16;

        /// <summary>
        /// <c>HRESULT Run(IFrameworkViewSource*)</c> - slot 13 by the documented
        /// ICoreApplication member order, and confirmed by the image calling it with a
        /// single object-pointer argument.
        /// </summary>
        public const int SlotCoreApplicationRun = 13;

        public const string CoreApplicationClass = "Windows.ApplicationModel.Core.CoreApplication";

        /// <summary><c>HRESULT CreateView(IFrameworkView**)</c>, the only method on IFrameworkViewSource.</summary>
        private const int SlotCreateView = InspectableSlots + 0;

        /// <summary>The IFrameworkView the image handed back, once CreateView has returned.</summary>
        public long? FrameworkView { get; private set; }

        // -----------------------------------------------------------------------
        // The view lifecycle
        //
        // IFrameworkView is small and stable, and its slot order is not in doubt:
        // Initialize, SetWindow, Load, Run, Uninitialize, straight after IInspectable.
        // A WinRT host calls them in exactly that order, which is what this drives.
        //
        // Uninitialize is deliberately absent from the sequence: Run is the game's
        // main loop and is not expected to return, so anything queued behind it would
        // never happen anyway.
        // -----------------------------------------------------------------------

        private static readonly (int Slot, string Name)[] ViewLifecycleSteps =
        [
            (InspectableSlots + 0, "Initialize"),
            (InspectableSlots + 1, "SetWindow"),
            (InspectableSlots + 2, "Load"),
            (InspectableSlots + 3, "Run"),
        ];

        private long _coreWindow;
        private long _coreDispatcher;
        private long _coreApplicationView;

        /// <summary>The window size the image is told it has, in pixels.</summary>
        /// <remarks>
        /// WVGA. Every WP8 device shipped at 480x800 or a scale of it, and a game reads
        /// this once to size its render target, so it is the one number here that has a
        /// visible consequence.
        /// </remarks>
        private static float WindowWidth => Direct3DRuntime.BackBufferWidth;

        private static float WindowHeight => Direct3DRuntime.BackBufferHeight;

        /// <summary>How many times the image got round its own main loop.</summary>
        public int ProcessEventsCalls { get; private set; }

        /// <summary>A property getter returning a WinRT boolean.</summary>
        private Action Boolean(bool value) => () =>
        {
            if (ArmEmulator.IsStackAddress(Arg(1)))
            {
                _emulator.WriteBoolean(Arg(1), value);
            }

            Return(HResultOk);
        };

        /// <summary>
        /// GetKeyState / GetAsyncKeyState, which take the key in r1 and return the state
        /// through r2. CoreVirtualKeyStates.None is zero, which is the honest answer: this
        /// has no keyboard.
        /// </summary>
        private Action KeyState() => () =>
        {
            if (ArmEmulator.IsStackAddress(Arg(2)))
            {
                _emulator.WriteUInt32(Arg(2), 0);
            }

            Return(HResultOk);
        };
        private readonly List<string> _lifecycle = new();

        /// <summary>Each view lifecycle step and the HRESULT it returned.</summary>
        public IReadOnlyList<string> Lifecycle => _lifecycle;

        // ---------------------------------------------------------------------
        // Input
        //
        // The image subscribes to five CoreWindow events during SetWindow and then waits.
        // Accepting the subscription and never raising it is honest but sterile: a game
        // that is never touched stays on its title screen forever, and everything past
        // that screen - the menu, a level, the physics - is unreachable no matter how much
        // of the platform works.
        //
        // ProcessEvents is where a real dispatcher delivers input, so it is where this
        // does too. Nothing here simulates a user; it delivers one tap, once, so that the
        // path behind the title screen gets exercised at all.
        // ---------------------------------------------------------------------

        private readonly Dictionary<int, long> _windowHandlers = new();

        /// <summary>Which CoreWindow events the image subscribed to, and with what handler.</summary>
        public IReadOnlyDictionary<int, long> WindowHandlers => _windowHandlers;

        /// <summary>
        /// The pointer events the image subscribed to, which is what says whether a gesture
        /// can be delivered at all.
        /// </summary>
        public string SubscriptionSummary()
        {
            string[] wanted =
            [
                $"{NameOf(SlotAddPointerPressed)}{(_windowHandlers.ContainsKey(SlotAddPointerPressed) ? "" : " MISSING")}",
                $"{NameOf(SlotAddPointerMoved)}{(_windowHandlers.ContainsKey(SlotAddPointerMoved) ? "" : " MISSING")}",
                $"{NameOf(SlotAddPointerReleased)}{(_windowHandlers.ContainsKey(SlotAddPointerReleased) ? "" : " MISSING")}",
            ];

            return $"{_windowHandlers.Count} CoreWindow subscription(s); " + string.Join(", ", wanted) +
                   $"; {_movesDelivered} move(s) delivered";
        }

        /// <summary>Pointer events delivered to the image.</summary>
        public List<string> InputDelivered { get; } = new();

        /// <summary>ICoreWindow slot numbers for the pointer events, by member order.</summary>
        private const int SlotAddPointerMoved = InspectableSlots + 38;

        private const int SlotAddPointerPressed = InspectableSlots + 40;

        private const int SlotAddPointerReleased = InspectableSlots + 42;

        /// <summary>
        /// Not a CoreWindow slot: a step that spends a turn round the main loop and delivers
        /// nothing, which is what <c>wait:N</c> in a gesture script expands to.
        /// </summary>
        /// <remarks>
        /// A script that has to reach a particular screen needs to say "let it run" as well as
        /// "touch here": gestures start on a period boundary, so without this the only way to
        /// put a hundred frames between two taps is a period of a hundred frames, which then
        /// also delays the first one by a hundred. Waiting is a gesture, so a script is a
        /// timeline.
        /// </remarks>
        private const int SlotWait = -1;

        /// <summary>
        /// How many times round the main loop before the tap, and how long it is held.
        /// </summary>
        /// <remarks>
        /// Late enough that the image has finished whatever it does on its first frames -
        /// a tap delivered into a half-built scene tells you nothing you want to know - and
        /// held for a few frames because a game that samples input once a frame can miss a
        /// press and release delivered back to back.
        /// </remarks>
        /// <remarks>
        /// WPR_TAP overrides it; zero never taps at all, which is what a long run wants -
        /// the tap resolves a null weak reference and ends the run, so leaving it enabled
        /// caps every run at 240 frames.
        /// </remarks>
        private static readonly int TapAfterFrames =
            int.TryParse(Environment.GetEnvironmentVariable("WPR_TAP"), out int tap) ? tap : 240;

        /// <summary>
        /// How many PointerMoved events a scripted tap carries between press and release, from
        /// <c>WPR_TAPHOLD</c>; default 8. Zero makes a tap a bare press and release.
        /// </summary>
        /// <remarks>
        /// A real finger reports moves while it is down, so eight is the realistic shape. But
        /// a menu engine that starts a drag on the first move event it sees - this one has
        /// `gainFocusOnDrag` in its scripts - would then never see a tap at all, and the only
        /// way to know which kind of engine this is, is to try both.
        /// </remarks>
        private static readonly int TapHeldFrames =
            int.TryParse(Environment.GetEnvironmentVariable("WPR_TAPHOLD"), out int hold) && hold >= 0 ? hold : 8;

        /// <summary>How many moves a drag is broken into when the script does not say.</summary>
        private const int DragStepsDefault = 12;

        private long _pointerArgs;

        /// <summary>One pointer event: which CoreWindow event, and where.</summary>
        private readonly record struct PointerStep(int Slot, float X, float Y);

        /// <summary>
        /// Pointer events still to be delivered, at most one per turn round the main loop.
        /// </summary>
        /// <remarks>
        /// One per turn is not a throttle, it is the only thing possible: delivering an event
        /// is a tail call into emulated code that takes over the return path, so the stub
        /// cannot deliver a second one and still return. A twelve-step drag therefore takes
        /// twelve frames - which is about a third of a second at the rate this runs, and so
        /// close to how long a real drag takes that nothing has to pretend.
        /// </remarks>
        private readonly Queue<PointerStep> _pending = new();

        /// <summary>Pointer events handed in from another thread - a window's mouse.</summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<PointerStep> _external = new();

        /// <summary>The three pointer events a host can inject.</summary>
        public enum PointerKind
        {
            Pressed,
            Moved,
            Released,
        }

        /// <summary>
        /// Queues a pointer event from outside the emulator. Safe from any thread; it is
        /// delivered on the emulator's, at the next turn round the image's main loop.
        /// </summary>
        /// <remarks>
        /// Coordinates are in the landscape raster space - <see cref="FrameCapture.Width"/> by
        /// <see cref="FrameCapture.Height"/> - which is what the image composes in, not the
        /// portrait bounds the device reports.
        /// </remarks>
        public void InjectPointer(PointerKind kind, float x, float y)
        {
            int slot = kind switch
            {
                PointerKind.Pressed => SlotAddPointerPressed,
                PointerKind.Released => SlotAddPointerReleased,
                _ => SlotAddPointerMoved,
            };

            _external.Enqueue(new PointerStep(slot, x, y));
        }

        /// <summary>Which scripted gesture runs next, cycling.</summary>
        private int _gestureIndex;

        /// <summary>Where the pointer is now. Read by get_Position while an event is live.</summary>
        private float _pointerX = WindowWidth / 2f;

        private float _pointerY = WindowHeight / 2f;

        /// <summary>Whether the pointer is down, which is what get_IsInContact answers.</summary>
        private bool _pointerInContact;

        /// <summary>
        /// The gesture script, from <c>WPR_INPUT</c>, as a list of already-expanded steps.
        /// </summary>
        /// <remarks>
        /// Semicolon-separated gestures, cycled one per period:
        /// <list type="bullet">
        /// <item><c>tap</c> or <c>tap:x,y</c> - press, hold, release at one point.</item>
        /// <item><c>drag:x1,y1&gt;x2,y2</c> or <c>...@steps</c> - press, interpolated moves,
        /// release.</item>
        /// <item><c>wait:N</c> - spend N turns round the main loop touching nothing.</item>
        /// </list>
        /// The default is a single <c>tap</c>, which is what this did before there was a
        /// script at all. Coordinates are in the landscape space the image composes in - see
        /// <see cref="FrameCapture.Width"/> - so the slingshot in Angry Birds is around
        /// <c>drag:150,300&gt;70,350</c> rather than anywhere near the portrait bounds the
        /// device reports.
        /// </remarks>
        private static readonly IReadOnlyList<IReadOnlyList<PointerStep>> Gestures = ParseGestures();

        private static IReadOnlyList<IReadOnlyList<PointerStep>> ParseGestures()
        {
            string script = Environment.GetEnvironmentVariable("WPR_INPUT") ?? "tap";
            var gestures = new List<IReadOnlyList<PointerStep>>();

            foreach (string part in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                IReadOnlyList<PointerStep>? gesture = ParseGesture(part.Trim());
                if (gesture is not null)
                {
                    gestures.Add(gesture);
                }
            }

            return gestures.Count > 0 ? gestures : [BuildTap(FrameCapture.Width / 2f, FrameCapture.Height / 2f)];
        }

        private static IReadOnlyList<PointerStep>? ParseGesture(string text)
        {
            string body = text.Contains(':') ? text[(text.IndexOf(':') + 1)..] : string.Empty;

            if (text.StartsWith("wait", StringComparison.OrdinalIgnoreCase))
            {
                int frames = int.TryParse(body, out int parsed) && parsed > 0 ? parsed : 60;
                return Enumerable.Range(0, frames).Select(_ => new PointerStep(SlotWait, 0f, 0f)).ToList();
            }

            if (text.StartsWith("tap", StringComparison.OrdinalIgnoreCase))
            {
                return TryPoint(body, out float x, out float y)
                    ? BuildTap(x, y)
                    : BuildTap(FrameCapture.Width / 2f, FrameCapture.Height / 2f);
            }

            if (!text.StartsWith("drag", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int steps = DragStepsDefault;
            int at = body.IndexOf('@');
            if (at >= 0)
            {
                if (int.TryParse(body[(at + 1)..], out int parsed) && parsed > 0)
                {
                    steps = parsed;
                }

                body = body[..at];
            }

            string[] ends = body.Split('>');
            if (ends.Length != 2 ||
                !TryPoint(ends[0], out float fromX, out float fromY) ||
                !TryPoint(ends[1], out float toX, out float toY))
            {
                return null;
            }

            return BuildDrag(fromX, fromY, toX, toY, steps);
        }

        private static bool TryPoint(string text, out float x, out float y)
        {
            x = y = 0f;
            string[] parts = text.Split(',');
            return parts.Length == 2 &&
                   float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        /// <summary>
        /// Press, hold, release - with the hold spent on PointerMoved events at the same
        /// point, which is what a real finger reports and costs nothing if the image did not
        /// subscribe to them.
        /// </summary>
        private static IReadOnlyList<PointerStep> BuildTap(float x, float y)
        {
            var steps = new List<PointerStep> { new(SlotAddPointerPressed, x, y) };
            for (int i = 0; i < TapHeldFrames; i++)
            {
                steps.Add(new PointerStep(SlotAddPointerMoved, x, y));
            }

            steps.Add(new PointerStep(SlotAddPointerReleased, x, y));
            return steps;
        }

        private static IReadOnlyList<PointerStep> BuildDrag(
            float fromX, float fromY, float toX, float toY, int steps)
        {
            var built = new List<PointerStep> { new(SlotAddPointerPressed, fromX, fromY) };
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / (steps + 1);
                built.Add(new PointerStep(
                    SlotAddPointerMoved, fromX + ((toX - fromX) * t), fromY + ((toY - fromY) * t)));
            }

            built.Add(new PointerStep(SlotAddPointerReleased, toX, toY));
            return built;
        }

        /// <summary>
        /// Delivers whatever input is due this frame, or returns false if there is none.
        /// </summary>
        /// <remarks>
        /// Returns true when it has taken over the return path - the caller must not also
        /// return, because a delegate invocation is a tail call into emulated code and only
        /// completes as an event.
        /// </remarks>
        private bool DeliverInput(long resumeAt)
        {
            // Anything a live host has injected goes first, and does not depend on the
            // scripted taps being enabled at all - a window with a mouse replaces the script.
            while (_external.TryDequeue(out PointerStep injected))
            {
                _pending.Enqueue(injected);
            }

            if (_pending.Count == 0 && (TapAfterFrames <= 0 || ProcessEventsCalls < TapAfterFrames))
            {
                return false;
            }

            // Start a gesture every TapAfterFrames, not just once. A game does not open on the
            // screen anyone wants to see: this one shows the publisher, then the licensor,
            // then a title screen, and each of them waits for a touch. One tap gets past
            // exactly one of them. Gestures cycle, so a script can tap through the menus and
            // then drag.
            if (_pending.Count == 0 && TapAfterFrames > 0 && ProcessEventsCalls % TapAfterFrames == 0)
            {
                foreach (PointerStep step in Gestures[_gestureIndex % Gestures.Count])
                {
                    _pending.Enqueue(step);
                }

                _gestureIndex++;
            }

            if (_pending.Count == 0)
            {
                return false;
            }

            PointerStep next = _pending.Dequeue();

            if (next.Slot == SlotWait)
            {
                // Spend the turn and deliver nothing. The pointer does not move: a wait between
                // two taps must not drag the finger across the screen on its way.
                return false;
            }

            // Move the pointer before the event goes out, because get_Position is answered
            // from these fields while the handler runs. A drag whose position does not change
            // is a long press, which is a different gesture entirely.
            _pointerX = next.X;
            _pointerY = next.Y;
            _pointerInContact = next.Slot != SlotAddPointerReleased;

            int slot = next.Slot;
            if (!_windowHandlers.TryGetValue(slot, out long handler) || handler == 0)
            {
                // A gesture step the image never subscribed to. Say so once rather than every
                // frame of every drag, and keep spending the turn - the hold has to last.
                if (_unsubscribed.Add(slot))
                {
                    InputDelivered.Add($"{NameOf(slot)}: not subscribed, steps dropped");
                }

                return false;
            }

            string name = NameOf(slot);
            long vtable = _emulator.ReadUInt32(handler);

            // A WinRT delegate is IUnknown-based, so Invoke is slot 3 - not slot 6. This is
            // the same trap the ThreadPool work item fell into.
            long invoke = _emulator.ReadUInt32(vtable + (SlotDelegateInvoke * 4));
            if (!_emulator.IsExecutableCode(invoke))
            {
                // Say what the vtable actually holds. "Not at slot 3" on its own cannot tell
                // a delegate whose layout is different from a pointer that is not a delegate.
                string slots = string.Join(" ", Enumerable.Range(0, 8).Select(i =>
                {
                    long entry = _emulator.ReadUInt32(vtable + (i * 4), 0);
                    return $"{i}:{entry:X8}{(_emulator.IsExecutableCode(entry) ? "*" : "")}";
                }));

                InputDelivered.Add(
                    $"{name}: handler 0x{handler:X8} vtable 0x{vtable:X8} has no Invoke at slot " +
                    $"{SlotDelegateInvoke} - slots {slots} (* = code)");
                return false;
            }

            // A drag is a dozen of these; logging every one buries the run. The ends of a
            // gesture are what matter, so moves are counted rather than listed.
            if (slot == SlotAddPointerMoved)
            {
                _movesDelivered++;
            }
            else
            {
                InputDelivered.Add(
                    $"{name} at ({_pointerX:0}, {_pointerY:0}) -> handler 0x{handler:X8} " +
                    $"invoke 0x{invoke:X8}");
            }

            // Record what the handler does with the event. A pointer event the image accepts
            // with S_OK and then ignores looks exactly like one it acted on; the calls it makes
            // in between - or fails to make - are the only difference visible from here.
            bool capture = slot != SlotAddPointerMoved;
            if (capture)
            {
                _emulator.StartCallCapture();
            }

            _emulator.CallEmulated(
                $"CoreWindow::{name}",
                invoke,
                [handler, _coreWindow, PointerArgs()],
                onReturn: () =>
                {
                    if (capture)
                    {
                        IReadOnlyList<string> made = _emulator.StopCallCapture();
                        InputDelivered.Add(
                            $"   {name} returned 0x{Arg(0):X8} after {made.Count} call(s): {Condense(made)}");
                    }

                    _emulator.ContinueAt(resumeAt);
                });

            return true;
        }

        /// <summary>Invoke on a WinRT delegate. See <see cref="SlotHandlerInvoke"/>.</summary>
        private const int SlotDelegateInvoke = 3;

        /// <summary>Completion handlers this runtime has answered, for the report.</summary>
        public List<string> AsyncCompleted { get; } = new();

        /// <summary>
        /// A call list with runs collapsed and the CRT noise stripped, so the shape of what a
        /// handler did survives being printed on one screen.
        /// </summary>
        private static string Condense(IReadOnlyList<string> calls)
        {
            var parts = new List<string>();
            string? previous = null;
            int run = 0;

            void Flush()
            {
                if (previous is null)
                {
                    return;
                }

                parts.Add(run > 1 ? $"{previous} x{run}" : previous);
            }

            foreach (string raw in calls)
            {
                // memcmp/strlen/new/delete say nothing about intent; the WinRT and Xbox calls do.
                string name = raw[(raw.IndexOf('!') + 1)..];
                if (name.StartsWith("??", StringComparison.Ordinal) ||
                    name is "memcmp" or "memcpy" or "memmove" or "memset" or "strlen" or "strcmp" or
                        "malloc" or "free" or "realloc" or "_Mtx_lock" or "_Mtx_unlock")
                {
                    continue;
                }

                if (name == previous)
                {
                    run++;
                    continue;
                }

                Flush();
                previous = name;
                run = 1;
            }

            Flush();
            return parts.Count == 0
                ? "(only CRT string/memory calls)"
                : string.Join(" > ", parts.Take(60)) + (parts.Count > 60 ? $" ... (+{parts.Count - 60})" : string.Empty);
        }

        /// <summary>
        /// Whether a pointer is plausibly a WinRT delegate: a heap object whose vtable has
        /// executable code at the Invoke slot.
        /// </summary>
        private bool LooksLikeDelegate(long pointer)
        {
            if (pointer == 0 || ArmEmulator.IsStackAddress(pointer))
            {
                return false;
            }

            long vtable = _emulator.ReadUInt32(pointer, 0);
            return vtable != 0 && _emulator.IsExecutableCode(
                _emulator.ReadUInt32(vtable + (SlotDelegateInvoke * 4), 0));
        }

        /// <summary>
        /// Answers a completion handler immediately, as an operation that has already
        /// finished. True if it has taken over the return path.
        /// </summary>
        /// <remarks>
        /// Every asynchronous thing this runtime stands in for has in fact already happened
        /// by the time it is asked - there is nothing behind these objects to wait for - so
        /// completing at once is not merely convenient, it is accurate.
        /// </remarks>
        private bool CompleteAsync(long operation, long handler, string origin)
        {
            long invoke = _emulator.ReadUInt32(_emulator.ReadUInt32(handler, 0) + (SlotDelegateInvoke * 4), 0);
            if (!_emulator.IsExecutableCode(invoke))
            {
                return false;
            }

            long callerReturn = _emulator.ReturnAddress;
            if (AsyncCompleted.Count < 24)
            {
                AsyncCompleted.Add($"{origin}(handler 0x{handler:X8}) -> completed at once");
            }

            _emulator.CallEmulated(
                $"{origin} completion",
                invoke,
                [handler, operation, AsyncStatusCompleted],
                onReturn: () =>
                {
                    Return(HResultOk);
                    _emulator.ContinueAt(callerReturn);
                });

            return true;
        }

        /// <summary>
        /// Takes a reference on an object this runtime is holding on to, and returns true if
        /// it has taken over the return path.
        /// </summary>
        /// <remarks>
        /// AddRef is emulated code, and a host stub can only tail-call - so this finishes the
        /// call itself: it arranges for AddRef to run and for its return trap to put S_OK
        /// back in r0 and continue to wherever the stub was going to return. A caller that
        /// gets true back must return immediately and do nothing else.
        /// </remarks>
        private bool KeepAlive(long instance)
        {
            if (instance == 0)
            {
                return false;
            }

            long vtable = _emulator.ReadUInt32(instance, 0);
            long addRef = _emulator.ReadUInt32(vtable + (SlotAddRef * 4), 0);

            if (!_emulator.IsExecutableCode(addRef))
            {
                return false;
            }

            long resumeAt = _emulator.ReturnAddress;
            _emulator.CallEmulated("delegate AddRef", addRef, [instance], onReturn: () =>
            {
                // AddRef answers the new count, and the caller of the stub wants an HRESULT.
                Return(HResultOk);
                _emulator.ContinueAt(resumeAt);
            });

            return true;
        }

        /// <summary>
        /// How the landscape composition sits on the portrait window, from <c>WPR_ROTATE</c>:
        /// <c>ccw</c> (default - the device turned so its buttons are on the right, WP8's
        /// "Landscape"), <c>cw</c> ("LandscapeFlipped"), or <c>none</c>.
        /// </summary>
        private static readonly string Rotation =
            (Environment.GetEnvironmentVariable("WPR_ROTATE") ?? "ccw").Trim().ToLowerInvariant();

        /// <summary>
        /// Converts a point in the landscape composition to the portrait window coordinates
        /// the image expects to be handed.
        /// </summary>
        /// <remarks>
        /// Hold the phone upright, then turn it a quarter turn anticlockwise: the former top
        /// edge is now the left edge, so landscape x runs along what was portrait y, and
        /// landscape y runs from what was the right edge down to what was the left. Turned
        /// the other way, both axes reverse. The composition size is
        /// <see cref="FrameCapture.Width"/> by <see cref="FrameCapture.Height"/> and the
        /// window is <see cref="Direct3DRuntime.BackBufferWidth"/> by
        /// <see cref="Direct3DRuntime.BackBufferHeight"/>; they are transposes of each other.
        /// </remarks>
        private static (float X, float Y) ToWindow(float landscapeX, float landscapeY)
        {
            float windowWidth = Direct3DRuntime.BackBufferWidth;
            float windowHeight = Direct3DRuntime.BackBufferHeight;

            return Rotation switch
            {
                "none" => (landscapeX, landscapeY),
                "cw" => (landscapeY, windowHeight - landscapeX),
                _ => (windowWidth - landscapeY, landscapeX),
            };
        }

        /// <summary>Gesture steps naming an event the image never subscribed to.</summary>
        private readonly HashSet<int> _unsubscribed = new();

        /// <summary>PointerMoved events delivered, which a drag makes far too many of to list.</summary>
        private int _movesDelivered;

        /// <summary>How many pointer moves reached the image.</summary>
        public int MovesDelivered => _movesDelivered;

        private static string NameOf(int slot) => slot switch
        {
            SlotAddPointerPressed => "PointerPressed",
            SlotAddPointerReleased => "PointerReleased",
            SlotAddPointerMoved => "PointerMoved",
            _ => $"slot {slot}",
        };

        /// <summary>
        /// A PointerEventArgs carrying a PointerPoint at the tap position.
        /// </summary>
        /// <remarks>
        /// IPointerEventArgs is get_CurrentPoint, get_KeyModifiers, get_Handled, put_Handled;
        /// IPointerPoint is get_PointerDevice, get_Position, get_PointerId, get_Timestamp,
        /// get_Properties, get_IsInContact. Only the two a game reads are implemented, and
        /// the rest fall through to discovery so the trace names anything else it wants.
        /// </remarks>
        private long PointerArgs()
        {
            if (_pointerArgs != 0)
            {
                return _pointerArgs;
            }

            // Windows.UI.Input.IPointerPoint, in the order Windows.UI.winmd declares it:
            // PointerDevice, Position, RawPosition, PointerId, FrameId, Timestamp,
            // IsInContact, Properties. The first version of this had PointerId at 8 and
            // Timestamp at 9 - so the two reads this game makes of RawPosition per press were
            // answered with a 32-bit 1 where it expected an 8-byte Point, and its one read of
            // PointerId was answered with a 64-bit timestamp. Every tap it was ever handed
            // therefore landed at (1.4e-45, whatever), and a splash that accepts any touch
            // was the only thing that could look as if input worked.
            long device = CreateDiscoveryObject(
                "IPointerDevice",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    // PointerDeviceType: Touch 0, Pen 1, Mouse 2. A phone has a touch screen.
                    [InspectableSlots + 0] = ("get_PointerDeviceType", () => ReturnUInt32(0)),
                    [InspectableSlots + 1] = ("get_IsIntegrated", Boolean(true)),
                });

            Action position = () =>
            {
                if (ArmEmulator.IsStackAddress(Arg(1)))
                {
                    // Held in the landscape space the image composes in; handed over in the
                    // portrait window space it expects - see ToWindow.
                    (float x, float y) = ToWindow(_pointerX, _pointerY);
                    _emulator.WriteSingle(Arg(1) + 0, x);
                    _emulator.WriteSingle(Arg(1) + 4, y);
                }

                Return(HResultOk);
            };

            long point = CreateDiscoveryObject(
                "IPointerPoint",
                slotCount: 16,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_PointerDevice", () => ReturnObject(device)),
                    [InspectableSlots + 1] = ("get_Position", position),

                    // RawPosition is the position before any transform the app applied to the
                    // window; there is none here, so it is the same point. This is the one this
                    // game reads.
                    [InspectableSlots + 2] = ("get_RawPosition", position),
                    [InspectableSlots + 3] = ("get_PointerId", () => ReturnUInt32(1)),
                    [InspectableSlots + 4] = ("get_FrameId", () => ReturnUInt32((uint)ProcessEventsCalls)),
                    [InspectableSlots + 5] = ("get_Timestamp", () =>
                    {
                        // Microseconds. A flick or a slingshot is a position difference over a
                        // time difference, so the frame counter at a notional 60Hz: monotonic,
                        // evenly spaced, and unrelated to how often the image reads the clock.
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt64(Arg(1), (ulong)ProcessEventsCalls * 16667UL);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 6] = ("get_IsInContact", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteBoolean(Arg(1), _pointerInContact);
                        }

                        Return(HResultOk);
                    }),
                });

            // Windows.UI.Core.IPointerEventArgs: CurrentPoint, KeyModifiers,
            // GetIntermediatePoints. Handled lives on ICoreWindowEventArgs, a separate
            // interface this runtime cannot distinguish through its QueryInterface.
            _pointerArgs = CreateDiscoveryObject(
                "IPointerEventArgs",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_CurrentPoint", () => ReturnObject(point)),
                    [InspectableSlots + 1] = ("get_KeyModifiers", () => ReturnUInt32(0)),
                    [InspectableSlots + 2] = ("GetIntermediatePoints", () => ReturnObject(EmptyVectorView())),
                });

            return _pointerArgs;
        }

        private void RegisterAppModelObjects()
        {
            // The dispatcher is what IFrameworkView::Run pumps: the main loop is
            // GetForCurrentThread()->Dispatcher->ProcessEvents(...) and nothing advances
            // without it. Registered before the window, which hands it out.
            _coreDispatcher = CreateDiscoveryObject(
                "ICoreDispatcher",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_HasThreadAccess", () =>
                    {
                        // The game only ever asks this from the thread it was given, and
                        // there is only one thread here anyway.
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteBoolean(Arg(1), true);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 1] = ("ProcessEvents", () =>
                    {
                        // Take the script snapshot here rather than at the end of the run: a
                        // buffer that was decrypted, compiled and freed only survives until
                        // the allocator hands its memory to something else.
                        if (ScriptDumper.Requested is { Frame: > 0 } wanted &&
                            ProcessEventsCalls >= wanted.Frame &&
                            (ProcessEventsCalls == wanted.Frame ||
                             (wanted.Every > 0 && (ProcessEventsCalls - wanted.Frame) % wanted.Every == 0)))
                        {
                            _emulator.Scripts.Scan(_emulator, wanted.Directory);
                        }

                        // Run whatever the image has queued for "another thread" before pumping
                        // its events. On a device a PPL continuation runs on a pool thread while
                        // the UI loop keeps turning; here there is one thread, and a queued
                        // chore only runs at a yield point. The main loop is ProcessEvents ->
                        // Present -> ProcessEvents and never touches any of the others, so the
                        // sign-in continuation - queued by the completion handler right after
                        // it set its event - sat in the queue for ten thousand frames with the
                        // Lua that hides the loading screen inside it. This is what "process
                        // events" means on a real dispatcher anyway.
                        //
                        // Only when something is queued, so that DrainDeferredCalls' continuation
                        // always runs from a return trap rather than inline in this stub.
                        if (_emulator.PendingDeferredCalls > 0)
                        {
                            long resumeAfterDrain = _emulator.ReturnAddress;
                            Return(HResultOk);
                            _emulator.DrainDeferredCalls(() =>
                            {
                                Return(HResultOk);
                                _emulator.ContinueAt(resumeAfterDrain);
                            });
                            return;
                        }

                        // A real dispatcher drains the input and system queues here, so this
                        // is where input belongs. The count is also what says how many times
                        // the game got round its own main loop.
                        ProcessEventsCalls++;

                        long resumeAt = _emulator.ReturnAddress;
                        Return(HResultOk);

                        // DeliverInput tail-calls into the image when it has something to
                        // deliver, and then returns here itself - so nothing may follow it.
                        DeliverInput(resumeAt);
                    }),
                });

            // ICoreWindow is large. 48 slots was not enough: SetWindow subscribes to an
            // event at slot 56, the read ran off the end of the vtable into the next
            // object on the heap, and the CPU jumped into that object as if it were code.
            // 64 leaves headroom past the last documented member.
            //
            // The named slots below are the documented ICoreWindow member order, and the
            // image has since confirmed five of them independently: it subscribed at 30,
            // 44, 46, 48 and 56, which are exactly add_Closed, add_PointerMoved,
            // add_PointerPressed, add_PointerReleased and add_VisibilityChanged - the five
            // events a game needs and nothing else. Everything unnamed still falls through
            // to discovery.
            _coreWindow = CreateDiscoveryObject(
                "ICoreWindow",
                slotCount: 64,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 1] = ("get_Bounds", () =>
                    {
                        // Windows.Foundation.Rect is four floats. WVGA is the WP8 baseline
                        // and the resolution this title was built against.
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteSingle(Arg(1) + 0, 0f);
                            _emulator.WriteSingle(Arg(1) + 4, 0f);
                            _emulator.WriteSingle(Arg(1) + 8, WindowWidth);
                            _emulator.WriteSingle(Arg(1) + 12, WindowHeight);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 3] = ("get_Dispatcher", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), (uint)_coreDispatcher);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 6] = ("get_IsInputEnabled", Boolean(true)),
                    [InspectableSlots + 10] = ("get_PointerPosition", () =>
                    {
                        // Windows.Foundation.Point, two floats. No pointer is down.
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteSingle(Arg(1) + 0, 0f);
                            _emulator.WriteSingle(Arg(1) + 4, 0f);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 11] = ("get_Visible", Boolean(true)),
                    [InspectableSlots + 12] = ("Activate", () => Return(HResultOk)),
                    [InspectableSlots + 14] = ("GetAsyncKeyState", KeyState()),
                    [InspectableSlots + 15] = ("GetKeyState", KeyState()),
                });

            // ICoreApplicationView slot 6 is get_CoreWindow by the documented member
            // order - the one slot worth wiring up front, since handing back a
            // placeholder window here would split the app across two different windows.
            // CoreWindow is handed to SetWindow, but the image also asks for it by name:
            // CoreWindow::GetForCurrentThread() activates the class and calls slot 6 on
            // ICoreWindowStatic. Without this the activation fails, vccorlib raises
            // ClassNotRegistered, and the game dies inside its own main loop.
            //
            // The two paths must hand back the SAME window. A second, separate object here
            // would split the game across two windows: it would subscribe to input on the
            // one it was given and ask the other one for its bounds.
            _factories["Windows.UI.Core.CoreWindow"] = CreateDiscoveryObject(
                "ICoreWindowStatic",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("GetForCurrentThread", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), (uint)_coreWindow);
                        }

                        Return(HResultOk);
                    }),
                });

            _coreApplicationView = CreateDiscoveryObject(
                "ICoreApplicationView",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_CoreWindow", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), (uint)_coreWindow);
                        }

                        Return(HResultOk);
                    }),
                });
        }

        /// <summary>
        /// Walks the image's own IFrameworkView through the WinRT startup sequence, one
        /// host-to-emulated call at a time.
        /// </summary>
        /// <remarks>
        /// Each step has to wait for the previous one to return, and returning is an
        /// asynchronous event here - control only comes back when the emulated function
        /// finishes. So the sequence is a continuation chain rather than a loop: every
        /// step's completion handler starts the next one, and the last one finally hands
        /// control back to whoever called Run.
        /// </remarks>
        private void StartViewLifecycle(long view, long callerReturn)
        {
            int next = 0;

            void RunNextStep()
            {
                if (next >= ViewLifecycleSteps.Length)
                {
                    Return(HResultOk);
                    _emulator.ContinueAt(callerReturn);
                    return;
                }

                (int slot, string name) = ViewLifecycleSteps[next++];
                long vtable = _emulator.ReadUInt32(view);
                long method = _emulator.ReadUInt32(vtable + (slot * 4));

                long[] arguments = name switch
                {
                    "Initialize" => [view, _coreApplicationView],
                    "SetWindow" => [view, _coreWindow],
                    "Load" => [view, 0], // a null HSTRING is the empty string in WinRT
                    _ => [view],
                };

                _lifecycle.Add($"-> IFrameworkView::{name} at 0x{method:X8}");

                _emulator.CallEmulated($"IFrameworkView::{name}", method, arguments, onReturn: () =>
                {
                    _lifecycle.Add($"   IFrameworkView::{name} returned 0x{Arg(0):X8}");

                    // A completed lifecycle step is the one moment the image is known to
                    // have finished something, which makes it the safe point to let any
                    // background work it queued actually run.
                    _emulator.DrainDeferredCalls(RunNextStep);
                });
            }

            RunNextStep();
        }

        private void RegisterCoreApplicationProbe()
        {
            (string, Action)[] methods = new (string, Action)[CoreApplicationProbeSlots];
            for (int i = 0; i < CoreApplicationProbeSlots; i++)
            {
                int slot = InspectableSlots + i;
                methods[i] = slot == SlotCoreApplicationRun
                    ? ("Run", CoreApplicationRun)
                    : ($"slot{slot}", () =>
                    {
                        // Log the argument registers too: for an unidentified slot they are
                        // the only evidence of what the method actually is.
                        _unimplementedCalls.Add(
                            $"ICoreApplication::slot{slot}  this=0x{Arg(0):X8} r1=0x{Arg(1):X8} r2=0x{Arg(2):X8}");
                        Return(HResultNotImplemented);
                    });
            }

            _factories[CoreApplicationClass] = CreateObject("ICoreApplication", methods);
        }

        /// <summary>
        /// Implements the WinRT app model entry point by calling back into the image.
        /// </summary>
        /// <remarks>
        /// This runs in the opposite direction to everything else here. The image passes an
        /// IFrameworkViewSource it built itself - real ARM code behind a real vtable - and a
        /// host that means to start the app has to call <c>CreateView</c> on it.
        ///
        /// The return address of Run has to be preserved across that call: once CreateView
        /// comes back, the CPU still owes a return to whoever called Run.
        /// </remarks>
        private void CoreApplicationRun()
        {
            long viewSource = Arg(1);
            long callerReturn = _emulator.ReturnAddress;

            if (viewSource == 0)
            {
                Return(HResultNoInterface);
                return;
            }

            // Walk the image's own vtable to find its CreateView implementation.
            long vtable = _emulator.ReadUInt32(viewSource);
            long createView = _emulator.ReadUInt32(vtable + (SlotCreateView * 4));
            long outView = _emulator.AllocateHeap(4);
            _emulator.WriteUInt32(outView, 0);

            _unimplementedCalls.Add(
                $"ICoreApplication::Run -> calling IFrameworkViewSource::CreateView at 0x{createView:X8}");

            _emulator.CallEmulated(
                "IFrameworkViewSource::CreateView",
                createView,
                [viewSource, outView],
                onReturn: () =>
                {
                    long hresult = Arg(0);
                    FrameworkView = _emulator.ReadUInt32(outView);

                    _unimplementedCalls.Add(
                        $"IFrameworkViewSource::CreateView returned HRESULT=0x{hresult:X8} " +
                        $"view=0x{FrameworkView:X8}");

                    if (hresult != HResultOk || FrameworkView is not > 0)
                    {
                        Return(hresult);
                        _emulator.ContinueAt(callerReturn);
                        return;
                    }

                    // With a view in hand, Run's real job begins: drive it through the
                    // startup sequence. Control returns to the image's caller only once
                    // that finishes - which, if Run() behaves like a main loop, it never does.
                    StartViewLifecycle(FrameworkView.Value, callerReturn);
                });
        }

        // ---------------------------------------------------------------------------
        // Windows.Storage.ApplicationData
        //
        // The image asks for this during CreateView and then parses what it gets back,
        // so a placeholder is worse than useless here - it produced a parse-and-throw
        // loop that ran until the instruction budget expired.
        //
        // Slot numbers below were read off the trace, not guessed: get_Current at 6,
        // then slot 12 on the result, then a QueryInterface and slot 12 again. That is
        // IApplicationData::get_LocalFolder followed by IStorageItem::get_Path, and the
        // documented member order for both agrees.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Where a WP8 app's local storage actually lived on the device, per product ID.
        /// </summary>
        public const string LocalFolderPath =
            @"C:\Data\Users\DefApps\AppData\{E94059A2-135C-420E-8E60-BCDA5FC3EC30}\Local";

        private const int SlotApplicationDataCurrent = InspectableSlots + 0;
        private const int SlotLocalSettings = InspectableSlots + 4;
        private const int SlotLocalFolder = InspectableSlots + 6;
        private const int SlotRoamingFolder = InspectableSlots + 7;
        private const int SlotTemporaryFolder = InspectableSlots + 8;

        private const int SlotStorageItemName = InspectableSlots + 5;
        private const int SlotStorageItemPath = InspectableSlots + 6;

        private void RegisterApplicationData()
        {
            long localFolder = CreateStorageFolder("Local", LocalFolderPath);
            long roamingFolder = CreateStorageFolder("Roaming", LocalFolderPath[..^5] + "Roaming");
            long temporaryFolder = CreateStorageFolder("Temp", LocalFolderPath[..^5] + "Temp");

            long applicationData = CreateDiscoveryObject(
                "IApplicationData",
                slotCount: 14,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_Version", () => ReturnUInt32(0)),
                    [SlotLocalFolder] = ("get_LocalFolder", () => ReturnObject(localFolder)),
                    [SlotRoamingFolder] = ("get_RoamingFolder", () => ReturnObject(roamingFolder)),
                    [SlotTemporaryFolder] = ("get_TemporaryFolder", () => ReturnObject(temporaryFolder)),
                });

            _factories["Windows.Storage.ApplicationData"] = CreateDiscoveryObject(
                "IApplicationDataStatics",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    [SlotApplicationDataCurrent] = ("get_Current", () => ReturnObject(applicationData)),
                });
        }

        /// <summary>
        /// A StorageFolder that knows its own name and path.
        /// </summary>
        /// <remarks>
        /// Laid out as IStorageItem, because that is the interface the image queries for
        /// and then reads Path from. A real StorageFolder implements several interfaces
        /// with different vtable layouts, and this cannot: QueryInterface here returns the
        /// same object whatever IID is asked for, so only one layout can be right at a
        /// time. Per-IID vtables are the honest fix when a second interface is needed.
        /// </remarks>
        private long CreateStorageFolder(string name, string path)
            => CreateDiscoveryObject(
                $"IStorageItem<{name}>",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [SlotStorageItemName] = ("get_Name", () => ReturnString(name)),
                    [SlotStorageItemPath] = ("get_Path", () => ReturnString(path)),
                });

        /// <summary>Returns an interface pointer through the out-parameter in r1.</summary>
        private void ReturnObject(long instance)
        {
            if (Arg(1) != 0)
            {
                _emulator.WriteUInt32(Arg(1), (uint)instance);
            }

            Return(HResultOk);
        }

        /// <summary>Returns a freshly allocated HSTRING through the out-parameter in r1.</summary>
        private void ReturnString(string text)
        {
            if (Arg(1) != 0)
            {
                _emulator.WriteUInt32(Arg(1), (uint)_strings.Create(text));
            }

            Return(HResultOk);
        }

        private void ReturnUInt32(uint value)
        {
            if (Arg(1) != 0)
            {
                _emulator.WriteUInt32(Arg(1), value);
            }

            Return(HResultOk);
        }

        // ---------------------------------------------------------------------------
        // Windows.System.Threading.ThreadPool
        //
        // A Unicorn context is one CPU with one set of registers and one stack, so there
        // is no honest way to run a work item *concurrently*. What there is, is a way to
        // run it *immediately*: RunAsync calls the handler inline, waits for it to return,
        // and only then hands back an IAsyncAction that is already complete.
        //
        // For load-and-decode work - which is what a game uses the pool for - that is
        // semantically fine, just serialised. Two things it cannot survive: a work item
        // that loops forever expecting to be a background thread, and a work item that
        // blocks waiting on something the caller has not done yet, which becomes a
        // deadlock rather than a wait. Both would need real scheduling over saved CPU
        // contexts, which the .NET binding does not expose.
        // ---------------------------------------------------------------------------

        /// <summary><c>HRESULT Invoke(IAsyncAction*)</c> on IWorkItemHandler.</summary>
        /// <summary>
        /// Invoke on a WinRT delegate, which is slot 3 and not slot 6.
        /// </summary>
        /// <remarks>
        /// Delegates are the one part of WinRT that is NOT IInspectable-based. A runtime
        /// class begins with the six IInspectable slots and its own members follow at 6; a
        /// delegate - IWorkItemHandler, TypedEventHandler, AsyncActionCompletedHandler,
        /// every add_X handler - derives from plain IUnknown, so its vtable is
        /// QueryInterface, AddRef, Release, Invoke and nothing else.
        ///
        /// Reading slot 6 of a four-slot vtable does not fail as a missing method. On this
        /// image the delegate's vtable is followed immediately in .rdata by the vtable of
        /// _ContinuationTaskHandle, so slot 6 resolved to that class's scalar deleting
        /// destructor. Every "work item" this probe ran was in fact the image tearing the
        /// work item down: the loader thread destroyed itself instead of loading anything,
        /// died on the way out, and the game's main loop then spin-waited forever on a flag
        /// the loader was supposed to set. Two whole layers of symptom from one wrong slot.
        /// </remarks>
        private const int SlotHandlerInvoke = 3;

        /// <summary>IUnknown::AddRef, slot 1 on everything.</summary>
        private const int SlotAddRef = 1;

        /// <summary>AsyncStatus::Completed.</summary>
        private const int AsyncStatusCompleted = 1;

        private readonly List<string> _workItems = new();

        /// <summary>Work items dispatched through the thread pool, and how they finished.</summary>
        public IReadOnlyList<string> WorkItems => _workItems;

        /// <summary>
        /// Windows.Phone.System.Analytics.HostInformation - the per-device identifier.
        /// </summary>
        /// <remarks>
        /// Asked for from inside the game's main loop, one call after the first
        /// ProcessEvents. IHostInformationStatics has exactly one member,
        /// get_PublisherHostId, which hands back an HSTRING that identifies this device to
        /// this publisher - a game uses it to key save data and analytics.
        ///
        /// The value is a fixed string rather than anything derived. It has to be stable
        /// across runs, because a game that sees a different device on every launch will
        /// treat its own saved state as someone else's.
        /// </remarks>
        private void RegisterHostInformation()
        {
            _factories["Windows.Phone.System.Analytics.HostInformation"] = CreateDiscoveryObject(
                "IHostInformationStatics",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_PublisherHostId",
                        () => ReturnString("57505200-0000-4000-8000-000000000001")),
                });
        }

        /// <summary>
        /// Windows.ApplicationModel.Store.CurrentApp, the licensing API.
        /// </summary>
        /// <remarks>
        /// Asked for by the loader thread, which checks whether it is running a trial before
        /// it does anything else. Leaving it unregistered is not neutral: vccorlib raises
        /// ClassNotRegistered, and the loader never reaches the point where it signals the
        /// main thread.
        ///
        /// The answers here are the ones a purchased copy gets. Reporting a trial would be
        /// the more conservative choice in the abstract but not here - a trial build takes a
        /// different and much narrower path through the game, which is not what we are trying
        /// to see run.
        /// </remarks>
        private void RegisterStore()
        {
            // ILicenseInformation: get_ProductLicenses 6, get_IsActive 7, get_IsTrial 8,
            // get_ExpirationDate 9, add_LicenseChanged 10, remove_LicenseChanged 11.
            long license = CreateDiscoveryObject(
                "ILicenseInformation",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 1] = ("get_IsActive", Boolean(true)),
                    [InspectableSlots + 2] = ("get_IsTrial", Boolean(false)),
                });

            // ICurrentAppStatics: get_LicenseInformation 6, get_LinkUri 7, get_AppId 8, then
            // the purchase and receipt calls, which are all async and none of our business.
            long statics = CreateDiscoveryObject(
                "ICurrentAppStatics",
                slotCount: 16,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_LicenseInformation", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), (uint)license);
                        }

                        Return(HResultOk);
                    }),
                });

            _factories["Windows.ApplicationModel.Store.CurrentApp"] = statics;

            // The simulator is what a developer build binds to, and it is the same shape.
            _factories["Windows.ApplicationModel.Store.CurrentAppSimulator"] = statics;
        }

        private void RegisterThreadPool()
        {
            _factories["Windows.System.Threading.ThreadPool"] = CreateDiscoveryObject(
                "IThreadPoolStatics",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    // RunAsync(handler, out action)
                    [InspectableSlots + 0] = ("RunAsync", () => RunWorkItem(Arg(1), Arg(2))),

                    // RunAsync(handler, priority, out action)
                    [InspectableSlots + 1] = ("RunAsync+priority", () => RunWorkItem(Arg(1), Arg(3))),
                });
        }

        /// <summary>
        /// Queues a work item and returns at once, as the real RunAsync does.
        /// </summary>
        /// <remarks>
        /// Running the handler inline here was the obvious first attempt and it does not
        /// work: the image queues background work part-way through building something, so a
        /// handler invoked before RunAsync returns sees half-initialised state and dies
        /// immediately, having made no calls at all. Deferring is both more faithful and
        /// what actually gets past it - the queue drains between view lifecycle steps,
        /// which are the points where the image has genuinely finished a phase.
        /// </remarks>
        private void RunWorkItem(long handler, long outAction)
        {
            if (handler == 0)
            {
                Return(HResultNoInterface);
                return;
            }

            long action = CreateAsyncAction();
            long vtable = _emulator.ReadUInt32(handler);
            long invoke = _emulator.ReadUInt32(vtable + (SlotHandlerInvoke * 4));
            _emulator.QueueDeferredCall("IWorkItemHandler::Invoke", invoke, handler, action);

            if (outAction != 0)
            {
                _emulator.WriteUInt32(outAction, (uint)action);
            }

            // A real thread pool takes a reference on the handler, because the caller is
            // entitled to release it the moment RunAsync returns - and this one does exactly
            // that. Queueing the function pointer without taking that reference leaves the
            // delegate alive only by luck.
            //
            // It ran on luck for a long time and then stopped: the moment weak references
            // started working, the release path actually destroyed the captured functor, and
            // the drain invoked a delegate whose vptr had already been walked back to
            // __abi_CaptureBase - so slot 1 was the next thing along in .rdata, and the CPU
            // jumped into it. The symptom was an invalid instruction during startup with no
            // apparent connection to refcounting at all.
            if (!KeepAlive(handler))
            {
                Return(HResultOk);
            }
        }

        /// <summary>
        /// An IAsyncAction that is already finished by the time anyone can see it, since
        /// the work ran inline. Attaching a completion handler therefore fires it at once.
        /// </summary>
        private long CreateAsyncAction()
        {
            // The completion handler needs the action pointer, which does not exist until
            // the object is built - hence the one-element box.
            long[] self = new long[1];

            long action = CreateDiscoveryObject(
                "IAsyncAction",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("put_Completed", () => CompleteAsyncAction(self[0], Arg(1))),
                    [InspectableSlots + 1] = ("get_Completed", () => ReturnObject(0)),
                    [InspectableSlots + 2] = ("GetResults", () => Return(HResultOk)),
                });

            self[0] = action;
            return action;
        }

        private void CompleteAsyncAction(long action, long completionHandler)
        {
            long callerReturn = _emulator.ReturnAddress;

            if (completionHandler == 0)
            {
                Return(HResultOk);
                return;
            }

            long vtable = _emulator.ReadUInt32(completionHandler);
            long invoke = _emulator.ReadUInt32(vtable + (SlotHandlerInvoke * 4));

            _workItems.Add($"   put_Completed -> completion handler at 0x{invoke:X8}");

            // Record everything the handler does between being invoked and returning. That
            // list is the image's verdict on the result it was handed - the one thing an
            // S_OK return can never say.
            _emulator.StartCallCapture();

            _emulator.CallEmulated(
                "IAsyncActionCompletedHandler::Invoke",
                invoke,
                [completionHandler, action, AsyncStatusCompleted],
                onReturn: () =>
                {
                    IReadOnlyList<string> made = _emulator.StopCallCapture();
                    AsyncCompleted.Add(
                        $"completion handler 0x{invoke:X8} made {made.Count:N0} call(s): {Condense(made)}");

                    Return(HResultOk);
                    _emulator.ContinueAt(callerReturn);
                });
        }

        // ---------------------------------------------------------------------------
        // Discovery objects
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Registers a class whose interface is not known in detail: every slot logs what it
        /// was called with and reports success.
        /// </summary>
        /// <remarks>
        /// Returning S_OK rather than E_NOTIMPL is the point. E_NOTIMPL makes vccorlib throw
        /// immediately, which stops the trace at the first unknown method; claiming success
        /// lets the image keep going and reveal the whole sequence it wanted.
        ///
        /// An out-parameter is blanked only when it points at the stack - a caller's local.
        /// Writing through a pointer that turns out to be a live object instead would
        /// scribble on its vtable pointer, so anything not obviously scratch is left alone.
        /// </remarks>
        private void RegisterDiscoveryClass(string className, string interfaceName, int slotCount)
            => _factories[className] = CreateDiscoveryObject(interfaceName, slotCount);

        /// <summary>
        /// Builds a discovery object that is passed around as an argument rather than
        /// activated by class name, such as the CoreWindow handed to SetWindow.
        /// </summary>
        /// <param name="known">
        /// Slots whose behaviour is known, by absolute vtable index. Everything else falls
        /// through to logging and a placeholder.
        /// </param>
        /// <summary>
        /// Every discovery vtable is at least this long, whatever the interface declares.
        /// </summary>
        /// <remarks>
        /// A vtable that is exactly as long as its interface turns a call to the wrong slot
        /// into the wrong kind of failure. The image's QueryInterface is answered with the same
        /// object for every IID, so it will call IVectorView members on an IIterable and
        /// IIterator members on an IVectorView, and a slot one past the end reads the first
        /// word of whatever heap block comes next and branches to it. Twice that was a list
        /// sentinel pointing at itself, and the report could say only "jumped into the heap".
        /// Padding with traps makes the same mistake print as `IVectorView::slot9`, which is a
        /// line in the to-do list rather than the end of the run.
        /// </remarks>
        private const int MinimumDiscoverySlots = 32;

        private long CreateDiscoveryObject(
            string interfaceName,
            int slotCount,
            IReadOnlyDictionary<int, (string Name, Action Handler)>? known = null,
            bool failing = false)
        {
            slotCount = Math.Max(slotCount, MinimumDiscoverySlots);
            (string, Action)[] methods = new (string, Action)[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                int slot = InspectableSlots + i;

                if (known is not null && known.TryGetValue(slot, out (string Name, Action Handler) implemented))
                {
                    methods[i] = implemented;
                    continue;
                }

                methods[i] = ($"slot{slot}", () =>
                {
                    _unimplementedCalls.Add(
                        $"{interfaceName}::slot{slot}  this=0x{Arg(0):X8} r1=0x{Arg(1):X8} r2=0x{Arg(2):X8}");

                    // Every call on a class nobody implements is a line in the specification
                    // of what implementing it would take.
                    Slots.Record(
                        interfaceName,
                        slot,
                        () => VtableProfile.DescribeCall(_emulator, _strings, Arg(1), Arg(2), Arg(3)),
                        VtableProfile.LooksLikeDelegate(_emulator, Arg(1)) ||
                        VtableProfile.LooksLikeDelegate(_emulator, Arg(2)));

                    // A class this runtime cannot honestly stand in for answers failure - but
                    // still fills the out-parameter, which is the one place this probe
                    // deliberately breaks its own rule that a stub either writes its
                    // out-parameter or reports failure, never neither.
                    //
                    // Blanking it is the correct answer and it kills the run outright. This
                    // image does `hr = call(&out); __abi_ThrowIfFailed(hr);` - and the raise
                    // is an import from vccorlib that nothing here can deliver, so the raise
                    // stub *returns*, the caller carries on believing it succeeded, and calls
                    // through the null one instruction later. Delivering that throw properly
                    // is not a small job and would not help even so: the image carries no RTTI
                    // for `Platform::Exception`, so it cannot catch one by type.
                    //
                    // Failure plus a live object is survivable and lets the caller's own error
                    // path run, which is the whole point of failing.
                    if (failing)
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), (uint)GetPlaceholder($"{interfaceName}::slot{slot}"));
                        }

                        Return(HResultFail);
                        return;
                    }

                    // Hand back a placeholder object rather than zero. Most of these
                    // out-parameters are interface pointers, and the image dereferences
                    // them immediately - blanking one sends it through a null vtable and
                    // execution ends at address 0. A placeholder is wrong for a scalar
                    // out-parameter too, but wrong-and-running beats null-and-stopped when
                    // the whole point is to see what gets called next.
                    if (ArmEmulator.IsStackAddress(Arg(1)))
                    {
                        _emulator.WriteUInt32(Arg(1), (uint)GetPlaceholder($"{interfaceName}::slot{slot}"));
                    }
                    else if (ArmEmulator.IsStackAddress(Arg(2)))
                    {
                        // Shaped like add_SomeEvent(handler, EventRegistrationToken* token):
                        // r1 is the handler object, so the scratch pointer is r2. A zero
                        // token is a valid one - it just never matches a remove.
                        _emulator.WriteUInt64(Arg(2), 0);

                        // Keep the handler. Accepting a subscription and throwing the
                        // delegate away makes the event permanently unraisable, which is
                        // the difference between a game that can be played and one that
                        // can only be watched.
                        if (interfaceName == "ICoreWindow")
                        {
                            _windowHandlers[slot] = Arg(1);
                        }

                        // And take a reference on it, because accepting a subscription is a
                        // promise to keep the delegate alive - the caller releases it the
                        // moment this returns.
                        //
                        // This was invisible while the allocator never freed: the delegate
                        // stayed valid because nothing could reuse its memory. Once the
                        // allocator learned to recycle, every one of the five CoreWindow
                        // subscriptions came back with the *same* address, because each
                        // delegate was freed before the next was made. The tell was a
                        // "handler" whose first three words pointed at itself.
                        if (KeepAlive(Arg(1)))
                        {
                            return;
                        }
                    }
                    else if (LooksLikeDelegate(Arg(1)))
                    {
                        // Shaped like put_Completed(handler) - one argument, and that
                        // argument is a WinRT delegate. Nothing else in WinRT looks like
                        // this: an event registration would want a token back through a
                        // second, stack-allocated argument, and that case is above.
                        //
                        // Registering a completion handler and never calling it is the
                        // quietest way to hang an image there is. It carries on, drawing
                        // frames, running its main loop, waiting for an answer that this
                        // runtime accepted responsibility for and never gave.
                        if (CompleteAsync(Arg(0), Arg(1), $"{interfaceName}::slot{slot}"))
                        {
                            return;
                        }
                    }

                    Return(HResultOk);
                });
            }

            return CreateObject(interfaceName, methods);
        }

        /// <summary>
        /// A shaped object handed back where an interface pointer is expected but no
        /// implementation exists. One per call site, so the trace stays readable.
        /// </summary>
        private long GetPlaceholder(string origin)
        {
            if (_placeholders.TryGetValue(origin, out long existing))
            {
                return existing;
            }

            const int placeholderSlots = 24;
            (string, Action)[] methods = new (string, Action)[placeholderSlots];
            for (int i = 0; i < placeholderSlots; i++)
            {
                int slot = InspectableSlots + i;
                methods[i] = ($"slot{slot}", () =>
                {
                    _unimplementedCalls.Add($"(from {origin})::slot{slot}  r1=0x{Arg(1):X8}");

                    // Placeholders are where the interesting surface actually is. The factory
                    // gets ActivateInstance and little else; everything a class can *do* is
                    // called on the instance it hands back, which is one of these.
                    Slots.Record(
                        $"<- {origin}",
                        slot,
                        () => VtableProfile.DescribeCall(_emulator, _strings, Arg(1), Arg(2), Arg(3)),
                        VtableProfile.LooksLikeDelegate(_emulator, Arg(1)) ||
                        VtableProfile.LooksLikeDelegate(_emulator, Arg(2)));

                    if (ArmEmulator.IsStackAddress(Arg(1)))
                    {
                        _emulator.WriteUInt32(Arg(1), (uint)GetPlaceholder($"{origin}/slot{slot}"));
                    }
                    else if (VtableProfile.LooksLikeDelegate(_emulator, Arg(1)))
                    {
                        // A callback handed to an object this runtime improvised. Accepting it
                        // and never calling it is how an image ends up waiting for ever, and
                        // this is the exact shape that kept Angry Birds Rio on its LOADING
                        // screen: the Xbox service is handed two delegates, the Lua rests
                        // until they fire (`restUntilCallback`, `loadingScreenCallbacks`), and
                        // a stand-in that answers S_OK promises they are coming.
                        //
                        // The same inference exists on the discovery default and never fired
                        // once, because these calls arrive on a *placeholder* - the object the
                        // factory handed back - which is a different path entirely.
                        if (ArmEmulator.IsStackAddress(Arg(2)))
                        {
                            // (handler, token*) is an event registration, not a completion,
                            // and the difference matters enormously. The metadata this image
                            // ships says slot 8 on a ServiceClient is
                            // `add_SignedOut(EventHandler<SignedOutEventArgs>)` - so firing it
                            // on registration tells the game the player just signed out, at
                            // the exact moment it is trying to sign them in.
                            //
                            // A zero token is a valid one; it just never matches a remove.
                            _emulator.WriteUInt64(Arg(2), 0);
                        }
                        else if (CompleteAsync(Arg(0), Arg(1), $"{origin}/slot{slot}"))
                        {
                            // No token to hand back, so this is `put_Completed(handler)` on an
                            // IAsyncOperation - the shape the loading screen is waiting on.
                            return;
                        }
                    }

                    Return(HResultOk);
                });
            }

            long placeholder = CreateObject($"placeholder<{origin}>", methods);
            _placeholders[origin] = placeholder;
            return placeholder;
        }

        private readonly Dictionary<string, long> _placeholders = new(StringComparer.Ordinal);

        // ---------------------------------------------------------------------------
        // Object construction
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Builds a COM object in emulated memory: an IInspectable header followed by the
        /// given interface methods, each wired to a trap that runs on the host.
        /// </summary>
        /// <summary>
        /// Builds an object whose vtable starts at IUnknown, not IInspectable.
        /// </summary>
        /// <remarks>
        /// Most of WinRT is IInspectable-based and <see cref="CreateObject"/> covers it. The
        /// exceptions are real and this probe has now met three of them: delegates, and the
        /// two weak-reference interfaces. All three put their first real method at slot 3.
        /// </remarks>
        private long CreateUnknownObject(
            string interfaceName,
            int slotCount,
            IReadOnlyDictionary<int, (string Name, Action Handler)> known)
        {
            long instance = _emulator.AllocateHeap(8);
            long vtable = _emulator.AllocateHeap(slotCount * 4);
            long self = instance;

            for (int slot = 0; slot < slotCount; slot++)
            {
                int captured = slot;
                // An explicit entry wins even for IUnknown's own slots, which is what lets an
                // object that implements two interfaces answer QueryInterface for real.
                (string name, Action handler) = known is not null && known.ContainsKey(slot)
                    ? (known[slot].Name, known[slot].Handler)
                    : slot switch
                {
                    // S_OK without writing the out-parameter is the mistake this probe has
                    // made more times than any other. These objects implement one interface,
                    // so handing back the object itself is the only answer available - but it
                    // has to actually be handed back.
                    0 => ("QueryInterface", () =>
                    {
                        if (Arg(2) != 0)
                        {
                            _emulator.WriteUInt32(Arg(2), (uint)self);
                        }

                        Return(HResultOk);
                    }),
                    1 => ("AddRef", () => Return(2)),
                    2 => ("Release", () => Return(1)),
                    _ when known.TryGetValue(slot, out (string Name, Action Handler) hit) => (hit.Name, hit.Handler),
                    _ => ($"slot{captured}", () => Return(HResultOk)),
                };

                long trap = _emulator.RegisterVtableMethod($"{interfaceName}::{name}", handler);
                _emulator.WriteUInt32(vtable + (slot * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _emulator.WriteUInt32(instance, (uint)vtable);
            _emulator.WriteUInt32(instance + 4, 1);
            return instance;
        }

        private long CreateObject(string interfaceName, params (string Name, Action Handler)[] methods)
        {
            long instance = _emulator.AllocateHeap(8);
            long vtable = _emulator.AllocateHeap((InspectableSlots + methods.Length) * 4);

            (string Name, Action Handler)[] all =
            [
                ("QueryInterface", () => QueryInterface(instance)),
                ("AddRef", () => AdjustRefCount(instance, +1)),
                ("Release", () => AdjustRefCount(instance, -1)),
                ("GetIids", GetIids),
                ("GetRuntimeClassName", () => Return(HResultNotImplemented)),
                ("GetTrustLevel", GetTrustLevel),
                .. methods,
            ];

            for (int i = 0; i < all.Length; i++)
            {
                long trap = _emulator.RegisterVtableMethod($"{interfaceName}::{all[i].Name}", all[i].Handler);
                _emulator.WriteUInt32(vtable + (i * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _emulator.WriteUInt32(instance, (uint)vtable);
            _emulator.WriteUInt32(instance + 4, 1); // refcount
            return instance;
        }

        /// <summary>
        /// HRESULT QueryInterface(this, REFIID iid, void** out). Hands back the same object
        /// for any interface asked for, which is correct for these single-interface statics
        /// and wrong the moment a real object implements more than one.
        /// </summary>
        /// <summary>Interfaces every WinRT object has, whatever it is.</summary>
        /// <remarks>
        /// These two are why a C++/CX object can be held weakly, and every ref class
        /// supports them. Neither can be answered with the object itself the way the other
        /// IIDs are: <c>IWeakReferenceSource</c> puts <c>GetWeakReference</c> at slot 3 and
        /// <c>IWeakReference</c> puts <c>Resolve</c> there, where an IInspectable has
        /// GetIids - so handing back the original pointer aims both calls at the wrong
        /// method.
        /// </remarks>
        private static readonly Guid IidWeakReferenceSource = new("00000038-0000-0000-c000-000000000046");

        private static readonly Guid IidWeakReference = new("00000037-0000-0000-c000-000000000046");

        private readonly Dictionary<long, long> _weakReferences = new();

        /// <summary>
        /// A weak reference to an object: one method, Resolve, which hands the object back.
        /// </summary>
        /// <remarks>
        /// Nothing here ever dies, so resolving always succeeds. That is the right answer
        /// for a probe - a weak reference that resolves to null sends the image down its
        /// object-has-gone path, which is not the path anyone is trying to see run.
        /// </remarks>
        private long WeakReferenceTo(long instance)
        {
            if (_weakReferences.TryGetValue(instance, out long existing))
            {
                return existing;
            }

            long weak = CreateUnknownObject(
                "IWeakReference",
                slotCount: 4,
                known: new Dictionary<int, (string, Action)>
                {
                    [3] = ("Resolve", () =>
                    {
                        if (Arg(2) != 0)
                        {
                            _emulator.WriteUInt32(Arg(2), (uint)instance);
                        }

                        Return(HResultOk);
                    }),
                });

            _weakReferences[instance] = weak;
            return weak;
        }

        private void QueryInterface(long instance)
        {
            Guid iid = Arg(1) == 0 ? Guid.Empty : _emulator.ReadGuid(Arg(1));

            if (iid == IidWeakReferenceSource)
            {
                // IWeakReferenceSource is IUnknown plus GetWeakReference at slot 3.
                long source = CreateUnknownObject(
                    "IWeakReferenceSource",
                    slotCount: 4,
                    known: new Dictionary<int, (string, Action)>
                    {
                        [3] = ("GetWeakReference", () =>
                        {
                            if (Arg(1) != 0)
                            {
                                _emulator.WriteUInt32(Arg(1), (uint)WeakReferenceTo(instance));
                            }

                            Return(HResultOk);
                        }),
                    });

                if (Arg(2) != 0)
                {
                    _emulator.WriteUInt32(Arg(2), (uint)source);
                }

                Return(HResultOk);
                return;
            }

            long outPointer = Arg(2);
            if (outPointer == 0)
            {
                Return(HResultNoInterface);
                return;
            }

            _emulator.WriteUInt32(outPointer, (uint)instance);
            AdjustRefCount(instance, +1);
            Return(HResultOk);
        }

        /// <summary>AddRef and Release both return the new reference count, not an HRESULT.</summary>
        private void AdjustRefCount(long instance, int delta)
        {
            uint count = (uint)((int)_emulator.ReadUInt32(instance + 4) + delta);
            _emulator.WriteUInt32(instance + 4, count);
            Return(count);
        }

        /// <summary>HRESULT GetIids(this, ULONG* count, IID** iids). Reports none.</summary>
        private void GetIids()
        {
            if (Arg(1) != 0)
            {
                _emulator.WriteUInt32(Arg(1), 0);
            }

            if (Arg(2) != 0)
            {
                _emulator.WriteUInt32(Arg(2), 0);
            }

            Return(HResultOk);
        }

        /// <summary>HRESULT GetTrustLevel(this, TrustLevel* level). 0 is BaseTrust.</summary>
        private void GetTrustLevel()
        {
            if (Arg(1) != 0)
            {
                _emulator.WriteUInt32(Arg(1), 0);
            }

            Return(HResultOk);
        }

        /// <summary>
        /// The n-th argument of the call that reached this stub: r0-r3, then the stack.
        /// </summary>
        /// <remarks>
        /// The stack half matters more here than anywhere: WinRT methods put the out-parameter
        /// last, so every method with four or more inputs returns through the stack.
        /// GetAchievementsAsync(UInt32, UInt32, Boolean, AchievementCollection) writes its
        /// operation to the fifth argument, and an accessor that stopped at r3 threw on it -
        /// which, for the image, was a task that never existed and a loading screen that never
        /// ended. Same rule as CallFrame.Arg: one word per argument, so a 64-bit value has to
        /// be read deliberately rather than counted through.
        /// </remarks>
        private long Arg(int index) => index switch
        {
            0 => _emulator.ReadRegister(Arm.UC_ARM_REG_R0),
            1 => _emulator.ReadRegister(Arm.UC_ARM_REG_R1),
            2 => _emulator.ReadRegister(Arm.UC_ARM_REG_R2),
            3 => _emulator.ReadRegister(Arm.UC_ARM_REG_R3),
            < 0 => throw new ArgumentOutOfRangeException(nameof(index)),
            _ => _emulator.ReadUInt32(_emulator.ReadRegister(Arm.UC_ARM_REG_SP) + ((index - 4) * 4)),
        };

        private void Return(long value) => _emulator.WriteRegister(Arm.UC_ARM_REG_R0, value);
    }
}
