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
    public sealed class WinRtRuntime
    {
        private const int HResultOk = 0;
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
            long stand = CreateDiscoveryObject(className, slotCount: 40);
            _factories[className] = stand;
            _improvised.Add(className);
            return stand;
        }

        private readonly List<string> _improvised = new();

        /// <summary>Classes answered with a stand-in because nothing implements them.</summary>
        public IReadOnlyList<string> ImprovisedClasses => _improvised;

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
        private const float WindowWidth = 800f;
        private const float WindowHeight = 480f;

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

        /// <summary>Pointer events delivered to the image.</summary>
        public List<string> InputDelivered { get; } = new();

        /// <summary>ICoreWindow slot numbers for the pointer events, by member order.</summary>
        private const int SlotAddPointerMoved = InspectableSlots + 38;

        private const int SlotAddPointerPressed = InspectableSlots + 40;

        private const int SlotAddPointerReleased = InspectableSlots + 42;

        /// <summary>
        /// How many times round the main loop before the tap, and how long it is held.
        /// </summary>
        /// <remarks>
        /// Late enough that the image has finished whatever it does on its first frames -
        /// a tap delivered into a half-built scene tells you nothing you want to know - and
        /// held for a few frames because a game that samples input once a frame can miss a
        /// press and release delivered back to back.
        /// </remarks>
        private const int TapAfterFrames = 240;

        private const int TapHeldFrames = 8;

        private long _pointerArgs;

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
            int slot = ProcessEventsCalls switch
            {
                TapAfterFrames => SlotAddPointerPressed,
                TapAfterFrames + TapHeldFrames => SlotAddPointerReleased,
                _ => 0,
            };

            if (slot == 0 || !_windowHandlers.TryGetValue(slot, out long handler) || handler == 0)
            {
                return false;
            }

            string name = slot == SlotAddPointerPressed ? "PointerPressed" : "PointerReleased";
            long vtable = _emulator.ReadUInt32(handler);

            // A WinRT delegate is IUnknown-based, so Invoke is slot 3 - not slot 6. This is
            // the same trap the ThreadPool work item fell into.
            long invoke = _emulator.ReadUInt32(vtable + (SlotDelegateInvoke * 4));
            if (!_emulator.IsExecutableCode(invoke))
            {
                InputDelivered.Add($"{name}: handler 0x{handler:X8} has no Invoke at slot {SlotDelegateInvoke}");
                return false;
            }

            InputDelivered.Add($"{name} at ({TapX:0}, {TapY:0}) -> handler 0x{handler:X8} invoke 0x{invoke:X8}");

            _emulator.CallEmulated(
                $"CoreWindow::{name}",
                invoke,
                [handler, _coreWindow, PointerArgs()],
                onReturn: () =>
                {
                    InputDelivered.Add($"   {name} returned 0x{Arg(0):X8}");
                    _emulator.ContinueAt(resumeAt);
                });

            return true;
        }

        /// <summary>Invoke on a WinRT delegate. See <see cref="SlotHandlerInvoke"/>.</summary>
        private const int SlotDelegateInvoke = 3;

        /// <summary>The middle of the screen, which is where a title screen expects a tap.</summary>
        private const float TapX = 400f;

        private const float TapY = 240f;

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

            long point = CreateDiscoveryObject(
                "IPointerPoint",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 1] = ("get_Position", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteSingle(Arg(1) + 0, TapX);
                            _emulator.WriteSingle(Arg(1) + 4, TapY);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 2] = ("get_PointerId", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), 1);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 5] = ("get_IsInContact", Boolean(true)),
                });

            _pointerArgs = CreateDiscoveryObject(
                "IPointerEventArgs",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_CurrentPoint", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)))
                        {
                            _emulator.WriteUInt32(Arg(1), (uint)point);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 2] = ("get_Handled", Boolean(false)),
                    [InspectableSlots + 3] = ("put_Handled", () => Return(HResultOk)),
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

            Return(HResultOk);
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

            _emulator.CallEmulated(
                "IAsyncActionCompletedHandler::Invoke",
                invoke,
                [completionHandler, action, AsyncStatusCompleted],
                onReturn: () =>
                {
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
        private long CreateDiscoveryObject(
            string interfaceName,
            int slotCount,
            IReadOnlyDictionary<int, (string Name, Action Handler)>? known = null)
        {
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

                    if (ArmEmulator.IsStackAddress(Arg(1)))
                    {
                        _emulator.WriteUInt32(Arg(1), (uint)GetPlaceholder($"{origin}/slot{slot}"));
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

            for (int slot = 0; slot < slotCount; slot++)
            {
                int captured = slot;
                (string name, Action handler) = slot switch
                {
                    0 => ("QueryInterface", () => Return(HResultOk)),
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

        private long Arg(int index) => index switch
        {
            0 => _emulator.ReadRegister(Arm.UC_ARM_REG_R0),
            1 => _emulator.ReadRegister(Arm.UC_ARM_REG_R1),
            2 => _emulator.ReadRegister(Arm.UC_ARM_REG_R2),
            3 => _emulator.ReadRegister(Arm.UC_ARM_REG_R3),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        private void Return(long value) => _emulator.WriteRegister(Arm.UC_ARM_REG_R0, value);
    }
}
