using System.Collections.Concurrent;

namespace WPR.Wp8Native
{
    /// <summary>A guest object as a bridge argument: the managed side found its pointer.</summary>
    public readonly record struct GuestRef(long Pointer);

    /// <summary>
    /// A managed object as a bridge argument: the page's own implementation of one of the
    /// component's interfaces (AC Pirates' <c>CSharpFunctions</c>), which the component calls back into.
    /// </summary>
    public readonly record struct HostManaged(object Value);

    /// <summary>
    /// One call a title's own managed page makes on its native component, crossing to the guest
    /// thread (Docs/Wp8Native/ManagedPageBridge.md). Made on the host's thread; the guest fills
    /// in the result and sets <see cref="Done"/>.
    /// </summary>
    /// <param name="Target">The guest object, or 0 for an activation or a static.</param>
    /// <param name="Arguments">
    /// float, double, int, uint, long, bool, string, <see cref="SizeF"/>, <see cref="GuestRef"/>,
    /// a <see cref="Delegate"/> (a C# event handler), <see cref="XamlShell.ManipulationHost"/>, or null.
    /// </param>
    public sealed record BridgeCall(BridgeCallKind Kind, string ClassName, string Member, long Target, object?[] Arguments)
    {
        public ManualResetEventSlim Done { get; } = new(false);

        /// <summary>The 8 bytes the member wrote through its out parameter (an activation: the instance).</summary>
        public ulong ResultBits { get; set; }

        /// <summary>The member's return type in the metadata ("Void", "Boolean", "String", a class name...).</summary>
        public string ResultType { get; set; } = "Void";

        /// <summary>A String result's text.</summary>
        public string? ResultText { get; set; }

        /// <summary>Why the call could not be made; the result is then the default.</summary>
        public string? Error { get; set; }
    }

    public enum BridgeCallKind
    {
        /// <summary>A runtime class's constructor: its factory's ActivateInstance.</summary>
        Activate,

        /// <summary>A method or accessor of a guest object, or a static of a class.</summary>
        Invoke,

        /// <summary><c>DrawingSurfaceBackgroundGrid.SetBackgroundContentProvider</c>: connect it and start drawing.</summary>
        SetProvider,
    }

    public sealed partial class WinRtRuntime
    {
        private readonly ConcurrentQueue<BridgeCall> _bridgeCalls = new();
        private readonly SemaphoreSlim _bridgeSignal = new(0);
        private Thread? _guestThread;

        /// <summary>Raised on the calling thread when a bridge call is queued: the host wakes the guest from its frame wait.</summary>
        public event Action? BridgeCallQueued;

        /// <summary>Calls the bridge made, in order (first 200), for the log.</summary>
        public List<string> BridgeLog { get; } = new();

        /// <summary>
        /// Queues a call for the guest thread and waits for it. A call made ON the guest thread (a
        /// C# event handler the component raised, calling back in) cannot wait for the guest, which
        /// is that very thread; it is refused and answered with the default.
        /// </summary>
        /// <remarks>
        /// A call from the page's UI thread (<see cref="BridgeUiThread"/>) runs on the guest's main
        /// thread. A call from any other managed thread runs on a guest thread of its own (a lane),
        /// as it did on the phone: Mandreel's page parks a C# thread in the component's
        /// <c>wait_event()</c> until the game posts a message, and on the main thread that wait
        /// would stop the frame loop for good.
        /// </remarks>
        public void RunBridgeCall(BridgeCall call, TimeSpan timeout)
        {
            if (Thread.CurrentThread == _guestThread)
            {
                call.Error = "re-entrant call from the guest thread";
                BridgeNote($"refused {call.Member}: {call.Error}");
                return;
            }

            Thread? ui = BridgeUiThread;
            if (ui is null || Thread.CurrentThread == ui || call.Kind != BridgeCallKind.Invoke)
            {
                _bridgeCalls.Enqueue(call);
            }
            else
            {
                BridgeLane lane = _lanes.GetOrAdd(Environment.CurrentManagedThreadId, id =>
                {
                    var created = new BridgeLane(id, Thread.CurrentThread.Name);
                    _lanesToStart.Enqueue(created);
                    return created;
                });
                lane.Calls.Enqueue(call);
            }

            _bridgeSignal.Release();
            BridgeCallQueued?.Invoke();
            BridgeCallWaiting = $"{call.ClassName}.{call.Member}";
            try
            {
                if (!call.Done.Wait(timeout))
                {
                    call.Error ??= "timed out";
                    BridgeNote($"{call.ClassName}.{call.Member}: no answer within {timeout.TotalSeconds:0}s");
                }
            }
            finally
            {
                BridgeCallWaiting = null;
            }
        }

        private volatile string? _bridgeCallWaiting;

        /// <summary>The bridge call a managed thread is waiting on right now, if any (for a watchdog).</summary>
        public string? BridgeCallWaiting
        {
            get => _bridgeCallWaiting;
            private set => _bridgeCallWaiting = value;
        }

        private void BridgeNote(string line)
        {
            lock (BridgeLog)
            {
                if (BridgeLog.Count < 200)
                {
                    BridgeLog.Add(line);
                }
            }

            Log("bridge: " + line);
        }

        /// <summary>
        /// Begins hosting a component whose own managed page drives it: DllMain, then the guest
        /// serves bridge calls until the page hands over its content provider, and from then on
        /// once a frame. The emulator must be run from the returned address.
        /// </summary>
        public long StartBridgedComponent(PeImage image, WinmdReader metadata, string componentDll)
        {
            _metadata = metadata;
            _componentImage = image;
            _bridged = true;
            // ClassId is filled in by the first activation; the page's own steps are its own.
            _shell = new XamlShell(componentDll, "", [], []);
            _scratch = _emulator.AllocateHeap(256);
            _iidCandidates = HarvestIidCandidates(image, metadata);
            if (!image.Exports.TryGetValue("DllGetActivationFactory", out uint factoryRva))
            {
                throw new InvalidOperationException($"{componentDll} exports no DllGetActivationFactory");
            }

            _getActivationFactory = image.ImageBase + factoryRva;
            if (image.EntryPointRva == 0)
            {
                throw new InvalidOperationException($"{componentDll} has no entry point to start the guest at");
            }

            _emulator.CallEmulated("DllMain", image.EntryPoint, [image.ImageBase, 1, 0], onReturn: () =>
            {
                _guestThread = Thread.CurrentThread;
                Log($"DllMain(PROCESS_ATTACH) returned {Arg(0)}; serving the managed page");
                ServeBridge();
            });

            return _emulator.StartWithModuleInitialisers(image.EntryPoint);
        }

        private bool _bridged;
        private long _getActivationFactory;
        private bool _serving;
        private bool _serveAgain;

        private bool _connectPending;
        private int _pageTurns;
        private int _pageTurnAtProvider = -1;

        /// <summary>The host's UI thread finished a turn (its handlers returned); called by the host once an update.</summary>
        public void PageTurned() => Interlocked.Increment(ref _pageTurns);

        /// <summary>The provider was handed over and the UI thread has not finished the turn that did it yet.</summary>
        private bool AwaitingPageTurn => _bridged && _pageTurnAtProvider >= 0 && Volatile.Read(ref _pageTurns) <= _pageTurnAtProvider;

        /// <summary>The page's UI thread, set by the host; calls from other threads get lanes. Null: every call is the UI thread's.</summary>
        public Thread? BridgeUiThread { get; set; }

        /// <summary>A managed thread other than the UI thread, and the guest thread that serves its calls.</summary>
        private sealed class BridgeLane(int managedThreadId, string? name)
        {
            public int ManagedThreadId { get; } = managedThreadId;

            public string Name { get; } = name ?? $"managed thread {managedThreadId}";

            public ConcurrentQueue<BridgeCall> Calls { get; } = new();

            /// <summary>The lane's own out-parameter slot: the main thread's scratch may be mid-call when this one runs.</summary>
            public long Scratch { get; set; }

            public bool Turning { get; set; }

            public bool TurnAgain { get; set; }
        }

        private readonly ConcurrentDictionary<int, BridgeLane> _lanes = new();
        private readonly ConcurrentQueue<BridgeLane> _lanesToStart = new();

        /// <summary>A page whose provider is a <c>DrawingSurface</c>'s (or a grid's), and whether the page is landscape.</summary>
        public bool PresentsUnturned => _bridged && _shell is { PresentsUnturned: true };

        /// <summary>Starts the guest threads for lanes asked for since the last turn. Runs on the guest's main thread.</summary>
        private void StartPendingLanes()
        {
            while (_lanesToStart.TryDequeue(out BridgeLane? lane))
            {
                lane.Scratch = _emulator.AllocateHeap(16);
                BridgeLane captured = lane;
                long gate = _emulator.RegisterVtableMethod($"bridge lane ({lane.Name})", () => LaneTurn(captured));
                int id = _emulator.CreateGuestThread($"bridge lane ({lane.Name})", ArmEmulator.ThumbEntry(gate), 0);
                BridgeNote($"guest thread {id} serves the calls of {lane.Name}");
            }
        }

        /// <summary>
        /// One turn of a lane's guest thread: the next call, or park until there is one. Parked, the
        /// other guest threads run; a lane never blocks the OS thread they all share.
        /// </summary>
        private void LaneTurn(BridgeLane lane)
        {
            if (lane.Turning)
            {
                lane.TurnAgain = true;
                return;
            }

            lane.Turning = true;
            try
            {
                do
                {
                    lane.TurnAgain = false;
                    if (_emulator.StopReason is not null)
                    {
                        return;
                    }

                    if (lane.Calls.TryDequeue(out BridgeCall? call))
                    {
                        // Answered without entering the guest, the call came straight back here
                        // (TurnAgain); otherwise its continuation takes the next turn.
                        ExecuteBridgeCall(call, () => LaneTurn(lane), lane.Scratch);
                        continue;
                    }

                    if (_emulator.BlockCurrentThread(() => !lane.Calls.IsEmpty || _emulator.StopReason is not null,
                            onResume: () => LaneTurn(lane), why: $"bridge lane {lane.Name} idle", timeout: TimeSpan.FromMilliseconds(16)))
                    {
                        return;
                    }

                    // Nothing else could run: wait on the OS for a call, then look again.
                    _bridgeSignal.Wait(16);
                    lane.TurnAgain = true;
                }
                while (lane.TurnAgain);
            }
            finally
            {
                lane.Turning = false;
            }
        }

        /// <summary>
        /// The guest between bridge calls before the content provider is connected: take the next
        /// call, or wait for one. Run as a loop, not by recursion, for the same reason as the idle
        /// turn: a call answered without entering the guest comes straight back here.
        /// </summary>
        private void ServeBridge()
        {
            if (_serving)
            {
                _serveAgain = true;
                return;
            }

            _serving = true;
            try
            {
                do
                {
                    _serveAgain = false;
                    if (_emulator.StopReason is not null)
                    {
                        return;
                    }

                    StartPendingLanes();
                    if (_connectPending && !AwaitingPageTurn)
                    {
                        // From here the frame loop serves the calls (InputThenDraw).
                        _connectPending = false;
                        ConnectProvider();
                        return;
                    }

                    if (!_bridgeCalls.TryDequeue(out BridgeCall? call))
                    {
                        // Nothing for the main thread yet: let the lanes and the component's own
                        // threads run (a Mandreel page's message thread starts before the provider).
                        // When another thread was picked, this trap must return for the switch to
                        // happen, and the scheduler re-enters ServeBridge when it comes back here.
                        // Only when nothing else could run is the continuation called at once, and
                        // it sets _serveAgain. Forcing it spun the main thread here for ever while
                        // the page's message thread (Fruit Ninja's wait_event) never ran: no
                        // Connect, no frame.
                        _bridgeSignal.Wait(16);
                        _emulator.RunOtherThreads(ServeBridge);
                        continue;
                    }

                    if (call.Kind == BridgeCallKind.SetProvider)
                    {
                        // The grid connects once it is on screen, i.e. after the handler that
                        // handed the provider over has returned: the rest of it comes first.
                        AdoptProvider(call);
                        _connectPending = true;
                        _serveAgain = true;
                        continue;
                    }

                    ExecuteBridgeCall(call, ServeBridge, _scratch + 32);
                }
                while (_serveAgain);
            }
            finally
            {
                _serving = false;
            }
        }

        /// <summary>Takes a bridge call queued during play, for <see cref="InputThenDraw"/>.</summary>
        private bool ServeBridgeCallInFrame(Action next)
        {
            if (!_bridged)
            {
                return false;
            }

            StartPendingLanes();
            if (!_bridgeCalls.TryDequeue(out BridgeCall? call))
            {
                return false;
            }

            if (call.Kind == BridgeCallKind.SetProvider)
            {
                if (call.Target == 0 && _providerNative != 0 && !_providerDisconnected)
                {
                    // The page let go of its surface: the provider is disconnected and no frame is
                    // drawn again (the component is about to be closed).
                    BridgeNote("content provider released by the page; drawing stops");
                    call.Done.Set();
                    DisconnectProvider(next);
                    return true;
                }

                BridgeNote($"content provider replaced with 0x{call.Target:X8} during play; kept the first");
                call.Done.Set();
                next();
                return true;
            }

            ExecuteBridgeCall(call, next, _scratch + 32);
            return true;
        }

        /// <summary>
        /// The provider the page handed over, and what kind: a <c>DrawingSurface</c>'s
        /// (Arguments[0] true) draws into a texture of its own, in the page's orientation
        /// (Arguments[1]: the page is landscape).
        /// </summary>
        private void AdoptProvider(BridgeCall call)
        {
            bool surface = call.Arguments.Length > 0 && call.Arguments[0] is true;
            bool landscape = call.Arguments.Length > 1 && call.Arguments[1] is true;
            _provider = call.Target;
            _pageTurnAtProvider = Volatile.Read(ref _pageTurns);
            _shell = _shell! with { DrawingSurface = surface, Landscape = landscape };
            PointerRotation = _shell.PresentsUnturned ? "none" : null;
            BridgeNote($"content provider 0x{_provider:X8} ({(surface ? "DrawingSurface" : "background grid")}{(landscape ? ", landscape page" : "")})");
            call.Done.Set();
        }

        /// <summary>
        /// The page's orientation as the host now shows it, once a frame. A DrawingSurface is an
        /// element of the page, so its texture follows the page: the page may hand its provider
        /// over (Fruit Ninja's constructor) before its landscape orientation has been applied, and
        /// the value read then would leave a landscape surface drawn as a portrait one, sideways.
        /// </summary>
        public void NotePageLandscape(bool landscape)
        {
            if (!_bridged || _shell is not { DrawingSurface: true } shell || shell.Landscape == landscape)
            {
                return;
            }

            _shell = shell with { Landscape = landscape };
            PointerRotation = _shell.PresentsUnturned ? "none" : null;
            BridgeNote($"the page is now {(landscape ? "landscape" : "portrait")}; the surface follows it");
        }

        private void ExecuteBridgeCall(BridgeCall call, Action next, long outSlot)
        {
            void Finish()
            {
                call.Done.Set();
                next();
            }

            try
            {
                if (call.Kind == BridgeCallKind.Activate)
                {
                    ActivateForBridge(call, Finish, outSlot);
                    return;
                }

                if (call.Member is "Dispose" or "Close" && call.Target != 0)
                {
                    // Closing the page's component: stop drawing it first, or the next frame calls
                    // a provider whose component has gone (on the phone the grid was gone too).
                    if (call.Target == _instance && _providerNative != 0 && !_providerDisconnected)
                    {
                        BridgeNote("the page closes its component; the provider is disconnected first");
                        DisconnectProvider(() => ExecuteBridgeCall(call, next, outSlot));
                        return;
                    }

                    // IClosable is a Windows interface the component's metadata only names: its
                    // one method, Close, is slot 6 behind the well-known IID.
                    QueryInterface(call.Target, ClosableIid, closable =>
                    {
                        if (closable == 0)
                        {
                            call.Error = "no IClosable";
                            Finish();
                            return;
                        }

                        CallSlot("IClosable::Close", closable, WinmdReader.InspectableSlots, [], [], hr =>
                        {
                            BridgeNote($"Close() = 0x{hr:X8}");
                            Finish();
                        });
                    });
                    return;
                }

                InvokeForBridge(call, Finish, outSlot);
            }
            catch (Exception ex)
            {
                call.Error = ex.Message;
                BridgeNote($"{call.Member} threw {ex.GetType().Name}: {ex.Message}");
                Finish();
            }
        }

        private readonly Dictionary<string, long> _bridgeFactories = new(StringComparer.Ordinal);
        private readonly Dictionary<(long Target, string Interface), long> _bridgeInterfaces = new();

        /// <summary>
        /// A class's activation factory: the component's own export first (loading the component's
        /// DLL a second time would map a second copy), else the package DLL that registers the class.
        /// </summary>
        private void BridgeFactory(string className, Action<long> then)
        {
            if (_bridgeFactories.TryGetValue(className, out long known))
            {
                then(known);
                return;
            }

            long classId = _strings.Create(className);
            long outFactory = _emulator.AllocateHeap(8);
            _emulator.WriteUInt32(outFactory, 0);
            Call("DllGetActivationFactory", _getActivationFactory, [classId, outFactory], [], hr =>
            {
                long factory = hr == 0 ? _emulator.ReadUInt32(outFactory) : 0;
                if (factory != 0)
                {
                    BridgeNote($"factory for {className}: 0x{factory:X8} (the component)");
                    _bridgeFactories[className] = factory;
                    then(factory);
                    return;
                }

                ActivatePackageFactory(className, other =>
                {
                    BridgeNote($"factory for {className}: 0x{other:X8} (a package DLL)");
                    if (other != 0)
                    {
                        _bridgeFactories[className] = other;
                    }

                    then(other);
                });
            });
        }

        /// <summary>
        /// The page's main object: the first instance of a class that hands out a content provider
        /// (or, until one does, the first instance at all). The rest of the host - the provider,
        /// the manipulation handler, the lifecycle - knows the component by it.
        /// </summary>
        private void AdoptInstance(string className, long instance, long factory)
        {
            if (instance == 0)
            {
                return;
            }

            bool drawsThePage = _metadata?.FindMethod(className, "CreateContentProvider") is not null;
            if (_instance == 0 || (drawsThePage && _shell!.ClassId != className && _metadata?.FindMethod(_shell.ClassId, "CreateContentProvider") is null))
            {
                _instance = instance;
                _factory = factory;
                _instanceInterfaces.Clear();
                _shell = _shell! with { ClassId = className };
                BridgeNote($"the page's component is {className} 0x{instance:X8}");
            }
        }

        /// <summary>ActivateInstance, or with arguments the factory's CreateInstance(...).</summary>
        private void ActivateForBridge(BridgeCall call, Action done, long outSlot)
        {
            BridgeFactory(call.ClassName, factory =>
            {
                if (factory == 0)
                {
                    call.Error = "no activation factory";
                    BridgeNote($"new {call.ClassName}(): {call.Error}");
                    done();
                    return;
                }

                if (call.Arguments.Length > 0 && _metadata?.FindStaticMethod(call.ClassName, "CreateInstance") is { } create)
                {
                    // A factory's statics are an interface of their own on the factory object.
                    WithInterfaceOn(factory, create.Interface, statics =>
                    {
                        if (statics == 0)
                        {
                            call.Error = $"{create.Interface.FullName} not available";
                            BridgeNote($"new {call.ClassName}(...): {call.Error}");
                            done();
                            return;
                        }

                        InvokeOn(statics, create.Interface, create.Method, "CreateInstance", call.Arguments, call, outSlot, () =>
                        {
                            AdoptInstance(call.ClassName, (long)(uint)call.ResultBits, factory);
                            call.ResultType = call.ClassName;
                            done();
                        });
                    });
                    return;
                }

                _emulator.WriteUInt32(outSlot, 0);
                CallSlot("ActivateInstance", factory, 6, [outSlot], [], hr =>
                {
                    long instance = _emulator.ReadUInt32(outSlot);
                    BridgeNote($"new {call.ClassName}() = 0x{hr:X8}, instance 0x{instance:X8}");
                    AdoptInstance(call.ClassName, instance, factory);
                    call.ResultBits = (ulong)instance;
                    call.ResultType = call.ClassName;
                    done();
                });
            });
        }

        /// <summary>
        /// A member by name: an instance member on the call's target, or a static of its class
        /// (through the class's factory), marshalled by the metadata, with the whole out value and
        /// its type kept for the managed side.
        /// </summary>
        private void InvokeForBridge(BridgeCall call, Action done, long outSlot)
        {
            if (_metadata is null)
            {
                call.Error = "no component metadata";
                done();
                return;
            }

            string className = call.ClassName;
            string member = call.Member;
            var found = className.Length == 0 ? null : _metadata.FindMethod(className, member);
            if (found is null && member == "SetManipulationHost")
            {
                found = (ManipulationHandlerLayout, ManipulationHandlerLayout.Methods[0]);
            }

            // An interface the component implements, called through a proxy: the bridge passes the
            // interface's own name.
            if (found is null && _metadata.Interface(className) is { } named &&
                named.Methods.FirstOrDefault(m => m.Name == member) is { } byInterface)
            {
                found = (named, byInterface);
            }

            if (found is null && call.Target == 0 && _metadata.FindStaticMethod(className, member) is { } staticMethod)
            {
                BridgeFactory(className, factory =>
                {
                    if (factory == 0)
                    {
                        call.Error = "no activation factory";
                        BridgeNote($"{className}.{member}: {call.Error}");
                        done();
                        return;
                    }

                    WithInterfaceOn(factory, staticMethod.Interface, statics =>
                    {
                        if (statics == 0)
                        {
                            call.Error = $"{staticMethod.Interface.FullName} not available";
                            BridgeNote($"{className}.{member}: {call.Error}");
                            done();
                            return;
                        }

                        InvokeOn(statics, staticMethod.Interface, staticMethod.Method, member, call.Arguments, call, outSlot, () =>
                        {
                            // A singleton's GetInstance(): the object the rest of the page talks to.
                            if (staticMethod.Method.ReturnType == className)
                            {
                                AdoptInstance(className, (long)(uint)call.ResultBits, factory);
                            }

                            done();
                        });
                    });
                });
                return;
            }

            if (found is null || call.Target == 0)
            {
                call.Error = $"{member} not on {className}";
                BridgeNote($"{member}: {call.Error}");
                done();
                return;
            }

            (WinmdReader.InterfaceLayout layout, WinmdReader.MethodLayout target) = found.Value;
            WithInterfaceOn(call.Target, layout, pointer =>
            {
                if (pointer == 0)
                {
                    call.Error = $"{layout.FullName} not available";
                    BridgeNote($"{member}: {call.Error}");
                    done();
                    return;
                }

                InvokeOn(pointer, layout, target, member, call.Arguments, call, outSlot, done);
            });
        }

        /// <summary>The interface pointer for <paramref name="layout"/> on any guest object; the page's main object uses the host's own lookup (it can probe for Windows interfaces).</summary>
        private void WithInterfaceOn(long target, WinmdReader.InterfaceLayout layout, Action<long> then)
        {
            if (target == _instance)
            {
                WithInterface(layout, then);
                return;
            }

            if (_bridgeInterfaces.TryGetValue((target, layout.FullName), out long cached))
            {
                then(cached);
                return;
            }

            Guid? iid = layout.Iid ?? (WellKnownIids.TryGetValue(layout.FullName, out Guid known) ? known : null);
            if (iid is null)
            {
                then(0);
                return;
            }

            QueryInterface(target, iid.Value, pointer =>
            {
                _bridgeInterfaces[(target, layout.FullName)] = pointer;
                then(pointer);
            });
        }

        /// <summary>Marshals the arguments by the member's metadata, calls its slot, and keeps the 8-byte out value.</summary>
        private void InvokeOn(long pointer, WinmdReader.InterfaceLayout layout, WinmdReader.MethodLayout target, string member,
            object?[] arguments, BridgeCall call, long outSlot, Action done)
        {
            List<long> core = [];
            List<uint> vfp = [];
            LentArgs lent = new();
            for (int i = 0; i < target.ParameterTypes.Count; i++)
            {
                lent.Index = i;
                Marshal(target.ParameterTypes[i], i < arguments.Length ? arguments[i] : null, core, vfp, lent);
            }

            bool hasResult = target.ReturnType != "Void";
            if (hasResult)
            {
                _emulator.WriteUInt64(outSlot, 0);
                core.Add(outSlot);
            }

            CallSlot($"{layout.FullName.Split('.')[^1]}::{member}", pointer, target.Slot, core, vfp, hr =>
            {
                ReturnLent(lent, arguments);
                call.ResultType = target.ReturnType;
                if (hasResult)
                {
                    call.ResultBits = _emulator.ReadUInt32(outSlot) | ((ulong)_emulator.ReadUInt32(outSlot + 4) << 32);
                    if (target.ReturnType == "String")
                    {
                        long handle = (long)(call.ResultBits & 0xFFFFFFFF);
                        call.ResultText = handle == 0 ? "" : _strings.ReadText(handle);
                    }
                }

                if (hr != 0 && (hr & 0x80000000) != 0)
                {
                    call.Error = $"0x{hr:X8}";
                }

                BridgeNote($"{member}({Describe(arguments)}) = 0x{hr:X8}{(hasResult ? $" -> 0x{call.ResultBits:X}" : "")}");
                done();
            });
        }

        /// <summary>
        /// A C# handler the page subscribed to one of the component's events, as a guest delegate:
        /// its Invoke reads the arguments by the delegate's metadata and calls the handler on the
        /// guest thread, as WinRT raised events on its own thread. A delegate that returns a value
        /// (Asphalt 7's "is this the demo?" callback) has it written through the out pointer that
        /// follows the arguments; dropped, the engine read whatever that word held.
        /// </summary>
        private long ManagedDelegate(string delegateType, Delegate handler)
        {
            WinmdReader.MethodLayout? invoke = _metadata?.DelegateInvoke(delegateType);
            string name = delegateType.Split('.')[^1];
            return CreateUnknownObject($"{name} (managed handler)", ("Invoke", () =>
            {
                object?[] values = [];
                int core = 1, vfp = 0;
                if (invoke is not null)
                {
                    values = new object?[invoke.ParameterTypes.Count];
                    for (int i = 0; i < values.Length; i++)
                    {
                        // Floats travel in VFP registers; everything else takes the next core word.
                        if (invoke.ParameterTypes[i] == "Single")
                        {
                            values[i] = BitConverter.UInt32BitsToSingle(_emulator.Cpu.VfpRead(vfp++));
                            continue;
                        }

                        long word = Arg(core++);
                        values[i] = invoke.ParameterTypes[i] switch
                        {
                            "String" => word == 0 ? "" : _strings.ReadText(word),
                            "Boolean" => (word & 0xFF) != 0,
                            "Int32" => (int)word,
                            "UInt32" => (uint)word,
                            _ => new GuestRef(word),
                        };
                    }
                }

                int count = CallbacksRaised[name] = CallbacksRaised.GetValueOrDefault(name) + 1;
                if (count <= 3)
                {
                    BridgeNote($"event {name} raised to the page");
                }

                object? result = null;
                try
                {
                    result = handler.DynamicInvoke(ConvertForHandler(handler, values));
                }
                catch (Exception ex)
                {
                    BridgeNote($"{name} handler threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
                }

                if (invoke is not null && invoke.ReturnType != "Void" && Arg(core) is long outPointer && outPointer != 0)
                {
                    WriteManagedResult(outPointer, invoke.ReturnType, result);
                }

                Return(HResultOk);
            }));
        }

        /// <summary>Set by the host: a guest pointer as a managed object of the given type (the bridge's wrapper), for callbacks.</summary>
        public Func<long, Type, object?>? WrapGuestObject { get; set; }

        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _managedObjects = new();

        /// <summary>
        /// A managed object implementing one of the component's interfaces, as a guest object: each
        /// method reads its arguments by the metadata, calls the object's implementation of that
        /// interface method on the guest thread, and writes the answer through the out pointer.
        /// The same object always gets the same guest object.
        /// </summary>
        private long ManagedInterfaceObject(string interfaceName, object target)
        {
            if (_managedObjects.TryGetValue(target, out object? known))
            {
                return (long)known;
            }

            WinmdReader.InterfaceLayout? layout = _metadata?.Interface(interfaceName);
            Type? managedInterface = target.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == interfaceName);
            if (layout is null || managedInterface is null)
            {
                BridgeNote($"{target.GetType().Name} as {interfaceName}: {(layout is null ? "not in the metadata" : "does not implement it")}; a placeholder stands in");
                return DefaultAnsweringObject(interfaceName);
            }

            System.Reflection.InterfaceMapping map = target.GetType().GetInterfaceMap(managedInterface);
            var handlers = new Dictionary<int, (string, Action)>();
            foreach (WinmdReader.MethodLayout method in layout.Methods)
            {
                int index = Array.FindIndex(map.InterfaceMethods, m => m.Name == method.Name && m.GetParameters().Length == method.ParameterTypes.Count);
                System.Reflection.MethodInfo? implementation = index < 0 ? null : map.TargetMethods[index];
                string name = $"{interfaceName.Split('.')[^1]}::{method.Name}";
                handlers[method.Slot] = (method.Name, () =>
                {
                    int core = 1, vfp = 0;
                    var values = new object?[method.ParameterTypes.Count];
                    System.Reflection.ParameterInfo[] parameters = implementation?.GetParameters() ?? [];
                    for (int i = 0; i < values.Length; i++)
                    {
                        string type = method.ParameterTypes[i];
                        if (type == "Single")
                        {
                            values[i] = BitConverter.UInt32BitsToSingle(_emulator.Cpu.VfpRead(vfp++));
                            continue;
                        }

                        long word = Arg(core++);
                        Type? wanted = i < parameters.Length ? parameters[i].ParameterType : null;
                        values[i] = type switch
                        {
                            "String" => word == 0 ? "" : _strings.ReadText(word),
                            "Boolean" => (word & 0xFF) != 0,
                            "Int32" => (int)word,
                            "UInt32" => (uint)word,
                            _ => word == 0 || wanted is null ? null : WrapGuestObject?.Invoke(word, wanted),
                        };
                    }

                    int count = CallbacksRaised[name] = CallbacksRaised.GetValueOrDefault(name) + 1;
                    if (count <= 3)
                    {
                        BridgeNote($"{name} called on the page's {target.GetType().Name}");
                    }

                    object? result = null;
                    try
                    {
                        result = implementation?.Invoke(target, values);
                    }
                    catch (Exception ex)
                    {
                        BridgeNote($"{name} threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
                    }

                    if (method.ReturnType != "Void" && Arg(core) is long outPointer && outPointer != 0)
                    {
                        WriteManagedResult(outPointer, method.ReturnType, result);
                    }

                    Return(HResultOk);
                });
            }

            long created = CreateDiscoveryObject(interfaceName, slotCount: layout.Methods.Count, known: handlers);
            _managedObjects.AddOrUpdate(target, created);
            BridgeNote($"the page's {target.GetType().Name} stands in for {interfaceName} (0x{created:X8})");
            return created;
        }

        /// <summary>A managed answer written through a WinRT out pointer, by the metadata's type.</summary>
        private void WriteManagedResult(long outPointer, string type, object? value)
        {
            switch (type)
            {
                case "Boolean":
                    _emulator.WriteMemory(outPointer, [(byte)(value is true ? 1 : 0)]);
                    break;
                case "Single":
                    _emulator.WriteUInt32(outPointer, BitConverter.SingleToUInt32Bits(value is null ? 0f : Convert.ToSingle(value)));
                    break;
                case "Double":
                    _emulator.WriteUInt64(outPointer, BitConverter.DoubleToUInt64Bits(value is null ? 0d : Convert.ToDouble(value)));
                    break;
                case "Int64" or "UInt64":
                    _emulator.WriteUInt64(outPointer, value is null ? 0 : Convert.ToUInt64(value));
                    break;
                case "String":
                    _emulator.WriteUInt32(outPointer, (uint)(value is string text && text.Length > 0 ? _strings.Create(text) : 0));
                    break;
                default:
                    _emulator.WriteUInt32(outPointer, value switch
                    {
                        null => 0,
                        int n => (uint)n,
                        uint n => n,
                        Enum e => (uint)Convert.ToInt32(e),
                        GuestRef guest => (uint)guest.Pointer,
                        _ => 0,
                    });
                    break;
            }
        }

        /// <summary>Hands a delegate the values it was declared with (a guest object it cannot use goes as null).</summary>
        private static object?[] ConvertForHandler(Delegate handler, object?[] values)
        {
            var parameters = handler.Method.GetParameters();
            var converted = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length && i < values.Length; i++)
            {
                converted[i] = values[i] is GuestRef ? null : values[i];
            }

            return converted;
        }
    }
}
