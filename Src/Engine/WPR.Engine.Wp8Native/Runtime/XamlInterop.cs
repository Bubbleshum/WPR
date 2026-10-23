using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>A Windows.Foundation.Size argument: two floats, passed in VFP registers.</summary>
    public readonly record struct SizeF(float Width, float Height);

    /// <summary>One call the managed page makes on the component: a method of the runtime class, by name.</summary>
    /// <param name="Arguments">
    /// float, int, bool, string, <see cref="SizeF"/>, or one of the tokens
    /// <see cref="XamlShell.ManipulationHost"/> / <see cref="XamlShell.Callback"/>.
    /// </param>
    public sealed record XamlShellStep(string Method, params object?[] Arguments)
    {
        /// <summary>
        /// Another runtime class the package registers (WMAppManifest.xml's ActivatableClasses), when
        /// the call is not on the shell's own class - e.g. Spark2's <c>LuaMobileSDK.LuaMobileSDKBridge</c>.
        /// Its DLL is loaded and activated on first use; a factory method (<c>CreateInstance</c>)
        /// makes the instance its later instance calls go to. Null: the shell's class.
        /// </summary>
        public string? Class { get; init; }
    }

    /// <summary>
    /// A C# handler the page subscribes to one of the component's events, as data: what it answers
    /// (for an event whose delegate returns a value) and what it calls back on the component, which
    /// the host does at the next frame - e.g. "the movie finished" when asked to show one.
    /// </summary>
    /// <param name="Returns">bool, int or string; null answers the type's default.</param>
    public sealed record XamlCallback(object? Returns = null, params XamlShellStep[] Then);

    /// <summary>
    /// A managed object the page passes to the component that implements one of the component's
    /// own interfaces - Spark2's <c>CSharpFunctions</c>, the C# half the engine calls back into.
    /// The host supplies it (<c>WinRtRuntime.HostObjectFor</c>).
    /// </summary>
    public sealed record XamlHostObject(string InterfaceName);

    /// <summary>
    /// What a WP8 Direct3D/XAML title's managed page does with its native component - the C#
    /// half of the game, written out as data so the host can play it.
    /// </summary>
    /// <remarks>
    /// These titles ship their game as a WinRT component DLL (C++/CX) and a thin Silverlight
    /// page that activates it, sets a few properties, and hands its content provider to a
    /// <c>DrawingSurfaceBackgroundGrid</c>. The page is a few dozen lines and follows the SDK
    /// template, so rather than running it, the host does what it does: <see cref="Launch"/>
    /// is <c>Application_Launching</c>, <see cref="Loaded"/> is the grid's Loaded handler, and
    /// <c>CreateContentProvider</c> / <c>SetManipulationHost</c> are recognised by name.
    /// </remarks>
    public sealed record XamlShell(
        string ComponentDll,
        string ClassId,
        IReadOnlyList<XamlShellStep> Launch,
        IReadOnlyList<XamlShellStep> Loaded)
    {
        /// <summary>The DrawingSurfaceManipulationHost argument of <c>SetManipulationHost</c>.</summary>
        public const string ManipulationHost = "$manipulationHost";

        /// <summary>A delegate the page would have subscribed (a C# callback); answered by a no-op.</summary>
        public const string Callback = "$callback";

        /// <summary>
        /// The render target the grid asks for: portrait, in physical pixels, as the device reports
        /// it (768x1280 on WXGA, <see cref="Wp8Display"/>).
        /// </summary>
        public SizeF RenderTarget { get; init; } = new(Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight);

        /// <summary>
        /// A static method that hands back the instance (a singleton's <c>GetInstance</c>), used in
        /// place of activating one. Null activates.
        /// </summary>
        public string? InstanceFrom { get; init; }

        /// <summary>
        /// The arguments <see cref="InstanceFrom"/> is called with. A class built through its
        /// activation factory's <c>CreateInstance(...)</c> (an <c>[Activatable(typeof(IFactory))]</c>
        /// runtime class, e.g. Spark2's <c>SparkAppBridge(CSharpFunctions^)</c>) names
        /// <c>CreateInstance</c> there and passes its constructor's arguments here.
        /// </summary>
        public object?[] InstanceArguments { get; init; } = [];

        /// <summary>
        /// Written by <see cref="XamlShellInference"/> from the title's managed shell rather than by
        /// hand: the host says so in its log and crash reports, since a failure may be the guess.
        /// </summary>
        public bool Inferred { get; init; }

        /// <summary>
        /// The page hands its provider to a <c>DrawingSurface</c> (<c>SetContentProvider</c>), not a
        /// <c>DrawingSurfaceBackgroundGrid</c>: the provider is <c>IDrawingSurfaceContentProviderNative</c>,
        /// which makes its own device, draws into a texture of its own and gives it back through
        /// <c>GetTexture</c>, instead of drawing into the host's target. Mandreel (Fruit Ninja,
        /// Jetpack Joyride), Gameloft's WCP titles (Age of Sparta, Uno &amp; Friends) and Microsoft
        /// Bingo are built this way.
        /// </summary>
        public bool DrawingSurface { get; init; }

        /// <summary>
        /// The page is laid out landscape (<c>SupportedOrientations="Landscape"</c>). Only a
        /// <see cref="DrawingSurface"/> sees it: the surface is an element of the page, so it is
        /// landscape too, and so are the size <c>GetTexture</c> is handed, the texture the component
        /// draws and the pointer positions it reads. A background grid is always the portrait screen.
        /// </summary>
        public bool Landscape { get; init; }

        /// <summary>
        /// The component draws into a texture in the page's own orientation, so the host shows it as
        /// it is rather than turning it a quarter like the portrait back buffer.
        /// </summary>
        public bool PresentsUnturned => DrawingSurface && Landscape;

        /// <summary>The page's back-key handler; null when it has none, so a press closes the app.</summary>
        public XamlBackKey? BackKey { get; init; }

        /// <summary>
        /// What the managed shell calls on the component when the app is deactivated:
        /// <c>Application_Deactivated</c>, then the page's <c>OnNavigatedFrom</c>. The host runs
        /// them before the grid disconnects the content provider (<c>WinRtRuntime.DeliverLifecycle</c>,
        /// AppLifecycle.cs).
        /// </summary>
        public IReadOnlyList<XamlShellStep> Deactivated { get; init; } = [];

        /// <summary>
        /// What the managed shell calls on the component when the app comes back with its instance
        /// preserved (a fast app switch): <c>Application_Activated</c> with
        /// <c>IsApplicationInstancePreserved</c> true, then the page's <c>OnNavigatedTo</c>
        /// (NavigationMode.Back). Run before the grid reconnects the content provider.
        /// </summary>
        public IReadOnlyList<XamlShellStep> Activated { get; init; } = [];

        /// <summary>
        /// The class is Mandreel's interop, whose page runs a thread that answers the component's
        /// messages (MandreelMessages.cs); the host starts its own before <c>CreateContentProvider</c>.
        /// </summary>
        public bool MandreelMessages { get; init; }
    }

    public sealed partial class WinRtRuntime
    {
        /// <summary>The XAML host's own log: every step, every QueryInterface, in order.</summary>
        public List<string> ComponentLog { get; } = new();

        /// <summary>Frames the content provider has drawn.</summary>
        public int ComponentFrames { get; private set; }

        private WinmdReader? _metadata;
        private XamlShell? _shell;
        private long _instance;
        private long _factory;
        private readonly Dictionary<string, long> _staticInterfaces = new(StringComparer.Ordinal);
        private readonly Queue<XamlShellStep> _followUps = new();
        private readonly Dictionary<string, long> _instanceInterfaces = new(StringComparer.Ordinal);
        private long _provider;
        private long _providerNative;
        private long _manipulationHost;
        private long _hostDevice, _hostContext, _hostTarget;
        private long _scratch;
        private IReadOnlyList<Guid> _iidCandidates = [];
        private PeImage? _componentImage;

        // IDrawingSurfaceBackgroundContentProviderNative: IUnknown 0-2, then Connect,
        // Disconnect, PrepareResources, Draw.
        private const int SlotProviderConnect = 3;
        private const int SlotProviderPrepareResources = 5;
        private const int SlotProviderDraw = 6;

        /// <summary>
        /// Begins hosting a component title. The emulator must then be run from
        /// <paramref name="image"/>'s entry point (or <c>DllGetActivationFactory</c> when it has
        /// none); everything after is a chain of host-to-emulated calls that ends in the frame
        /// loop and never returns.
        /// </summary>
        /// <returns>The address to start the CPU at.</returns>
        public long StartComponent(PeImage image, WinmdReader metadata, XamlShell shell)
        {
            _metadata = metadata;
            _shell = shell;
            _componentImage = image;
            // The component reads the raw (portrait) position and rotates it itself from the
            // Orientation the page set, exactly like an exe title: the default rotation is right.
            // A DrawingSurface on a landscape page is the exception: its pointer positions are
            // relative to the element, which is landscape (Jetpack Joyride, Fruit Ninja).
            PointerRotation = shell.PresentsUnturned ? "none" : null;
            _scratch = _emulator.AllocateHeap(256);
            _iidCandidates = HarvestIidCandidates(image, metadata);
            Log($"{_iidCandidates.Count} candidate IID(s) harvested from {shell.ComponentDll}");

            if (!image.Exports.TryGetValue("DllGetActivationFactory", out uint factoryRva))
            {
                throw new InvalidOperationException($"{shell.ComponentDll} exports no DllGetActivationFactory");
            }

            long getFactory = image.ImageBase + factoryRva;

            void Activate()
            {
                long classId = _strings.Create(shell.ClassId);
                long outFactory = _scratch;
                _emulator.WriteUInt32(outFactory, 0);
                Call("DllGetActivationFactory", getFactory, [classId, outFactory], [], hr =>
                {
                    long factory = _emulator.ReadUInt32(outFactory);
                    Log($"DllGetActivationFactory({shell.ClassId}) = 0x{hr:X8}, factory 0x{factory:X8}");
                    if (factory == 0)
                    {
                        Fail("no activation factory");
                        return;
                    }

                    _factory = factory;
                    if (shell.InstanceFrom is { } instanceFrom)
                    {
                        InvokeMember(instanceFrom, shell.InstanceArguments, instance =>
                        {
                            _instance = instance;
                            Log($"{instanceFrom}() = instance 0x{_instance:X8}");
                            if (_instance == 0)
                            {
                                Fail($"{instanceFrom} gave nothing");
                                return;
                            }

                            RunSteps(shell.Launch, 0, () => RunSteps(shell.Loaded, 0, ConnectProvider));
                        });
                        return;
                    }

                    // IActivationFactory::ActivateInstance(IInspectable**) is slot 6.
                    long outInstance = _scratch + 4;
                    _emulator.WriteUInt32(outInstance, 0);
                    CallSlot("ActivateInstance", factory, 6, [outInstance], [], hr2 =>
                    {
                        _instance = _emulator.ReadUInt32(outInstance);
                        Log($"ActivateInstance = 0x{hr2:X8}, instance 0x{_instance:X8}");
                        if (_instance == 0)
                        {
                            Fail("ActivateInstance gave nothing");
                            return;
                        }

                        RunSteps(shell.Launch, 0, () => RunSteps(shell.Loaded, 0, ConnectProvider));
                    });
                });
            }

            // DllMain runs the CRT initialisers and static constructors; a DLL without an entry
            // point has none to run.
            if (image.EntryPointRva != 0)
            {
                _emulator.CallEmulated("DllMain", image.EntryPoint, [image.ImageBase, 1, 0], onReturn: () =>
                {
                    Log($"DllMain(PROCESS_ATTACH) returned {Arg(0)}");
                    Activate();
                });

                // The package DLLs the component imports are initialised before it, as the loader would.
                return _emulator.StartWithModuleInitialisers(image.EntryPoint);
            }

            Activate();
            return _emulator.StartWithModuleInitialisers(getFactory);
        }

        private void Log(string line) => ComponentLog.Add(line);

        private void Fail(string why)
        {
            Log("STOPPED: " + why);
            _emulator.Stop($"XAML host: {why}");
        }

        // -----------------------------------------------------------------------------
        // The page's steps
        // -----------------------------------------------------------------------------

        private void RunSteps(IReadOnlyList<XamlShellStep> steps, int index, Action done)
        {
            if (index >= steps.Count)
            {
                done();
                return;
            }

            XamlShellStep step = steps[index];
            if (step.Method == "CreateContentProvider" && _shell!.MandreelMessages && !_mandreelStarted)
            {
                StartMandreelMessageThread(() => RunSteps(steps, index, done));
                return;
            }

            InvokeMember(step.Method, step.Arguments, step.Class, result =>
            {
                if (step.Method == "CreateContentProvider")
                {
                    _provider = result;
                    Log($"content provider 0x{_provider:X8}");
                }

                RunSteps(steps, index + 1, done);
            });
        }

        /// <summary>Calls a method of the runtime class by name, marshalling by its metadata.</summary>
        private static readonly WinmdReader.InterfaceLayout ManipulationHandlerLayout = new(
            "Windows.Phone.Input.Interop.IDrawingSurfaceManipulationHandler",
            [new WinmdReader.MethodLayout("SetManipulationHost", WinmdReader.InspectableSlots, ["Windows.Phone.Input.Interop.DrawingSurfaceManipulationHost"], "Void")]);

        private void InvokeMember(string method, object?[] arguments, Action<long> then) => InvokeMember(method, arguments, null, then);

        private void InvokeMember(string method, object?[] arguments, string? onClass, Action<long> then)
        {
            if (onClass is not null && onClass != _shell!.ClassId)
            {
                InvokePackageMember(onClass, method, arguments, then);
                return;
            }

            string className = _shell!.ClassId;
            if (_metadata is null)
            {
                Log($"{method}: no component metadata; skipped");
                then(0);
                return;
            }

            var found = _metadata.FindMethod(className, method);
            if (found is null && method == "SetManipulationHost")
            {
                // Windows.Phone.Input.Interop.IDrawingSurfaceManipulationHandler is a Windows
                // interface, so the component's metadata names it but does not define it: one
                // method, SetManipulationHost(DrawingSurfaceManipulationHost), at slot 6.
                found = (ManipulationHandlerLayout, ManipulationHandlerLayout.Methods[0]);
            }

            bool isStatic = false;
            if (found is null && _metadata.FindStaticMethod(className, method) is { } staticMethod)
            {
                found = staticMethod;
                isStatic = true;
            }

            if (found is null)
            {
                Log($"{method}: not on {className}; skipped");
                then(0);
                return;
            }

            (WinmdReader.InterfaceLayout layout, WinmdReader.MethodLayout target) = found.Value;
            Action<WinmdReader.InterfaceLayout, Action<long>> resolve = isStatic ? WithStaticInterface : WithInterface;
            resolve(layout, pointer =>
            {
                if (pointer == 0)
                {
                    Log($"{method}: {layout.FullName} not available; skipped");
                    then(0);
                    return;
                }

                CallMember(layout, target, pointer, method, arguments, then);
            });
        }

        /// <summary>Marshals <paramref name="arguments"/> by the member's metadata and calls its slot on <paramref name="pointer"/>.</summary>
        private void CallMember(WinmdReader.InterfaceLayout layout, WinmdReader.MethodLayout target, long pointer, string method, object?[] arguments, Action<long> then)
        {
            List<long> core = [];
            List<uint> vfp = [];
            for (int i = 0; i < target.ParameterTypes.Count; i++)
            {
                object? value = i < arguments.Length ? arguments[i] : null;
                Marshal(target.ParameterTypes[i], value, core, vfp);
            }

            bool hasResult = target.ReturnType != "Void";
            long outSlot = _scratch + 32;
            if (hasResult)
            {
                _emulator.WriteUInt64(outSlot, 0);
                core.Add(outSlot);
            }

            CallSlot($"{layout.FullName.Split('.')[^1]}::{method}", pointer, target.Slot, core, vfp, hr =>
            {
                long result = hasResult ? _emulator.ReadUInt32(outSlot) : 0;
                Log($"{method}({Describe(arguments)}) = 0x{hr:X8}{(hasResult ? $" -> 0x{result:X8}" : "")}");
                then(result);
            });
        }

        /// <summary>A package runtime class the page uses besides its own: its factory, the instance it made, and the interfaces asked for.</summary>
        private sealed class PackageObject(long factory)
        {
            public long Factory { get; } = factory;

            public long Instance { get; set; }

            public Dictionary<string, long> Interfaces { get; } = new(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, PackageObject> _packageObjects = new(StringComparer.Ordinal);

        /// <summary>The instance a package class's factory member made for the page; 0 when none.</summary>
        public long PackageInstance(string className) => _packageObjects.TryGetValue(className, out PackageObject? obj) ? obj.Instance : 0;

        /// <summary>
        /// A member of another runtime class the package registers, as the page calls it: the class's
        /// DLL activated on first use (<see cref="ActivatePackageFactory"/>), a static or factory
        /// member through the factory's interface for it, an instance member through the instance a
        /// factory member made. Anything unavailable is logged and skipped, as for the shell's class.
        /// </summary>
        private void InvokePackageMember(string className, string method, object?[] arguments, Action<long> then)
        {
            ActivatePackageFactory(className, factory =>
            {
                if (factory == 0)
                {
                    Log($"{className}.{method}: no activation factory; skipped");
                    then(0);
                    return;
                }

                if (!_packageObjects.TryGetValue(className, out PackageObject? obj))
                {
                    _packageObjects[className] = obj = new PackageObject(factory);
                }

                bool isStatic = false;
                var found = _metadata!.FindMethod(className, method);
                if (found is null && _metadata.FindStaticMethod(className, method) is { } staticMethod)
                {
                    found = staticMethod;
                    isStatic = true;
                }

                if (found is null)
                {
                    Log($"{className}.{method}: not in the package metadata; skipped");
                    then(0);
                    return;
                }

                (WinmdReader.InterfaceLayout layout, WinmdReader.MethodLayout target) = found.Value;
                long owner = isStatic ? obj.Factory : obj.Instance;
                if (owner == 0 || layout.Iid is not { } iid)
                {
                    Log($"{className}.{method}: {(owner == 0 ? "no instance yet" : $"{layout.FullName} has no IID")}; skipped");
                    then(0);
                    return;
                }

                string key = (isStatic ? "static " : "") + layout.FullName;
                void Invoke(long pointer)
                {
                    if (pointer == 0)
                    {
                        Log($"{className}.{method}: {layout.FullName} not available; skipped");
                        then(0);
                        return;
                    }

                    CallMember(layout, target, pointer, method, arguments, result =>
                    {
                        // A factory member (CreateInstance(...)) made the object later calls go to.
                        if (isStatic && obj.Instance == 0 && result != 0 && (target.ReturnType == className || method == "CreateInstance"))
                        {
                            obj.Instance = result;
                            Log($"{className}: instance 0x{result:X8}");
                        }

                        then(result);
                    });
                }

                if (obj.Interfaces.TryGetValue(key, out long cached))
                {
                    Invoke(cached);
                    return;
                }

                QueryInterface(owner, iid, pointer =>
                {
                    obj.Interfaces[key] = pointer;
                    Invoke(pointer);
                });
            });
        }

        private static string Describe(object?[] arguments)
            => string.Join(", ", arguments.Select(a => a switch
            {
                null => "null",
                SizeF s => $"{s.Width}x{s.Height}",
                string t => $"\"{t}\"",
                _ => a.ToString(),
            }));

        private void Marshal(string type, object? value, List<long> core, List<uint> vfp)
        {
            switch (type)
            {
                case "Single":
                    vfp.Add(BitConverter.SingleToUInt32Bits(Convert.ToSingle(value ?? 0f)));
                    break;
                case "Windows.Foundation.Size":
                case "Windows.Foundation.Point":
                    SizeF size = value is SizeF s ? s : default;
                    vfp.Add(BitConverter.SingleToUInt32Bits(size.Width));
                    vfp.Add(BitConverter.SingleToUInt32Bits(size.Height));
                    break;
                case "String":
                    core.Add(value is string text && text.Length > 0 ? _strings.Create(text) : 0);
                    break;
                case "Boolean":
                    core.Add(value is true ? 1 : 0);
                    break;
                default:
                    core.Add(value switch
                    {
                        XamlShell.ManipulationHost => ManipulationHostObject(),
                        XamlShell.Callback => CallbackDelegate(type, new XamlCallback()),
                        XamlCallback callback => CallbackDelegate(type, callback),
                        XamlHostObject hosted => HostObjectFor(hosted.InterfaceName),
                        int n => n,
                        uint n => n,
                        long n => n,
                        _ => 0,
                    });
                    break;
            }
        }

        // -----------------------------------------------------------------------------
        // QueryInterface, by IID from metadata or by probing harvested candidates
        // -----------------------------------------------------------------------------

        private void WithInterface(WinmdReader.InterfaceLayout layout, Action<long> then)
        {
            if (_instanceInterfaces.TryGetValue(layout.FullName, out long cached))
            {
                then(cached);
                return;
            }

            void Remember(long pointer)
            {
                _instanceInterfaces[layout.FullName] = pointer;
                then(pointer);
            }

            if (layout.Iid is { } iid)
            {
                QueryInterface(_instance, iid, Remember);
                return;
            }

            // A Windows interface (IDrawingSurfaceManipulationHandler): its IID is not in the
            // component's metadata. The object implements exactly one IInspectable interface with
            // this many methods that the metadata does not explain, and the candidates harvested
            // from the DLL's IID table include its IID; probe them.
            int wantedSlots = WinmdReader.InspectableSlots + layout.Methods.Count;
            void Probe() => ProbeCandidates(_instance, 0, wantedSlots, exclude: KnownInstanceIids(), found =>
            {
                Log($"{layout.FullName}: {(found.Pointer == 0 ? "no candidate answered" : $"IID {found.Iid} -> 0x{found.Pointer:X8}")}");
                Remember(found.Pointer);
            }, atLeast: true);

            // The Windows IIDs are fixed, so ask for the known one first. Probing is the fallback,
            // and it can be fooled: Modern Combat 4's instance answered an unrelated harvested IID
            // with its main 64-slot vtable, which passed the "at least" check, and
            // SetManipulationHost then called some other method - the game never subscribed to
            // touch and its title screen ignored every tap.
            if (WellKnownIids.TryGetValue(layout.FullName, out Guid known))
            {
                QueryInterface(_instance, known, pointer =>
                {
                    if (pointer == 0)
                    {
                        Probe();
                        return;
                    }

                    Log($"{layout.FullName}: IID {known} -> 0x{pointer:X8}");
                    Remember(pointer);
                });
                return;
            }

            Probe();
        }

        /// <summary>Windows interfaces the component implements but its metadata only names.</summary>
        private static readonly Dictionary<string, Guid> WellKnownIids = new(StringComparer.Ordinal)
        {
            ["Windows.Phone.Input.Interop.IDrawingSurfaceManipulationHandler"] = new("171b7ef7-a96f-4e37-b936-7a0b10f35163"),
        };

        /// <summary>IDrawingSurfaceBackgroundContentProviderNative, the same in every title seen.</summary>
        private static readonly Guid BackgroundContentProviderNativeIid = new("262c1892-975a-45aa-8fe7-f25c2c4088e3");

        /// <summary>IDrawingSurfaceContentProviderNative (a <c>DrawingSurface</c>'s provider), found by probing Fruit Ninja's.</summary>
        private static readonly Guid SurfaceContentProviderNativeIid = new("c8377af0-7a69-41d4-a322-8f60e9a0acf8");

        /// <summary>A statics interface, from the activation factory by its IID.</summary>
        private void WithStaticInterface(WinmdReader.InterfaceLayout layout, Action<long> then)
        {
            if (_staticInterfaces.TryGetValue(layout.FullName, out long cached))
            {
                then(cached);
                return;
            }

            if (_factory == 0 || layout.Iid is not { } iid)
            {
                then(0);
                return;
            }

            QueryInterface(_factory, iid, pointer =>
            {
                _staticInterfaces[layout.FullName] = pointer;
                then(pointer);
            });
        }

        private HashSet<Guid> KnownInstanceIids()
        {
            HashSet<Guid> known = [ClosableIid];
            foreach (WinmdReader.InterfaceLayout layout in _metadata!.InterfacesOf(_shell!.ClassId))
            {
                if (layout.Iid is { } iid)
                {
                    known.Add(iid);
                }
            }

            return known;
        }

        /// <summary>Windows.Foundation.IClosable, which every C++/CX class with a destructor implements.</summary>
        private static readonly Guid ClosableIid = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");

        /// <param name="atLeast">
        /// Accept a longer vtable. Counting stops at the next vtable's complete-object-locator,
        /// but in C++/CX the next vtable can follow with no data between, so a one-method
        /// interface can count as two.
        /// </param>
        private void ProbeCandidates(long target, int index, int wantedSlots, HashSet<Guid> exclude, Action<(Guid Iid, long Pointer)> then, bool atLeast = false)
        {
            while (index < _iidCandidates.Count && exclude.Contains(_iidCandidates[index]))
            {
                index++;
            }

            if (index >= _iidCandidates.Count)
            {
                then((Guid.Empty, 0));
                return;
            }

            Guid candidate = _iidCandidates[index];
            QueryInterface(target, candidate, pointer =>
            {
                if (pointer != 0)
                {
                    Log($"   probe {candidate}: 0x{pointer:X8}, {VtableLength(pointer)} code slot(s)");
                }

                int length = pointer == 0 ? 0 : VtableLength(pointer);
                if (pointer != 0 && (length == wantedSlots || (atLeast && length > wantedSlots)))
                {
                    then((candidate, pointer));
                    return;
                }

                ProbeCandidates(target, index + 1, wantedSlots, exclude, then, atLeast);
            });
        }

        /// <summary>How many consecutive vtable entries point at code.</summary>
        /// <remarks>
        /// MSVC puts the complete-object-locator pointer - a data address - in front of every
        /// vtable, so the count stops at the start of the next one.
        /// </remarks>
        private int VtableLength(long interfacePointer)
        {
            long vtable = _emulator.ReadUInt32(interfacePointer, 0);
            int count = 0;
            while (count < 64 && _emulator.IsExecutableCode(_emulator.ReadUInt32(vtable + (count * 4), 0)))
            {
                count++;
            }

            return count;
        }

        private void QueryInterface(long target, Guid iid, Action<long> then)
        {
            long iidAddress = _scratch + 64;
            long outPointer = _scratch + 80;
            _emulator.WriteMemory(iidAddress, iid.ToByteArray());
            _emulator.WriteUInt32(outPointer, 0);
            CallSlot("QueryInterface", target, 0, [iidAddress, outPointer], [], hr =>
                then(hr == 0 ? _emulator.ReadUInt32(outPointer) : 0));
        }

        /// <summary>
        /// GUID-shaped values near the IIDs the metadata names: C++/CX keeps every IID a class
        /// implements in one table, so the Windows interfaces sit beside the component's own.
        /// </summary>
        private static IReadOnlyList<Guid> HarvestIidCandidates(PeImage image, WinmdReader metadata)
        {
            ReadOnlySpan<byte> raw = image.Raw;
            HashSet<Guid> found = [];
            List<Guid> ordered = [];
            foreach (string name in metadata.InterfaceNames)
            {
                if (metadata.Interface(name)?.Iid is not { } iid)
                {
                    continue;
                }

                int at = raw.IndexOf(iid.ToByteArray());
                if (at < 0)
                {
                    continue;
                }

                for (int offset = Math.Max(0, at - 1024); offset < Math.Min(raw.Length - 16, at + 1024); offset += 4)
                {
                    var candidate = new Guid(raw.Slice(offset, 16));
                    if (LooksLikeIid(raw.Slice(offset, 16)) && found.Add(candidate))
                    {
                        ordered.Add(candidate);
                    }
                }
            }

            return ordered;
        }

        /// <summary>RFC 4122 variant with a version of 1-5, or COM's own {xxxxxxxx-0000-0000-C000-000000000046}.</summary>
        private static bool LooksLikeIid(ReadOnlySpan<byte> bytes)
        {
            int version = bytes[7] >> 4;
            bool rfc = (bytes[8] & 0xC0) == 0x80 && version is >= 1 and <= 5;
            bool com = bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 && bytes[8] == 0xC0 && bytes[15] == 0x46;
            return rfc || com;
        }

        // -----------------------------------------------------------------------------
        // The DrawingSurfaceBackgroundGrid side: connect the provider, then draw forever
        // -----------------------------------------------------------------------------

        private void ConnectProvider()
        {
            if (_provider == 0)
            {
                Fail("the page never produced a content provider");
                return;
            }

            if (_shell!.DrawingSurface)
            {
                // IDrawingSurfaceContentProviderNative: IUnknown plus Connect, Disconnect,
                // PrepareResources, GetTexture - seven slots, like the background one, whose IID is
                // the one to rule out.
                // The pointer at the object's start is its IInspectable face (IDrawingSurfaceContentProvider,
                // six slots that can count as seven), so an answer that hands it back is ruled out too.
                HashSet<Guid> ruledOut = [BackgroundContentProviderNativeIid];
                void Probe() => ProbeCandidates(_provider, 0, wantedSlots: 7, exclude: ruledOut, found =>
                {
                    if (found.Pointer == _provider && ruledOut.Add(found.Iid))
                    {
                        Probe();
                        return;
                    }

                    ConnectProviderNative(found);
                });
                QueryInterface(_provider, SurfaceContentProviderNativeIid, pointer =>
                {
                    if (pointer != 0)
                    {
                        ConnectProviderNative((SurfaceContentProviderNativeIid, pointer));
                        return;
                    }

                    Probe();
                });
                return;
            }

            // IDrawingSurfaceBackgroundContentProviderNative: IUnknown plus four methods. The known
            // IID first; probing for a seven-slot interface is the fallback.
            QueryInterface(_provider, BackgroundContentProviderNativeIid, pointer =>
            {
                if (pointer != 0)
                {
                    ConnectProviderNative((BackgroundContentProviderNativeIid, pointer));
                    return;
                }

                ProbeCandidates(_provider, 0, wantedSlots: 7, exclude: [], ConnectProviderNative);
            });
        }

        private void ConnectProviderNative((Guid Iid, long Pointer) found)
        {
            {
                _providerNative = found.Pointer;
                Log($"content provider native interface: {(found.Pointer == 0 ? "NOT FOUND" : $"IID {found.Iid} -> 0x{found.Pointer:X8}")}");
                if (_providerNative == 0)
                {
                    Fail("no IDrawingSurfaceBackgroundContentProviderNative");
                    return;
                }

                _runtimeHost = RuntimeHostObject();
                if (_shell!.DrawingSurface)
                {
                    // Connect(IDrawingSurfaceRuntimeHostNative*): the component makes its own device
                    // and hands its texture to CreateSynchronizedTexture.
                    CallSlot("SurfaceProvider::Connect", _providerNative, SlotProviderConnect, [_runtimeHost], [], hr =>
                    {
                        Log($"Connect = 0x{hr:X8}; drawing (DrawingSurface)");
                        NextFrame();
                    });
                    return;
                }

                (_hostDevice, _hostContext) = _emulator.Direct3D.CreateHostDevice();
                _hostTarget = _emulator.Direct3D.CreateHostRenderTargetView();

                CallSlot("Provider::Connect", _providerNative, SlotProviderConnect, [_runtimeHost, _hostDevice], [], hr =>
                {
                    Log($"Connect = 0x{hr:X8}; drawing");
                    NextFrame();
                });
            }
        }

        private void NextFrame()
        {
            // Background work the game queued runs between frames - the closest a single CPU
            // gets to the thread pool it was written for - then audio, then the other guest
            // threads, then any input that is due. Audio comes before the threads so a mixer
            // thread woken by OnBufferEnd refills in this frame rather than the next: with two
            // 25 ms buffers queued (Modern Combat 4) a frame's wait ran the stream dry.
            void ThreadsThenInput() => _emulator.RunOtherThreads(InputThenDraw);

            QueueDueTimers();

            _emulator.DrainDeferredCalls(() =>
            {
                if (!_emulator.XAudio2.Pump(ThreadsThenInput))
                {
                    ThreadsThenInput();
                }
            });
        }

        private void InputThenDraw()
        {
            // Calls the page's handlers make back into the component, a frame after the event.
            if (_followUps.Count > 0)
            {
                XamlShellStep[] steps = [.. _followUps];
                _followUps.Clear();
                RunSteps(steps, 0, InputThenDraw);
                return;
            }

            if (DeliverLifecycle(InputThenDraw))
            {
                return;
            }

            if (_providerDisconnected)
            {
                // In the background the grid has disconnected the provider: nothing is drawn and
                // no input reaches a page that is not on screen. The loop still turns - paced by
                // the host's tick, so it does not spin - because a Suspending deferral and the
                // return to the foreground are only delivered from here.
                // A turn that draws nothing usually comes straight back here without going through
                // the guest, so the turns are run as a loop, not by recursion: one turn per host tick
                // grew the stack for the whole time the app was away, and once the host stopped
                // pacing it (closing) it overflowed in a moment (Amazing Alex, suspended at exit).
                IdleTurn?.Invoke();
                if (_inIdleTurn)
                {
                    _idleTurnAgain = true;
                    return;
                }

                _inIdleTurn = true;
                try
                {
                    do
                    {
                        _idleTurnAgain = false;
                        NextFrame();
                    }
                    while (_idleTurnAgain);
                }
                finally
                {
                    _inIdleTurn = false;
                }

                return;
            }

            if (DeliverBack(PrepareAndDraw) ||
                DeliverSensors(InputThenDraw) ||
                DeliverKeys(InputThenDraw) ||
                (_manipulationHost != 0 && DeliverInput(PrepareAndDraw, _manipulationHost)))
            {
                return;
            }

            PrepareAndDraw();
        }

        /// <summary>
        /// Raised on the guest's thread for each turn of a XAML title's frame loop that draws nothing
        /// (the provider is disconnected while the app is in the background). The host waits for its
        /// next tick here, as it does after a frame, so the loop is paced rather than spinning.
        /// </summary>
        public event Action? IdleTurn;

        /// <summary>Whether the grid has disconnected the content provider (the app is in the background).</summary>
        public bool ProviderDisconnected => _providerDisconnected;

        private bool _providerDisconnected;

        /// <summary>The idle-turn loop in <see cref="InputThenDraw"/> is on the stack.</summary>
        private bool _inIdleTurn;

        /// <summary>A turn re-entered the idle path synchronously: the loop runs another.</summary>
        private bool _idleTurnAgain;
        private long _runtimeHost;

        // IDrawingSurfaceBackgroundContentProviderNative::Disconnect.
        private const int SlotProviderDisconnect = 4;

        /// <summary>A XAML component is hosted and its provider connected, so the lifecycle has page and grid steps to add.</summary>
        private bool HostsXamlComponent => _shell is not null && _providerNative != 0;

        /// <summary>
        /// The grid leaving the screen: <c>IDrawingSurfaceBackgroundContentProviderNative::Disconnect()</c>.
        /// No frame is drawn until <see cref="ReconnectProvider"/>.
        /// </summary>
        private void DisconnectProvider(Action then)
        {
            _providerDisconnected = true;
            CallProviderSlot("Provider::Disconnect", SlotProviderDisconnect, [], then);
        }

        /// <summary>
        /// The grid back on screen: <c>Connect(host, device)</c> again. WP8 handed over a new device
        /// here, because the old one was lost; the host's device never is, so the same one goes back.
        /// A component that recreates its resources on Connect does so against a device that still
        /// works, and one that compares pointers keeps what it has. Both are right here.
        /// </summary>
        private void ReconnectProvider(Action then) =>
            CallProviderSlot("Provider::Connect", SlotProviderConnect, _shell!.DrawingSurface ? [_runtimeHost] : [_runtimeHost, _hostDevice], () =>
            {
                _providerDisconnected = false;
                then();
            });

        /// <summary>One provider call made from the lifecycle. A slot with no code is logged and skipped rather than stopping the guest.</summary>
        private void CallProviderSlot(string name, int slot, IReadOnlyList<long> core, Action then)
        {
            long function = _emulator.ReadUInt32(_emulator.ReadUInt32(_providerNative, 0) + (slot * 4), 0);
            if (!_emulator.IsExecutableCode(function))
            {
                Log($"{name}: 0x{function:X8} is not code; skipped");
                _appLifecycleLog.Add($"   {name}: no code");
                then();
                return;
            }

            _appLifecycleLog.Add($"   {name}");
            _emulator.CallEmulated(name, function, [_providerNative, .. core], onReturn: () =>
            {
                long hr = Arg(0) & 0xFFFFFFFFL;
                Log($"{name} = 0x{hr:X8}");
                if (hr != HResultOk && (hr & 0x80000000L) != 0)
                {
                    _appLifecycleLog.Add($"   {name} returned 0x{hr:X8}");
                }

                then();
            }, recycle: true);
        }

        private void PrepareAndDraw()
        {
            // PrepareResources(const LARGE_INTEGER* presentTargetTime, DrawingSurfaceSizeF* desiredRenderTargetSize)
            // A drawn frame is a turn round the main loop: it is what moves the guest clock
            // (HostStubs.Microseconds counts these), and an exe title's ProcessEvents is the
            // only other thing that does. Without it time stood still and Amazing Alex's splash,
            // waiting for its seconds to pass, never ended.
            ProcessEventsCalls++;

            // In QueryPerformanceCounter units (1 MHz here), one frame ahead.
            long time = _scratch + 96;
            long size = _scratch + 104;
            _emulator.WriteUInt64(time, (ulong)((ProcessEventsCalls + 1) * 16_667));
            // A DrawingSurface on a landscape page measures landscape: 1280x768 on WXGA where the
            // grid's render target is 768x1280.
            (float surfaceWidth, float surfaceHeight) = _shell!.PresentsUnturned
                ? (_shell.RenderTarget.Height, _shell.RenderTarget.Width)
                : (_shell.RenderTarget.Width, _shell.RenderTarget.Height);
            _emulator.WriteMemory(size, [
                .. BitConverter.GetBytes(surfaceWidth),
                .. BitConverter.GetBytes(surfaceHeight),
            ]);

            if (_shell.DrawingSurface)
            {
                PrepareAndGetTexture(time, size);
                return;
            }

            CallSlot("Provider::PrepareResources", _providerNative, SlotProviderPrepareResources, [time, size], [], _ =>
            {
                CallSlot("Provider::Draw", _providerNative, SlotProviderDraw, [_hostDevice, _hostContext, _hostTarget], [], hr =>
                {
                    ComponentFrames++;
                    if (ComponentFrames == 1)
                    {
                        Log($"first Draw returned 0x{hr:X8}");
                    }

                    _emulator.Direct3D.DeliverFrame();
                    NextFrame();
                });
            });
        }

        /// <summary>
        /// A <c>DrawingSurface</c> frame: <c>PrepareResources(presentTargetTime*, BOOL* contentDirty)</c>,
        /// then <c>GetTexture(DrawingSurfaceSizeF* surfaceSize, IDrawingSurfaceSynchronizedTextureNative**,
        /// DrawingSurfaceRectF* textureSubRectangle)</c>, inside which the component draws into the
        /// texture it gave <c>CreateSynchronizedTexture</c> - the frame the host shows.
        /// </summary>
        private void PrepareAndGetTexture(long time, long size)
        {
            long dirty = _scratch + 112;
            long synchronized = _scratch + 116;
            long rectangle = _scratch + 120; // left, top, right, bottom: 16 bytes
            _emulator.WriteUInt32(dirty, 0);
            _emulator.WriteUInt32(synchronized, 0);
            CallSlot("SurfaceProvider::PrepareResources", _providerNative, SlotProviderPrepareResources, [time, dirty], [], _ =>
            {
                CallSlot("SurfaceProvider::GetTexture", _providerNative, SlotProviderDraw, [size, synchronized, rectangle], [], hr =>
                {
                    ComponentFrames++;
                    if (ComponentFrames == 1)
                    {
                        Log($"first GetTexture returned 0x{hr:X8}, texture 0x{_emulator.ReadUInt32(synchronized):X8}");
                    }

                    _emulator.Direct3D.DeliverFrame();
                    NextFrame();
                });
            });
        }

        /// <summary>
        /// IDrawingSurfaceRuntimeHostNative: IUnknown, then two methods - RequestAdditionalFrame() and
        /// CreateSynchronizedTexture(ID3D11Texture2D*, IDrawingSurfaceSynchronizedTextureNative**),
        /// the second called only by a <c>DrawingSurface</c> component.
        /// </summary>
        /// <remarks>
        /// <para>Their order is not in any metadata this host can read, so both slots answer both:
        /// a call whose first argument is a resource the device made is CreateSynchronizedTexture,
        /// anything else RequestAdditionalFrame. (Background titles call slot 3 with no arguments;
        /// Fruit Ninja's Mandreel host hands its texture to slot 3 as well.)</para>
        /// <para>Every frame is drawn anyway: the host never stops asking.</para>
        /// </remarks>
        private long RuntimeHostObject() => CreateUnknownObject(
            "IDrawingSurfaceRuntimeHostNative",
            ("RequestAdditionalFrame|CreateSynchronizedTexture", RuntimeHostCall),
            ("CreateSynchronizedTexture|RequestAdditionalFrame", RuntimeHostCall));

        private int _runtimeHostCalls;

        private void RuntimeHostCall()
        {
            long texture = Arg(1);
            long outSynchronized = Arg(2);
            if (!_emulator.Direct3D.IsResource(texture))
            {
                if (_runtimeHostCalls++ < 3)
                {
                    Log($"runtime host: RequestAdditionalFrame (r1=0x{texture:X8} r2=0x{outSynchronized:X8})");
                }

                Return(HResultOk); // RequestAdditionalFrame
                return;
            }

            _emulator.Direct3D.AdoptPresentationTexture(texture);
            Log($"CreateSynchronizedTexture(0x{texture:X8}): the component's texture is the screen");
            if (outSynchronized != 0)
            {
                // IDrawingSurfaceSynchronizedTextureNative: BeginDraw, EndDraw. One GPU here, so
                // there is nothing to synchronise.
                _emulator.WriteUInt32(outSynchronized, (uint)CreateUnknownObject(
                    "IDrawingSurfaceSynchronizedTextureNative",
                    ("BeginDraw", () => Return(HResultOk)),
                    ("EndDraw", () => Return(HResultOk))));
            }

            Return(HResultOk);
        }

        /// <summary>
        /// An object whose interface derives from IUnknown rather than IInspectable: its own
        /// methods start at slot 3. <see cref="CreateDiscoveryObject"/> fixes slots 3-5 as
        /// IInspectable's, which aimed every RequestAdditionalFrame at GetIids.
        /// </summary>
        private long CreateUnknownObject(string interfaceName, params (string Name, Action Handler)[] methods)
        {
            long instance = _emulator.AllocateHeap(8);
            (string Name, Action Handler)[] all =
            [
                ("QueryInterface", () => QueryInterface(instance)),
                ("AddRef", () => AdjustRefCount(instance, +1)),
                ("Release", () => AdjustRefCount(instance, -1)),
                .. methods,
            ];

            long vtable = _emulator.AllocateHeap(all.Length * 4);
            for (int i = 0; i < all.Length; i++)
            {
                long trap = _emulator.RegisterVtableMethod($"{interfaceName}::{all[i].Name}", all[i].Handler);
                _emulator.WriteUInt32(vtable + (i * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _emulator.WriteUInt32(instance, (uint)vtable);
            _emulator.WriteUInt32(instance + 4, 1);
            return instance;
        }

        /// <summary>
        /// DrawingSurfaceManipulationHost: the PointerMoved/Pressed/Released events a component
        /// subscribes to in SetManipulationHost, taken alphabetically as WinRT declares them.
        /// The handlers go where CoreWindow's do, so the same input path feeds both.
        /// </summary>
        private long ManipulationHostObject()
        {
            if (_manipulationHost != 0)
            {
                return _manipulationHost;
            }

            Action Subscribe(int coreWindowSlot, string name) => () =>
            {
                AddWindowHandler(coreWindowSlot, Arg(1));
                Log($"manipulation host: {name} subscribed, handler 0x{Arg(1):X8}");
                if (Arg(2) != 0)
                {
                    _emulator.WriteUInt64(Arg(2), (ulong)coreWindowSlot);
                }

                // The component releases its reference once the add returns; without one of our
                // own the handler was freed and every pointer event hit a zeroed vtable.
                AddRefThenReturn(Arg(1), HResultOk);
            };

            _manipulationHost = CreateDiscoveryObject(
                "IDrawingSurfaceManipulationHost",
                slotCount: 16,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("add_PointerMoved", Subscribe(SlotAddPointerMoved, "PointerMoved")),
                    [InspectableSlots + 1] = ("remove_PointerMoved", () => Return(HResultOk)),
                    [InspectableSlots + 2] = ("add_PointerPressed", Subscribe(SlotAddPointerPressed, "PointerPressed")),
                    [InspectableSlots + 3] = ("remove_PointerPressed", () => Return(HResultOk)),
                    [InspectableSlots + 4] = ("add_PointerReleased", Subscribe(SlotAddPointerReleased, "PointerReleased")),
                    [InspectableSlots + 5] = ("remove_PointerReleased", () => Return(HResultOk)),
                });
            return _manipulationHost;
        }

        /// <summary>Times each handler stand-in was invoked, for the log.</summary>
        public Dictionary<string, int> CallbacksRaised { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The stand-in for one C# event handler: answers through the delegate's out pointer when it
        /// returns something (so the component never reads an unset HSTRING or bool), and queues the
        /// handler's calls back into the component for the next frame.
        /// </summary>
        private long CallbackDelegate(string delegateType, XamlCallback callback)
        {
            WinmdReader.MethodLayout? invoke = _metadata?.DelegateInvoke(delegateType);
            string name = delegateType.Split('.')[^1];

            // Invoke(this, args..., retval*): floats travel in VFP registers and take no core slot.
            int resultArg = -1;
            if (invoke is not null && invoke.ReturnType != "Void")
            {
                resultArg = 1 + invoke.ParameterTypes.Count(t => t is not ("Single" or "Double"));
            }

            return CreateUnknownObject($"{name} (C# handler)", ("Invoke", () =>
            {
                int count = CallbacksRaised[name] = CallbacksRaised.GetValueOrDefault(name) + 1;
                if (count <= 3)
                {
                    Log($"event {name} raised" +
                        (callback.Then.Length > 0 ? $"; will call {string.Join(", ", callback.Then.Select(t => t.Method))}" : ""));
                }

                long result = resultArg > 0 ? Arg(resultArg) : 0;
                if (result != 0)
                {
                    long answer = callback.Returns switch
                    {
                        bool b => b ? 1 : 0,
                        int n => n,
                        string text => text.Length == 0 ? 0 : _strings.Create(text),
                        _ => 0,
                    };

                    if (invoke!.ReturnType is "Boolean" or "Byte")
                    {
                        _emulator.WriteMemory(result, [(byte)answer]);
                    }
                    else
                    {
                        _emulator.WriteUInt32(result, (uint)answer);
                    }
                }

                foreach (XamlShellStep step in callback.Then)
                {
                    _followUps.Enqueue(step);
                }

                Return(HResultOk);
            }));
        }

        /// <summary>A delegate whose Invoke does nothing: the stand-in for a C# event handler.</summary>
        private long NoOpDelegate() => _noOpDelegate != 0 ? _noOpDelegate : _noOpDelegate =
            CreateUnknownObject("NoOpDelegate", ("Invoke", () => Return(HResultOk)));

        private long _noOpDelegate;

        // -----------------------------------------------------------------------------
        // vccorlib's activation: the module's WRL creator map
        // -----------------------------------------------------------------------------

        /// <summary>IActivationFactory, {00000035-0000-0000-C000-000000000046}.</summary>
        private static readonly Guid ActivationFactoryIid = new("00000035-0000-0000-c000-000000000046");

        /// <summary>CLASS_E_CLASSNOTAVAILABLE.</summary>
        private const long ClassNotAvailable = 0x80040111;

        /// <summary>
        /// <c>HRESULT Platform::Details::GetActivationFactory(ModuleBase*, HSTRING, IActivationFactory**)</c> -
        /// what a C++/CX component's <c>DllGetActivationFactory</c> forwards to.
        /// </summary>
        /// <remarks>
        /// vccorlib walks the module's WRL creator map: the <c>minATL</c> section holds pointers
        /// to <c>CreatorMap</c> entries, each <c>{ factoryCreator, getRuntimeName, getTrustLevel,
        /// factoryCache, serverName }</c>. The runtime class name comes from calling
        /// <c>getRuntimeName</c>, and the factory from <c>factoryCreator(flags*, entry, iid, out)</c>,
        /// so this is two guest calls per entry, chained.
        /// </remarks>
        public void ModuleGetActivationFactory()
        {
            string wanted = Arg(1) == 0 ? string.Empty : _strings.ReadText(Arg(1));
            long outFactory = Arg(2);
            long callerReturn = _emulator.ReturnAddress;
            LoadedModule? owner = _emulator.ModuleAt(Arg(0)) ?? _emulator.ModuleAt(callerReturn & ~1L);
            List<long> entries = CreatorEntries(owner);
            List<string> seen = [];

            void Finish(long hr)
            {
                Return(hr);
                _emulator.ContinueAt(callerReturn);
            }

            void Try(int index)
            {
                if (index >= entries.Count)
                {
                    Log($"GetActivationFactory({wanted}): not in {owner?.Name ?? "the component"}'s {entries.Count} creator(s) [{string.Join(", ", seen)}]");
                    Finish(ClassNotAvailable);
                    return;
                }

                long entry = entries[index];
                long getName = _emulator.ReadUInt32(entry + 4, 0);
                if (!_emulator.IsExecutableCode(getName))
                {
                    Try(index + 1);
                    return;
                }

                Call("CreatorMap::getRuntimeName", getName, [], [], namePointer =>
                {
                    string name = namePointer == 0 ? string.Empty : _emulator.ReadUtf16String(namePointer);
                    if (!string.Equals(name, wanted, StringComparison.Ordinal))
                    {
                        seen.Add(name);
                        Try(index + 1);
                        return;
                    }

                    long flags = _scratch + 128;
                    long iid = _scratch + 144;
                    _emulator.WriteUInt32(flags, 0);
                    _emulator.WriteMemory(iid, ActivationFactoryIid.ToByteArray());
                    long create = _emulator.ReadUInt32(entry, 0);
                    Call("CreatorMap::factoryCreator", create, [flags, entry, iid, outFactory], [], hr =>
                    {
                        Log($"GetActivationFactory({wanted}) via creator {index} = 0x{hr:X8}");
                        Finish(hr);
                    });
                });
            }

            Try(0);
        }

        /// <summary>
        /// The creator map of the module whose DllGetActivationFactory forwarded here: the one that
        /// holds the <c>ModuleBase*</c> it passed (its WRL module object), else the one the call
        /// returns into (the forward is usually a tail call, so that is often the host's trap). A
        /// package can hold several C++/CX components (Spark2's SparkApp.dll and LuaMobileSDK.dll),
        /// and each answers only for its own classes. Falls back to the shell's component.
        /// </summary>
        private List<long> CreatorEntries(LoadedModule? module)
        {
            List<long> entries = [];
            PeImage? image = module?.Image ?? _componentImage;
            if (image is null)
            {
                return entries;
            }

            long imageBase = module?.ImageBase ?? image.ImageBase;
            foreach (PeSection section in image.Sections.Where(s => s.Name.StartsWith("minATL", StringComparison.Ordinal)))
            {
                long start = imageBase + section.VirtualAddress;
                for (long at = start; at < start + section.VirtualSize; at += 4)
                {
                    long entry = _emulator.ReadUInt32(at, 0);
                    if (entry != 0)
                    {
                        entries.Add(entry);
                    }
                }
            }

            return entries;
        }

        // -----------------------------------------------------------------------------
        // Calling into the image
        // -----------------------------------------------------------------------------

        private void CallSlot(string name, long target, int slot, IReadOnlyList<long> core, IReadOnlyList<uint> vfp, Action<long> then)
        {
            long vtable = _emulator.ReadUInt32(target);
            long function = _emulator.ReadUInt32(vtable + (slot * 4));
            Call(name, function, [target, .. core], vfp, then);
        }

        /// <summary>
        /// One host-to-emulated call whose continuation is <paramref name="then"/>, given r0.
        /// Floats go in s0, s1, ... (AAPCS-VFP); only the four core argument registers exist here.
        /// </summary>
        private void Call(string name, long function, IReadOnlyList<long> core, IReadOnlyList<uint> vfp, Action<long> then)
        {
            for (int i = 0; i < vfp.Count; i++)
            {
                _emulator.Cpu.VfpWrite(i, vfp[i]);
            }

            if (!_emulator.IsExecutableCode(function))
            {
                Log($"{name}: 0x{function:X8} is not code");
                Fail($"{name} has no code");
                return;
            }

            _emulator.CallEmulated(name, function, core.ToArray(), onReturn: () => then(Arg(0)), recycle: true);
        }
    }
}
