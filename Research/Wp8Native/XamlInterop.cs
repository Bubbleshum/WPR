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
    public sealed record XamlShellStep(string Method, params object?[] Arguments);

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

        /// <summary>The render target the grid asks for: WVGA, portrait, as the device reports it.</summary>
        public SizeF RenderTarget { get; init; } = new(480, 800);

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
            PointerRotation = null;
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
                return image.EntryPoint;
            }

            Activate();
            return getFactory;
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
            InvokeMember(step.Method, step.Arguments, result =>
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

        private void InvokeMember(string method, object?[] arguments, Action<long> then)
        {
            string className = _shell!.ClassId;
            var found = _metadata!.FindMethod(className, method);
            if (found is null && method == "SetManipulationHost")
            {
                // Windows.Phone.Input.Interop.IDrawingSurfaceManipulationHandler is a Windows
                // interface, so the component's metadata names it but does not define it: one
                // method, SetManipulationHost(DrawingSurfaceManipulationHost), at slot 6.
                found = (ManipulationHandlerLayout, ManipulationHandlerLayout.Methods[0]);
            }

            if (found is null)
            {
                Log($"{method}: not on {className}; skipped");
                then(0);
                return;
            }

            (WinmdReader.InterfaceLayout layout, WinmdReader.MethodLayout target) = found.Value;
            WithInterface(layout, pointer =>
            {
                if (pointer == 0)
                {
                    Log($"{method}: {layout.FullName} not available; skipped");
                    then(0);
                    return;
                }

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
                        XamlShell.Callback => NoOpDelegate(),
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
            ProbeCandidates(_instance, 0, wantedSlots, exclude: KnownInstanceIids(), found =>
            {
                Log($"{layout.FullName}: {(found.Pointer == 0 ? "no candidate answered" : $"IID {found.Iid} -> 0x{found.Pointer:X8}")}");
                Remember(found.Pointer);
            }, atLeast: true);
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

            // IDrawingSurfaceBackgroundContentProviderNative: IUnknown plus four methods.
            ProbeCandidates(_provider, 0, wantedSlots: 7, exclude: [], found =>
            {
                _providerNative = found.Pointer;
                Log($"content provider native interface: {(found.Pointer == 0 ? "NOT FOUND" : $"IID {found.Iid} -> 0x{found.Pointer:X8}")}");
                if (_providerNative == 0)
                {
                    Fail("no IDrawingSurfaceBackgroundContentProviderNative");
                    return;
                }

                (_hostDevice, _hostContext) = _emulator.Direct3D.CreateHostDevice();
                _hostTarget = _emulator.Direct3D.CreateHostRenderTargetView();
                long runtimeHost = RuntimeHostObject();

                CallSlot("Provider::Connect", _providerNative, SlotProviderConnect, [runtimeHost, _hostDevice], [], hr =>
                {
                    Log($"Connect = 0x{hr:X8}; drawing");
                    NextFrame();
                });
            });
        }

        private void NextFrame()
        {
            // Background work the game queued runs between frames - the closest a single CPU
            // gets to the thread pool it was written for - then any input that is due.
            _emulator.DrainDeferredCalls(() => _emulator.RunOtherThreads(() =>
            {
                if (_manipulationHost != 0 && DeliverInput(PrepareAndDraw, _manipulationHost))
                {
                    return;
                }

                PrepareAndDraw();
            }));
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
            _emulator.WriteMemory(size, [
                .. BitConverter.GetBytes(_shell!.RenderTarget.Width),
                .. BitConverter.GetBytes(_shell.RenderTarget.Height),
            ]);

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

        /// <summary>IDrawingSurfaceRuntimeHostNative: IUnknown, then RequestAdditionalFrame.</summary>
        /// <remarks>Every frame is drawn anyway: the host never stops asking.</remarks>
        private long RuntimeHostObject() => CreateUnknownObject(
            "IDrawingSurfaceRuntimeHostNative",
            ("RequestAdditionalFrame", () => Return(HResultOk)));

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
                _windowHandlers[coreWindowSlot] = Arg(1);
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
            List<long> entries = CreatorEntries();

            void Finish(long hr)
            {
                Return(hr);
                _emulator.ContinueAt(callerReturn);
            }

            void Try(int index)
            {
                if (index >= entries.Count)
                {
                    Log($"GetActivationFactory({wanted}): not in the module's {entries.Count} creator(s)");
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

        private List<long> CreatorEntries()
        {
            List<long> entries = [];
            if (_componentImage is not { } image)
            {
                return entries;
            }

            foreach (PeSection section in image.Sections.Where(s => s.Name.StartsWith("minATL", StringComparison.Ordinal)))
            {
                long start = image.ImageBase + section.VirtualAddress;
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
