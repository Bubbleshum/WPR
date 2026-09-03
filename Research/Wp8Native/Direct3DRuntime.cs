namespace WPR.Wp8Native
{
    /// <summary>
    /// The Direct3D 11 and DXGI surface the image needs in order to get a window and start
    /// presenting frames.
    /// </summary>
    /// <remarks>
    /// Two things make this different from the WinRT side of the probe, and both make it
    /// easier rather than harder.
    ///
    /// First, these are plain COM: <c>IUnknown</c> occupies slots 0-2, not the six an
    /// <c>IInspectable</c> takes. Second, most of the surface returns <c>void</c>. A WinRT
    /// stub that does nothing is a lie the caller eventually notices, but
    /// <c>IASetVertexBuffers</c> that does nothing is exactly what a renderer with no output
    /// device does, and the caller has no way to tell and no reason to care. What matters is
    /// recording what was asked for, which is what turns this from a stub into a description
    /// of the frame the game wanted to draw.
    ///
    /// What is NOT here is any rasterisation. Nothing is drawn and no pixel is produced. The
    /// point of this layer is to get the image through device creation, swap chain creation
    /// and into its main loop, and to say precisely what it does once it is there.
    /// </remarks>
    public sealed class Direct3DRuntime
    {
        private const long HResultOk = 0;
        private const long HResultNoInterface = unchecked((int)0x80004002);
        private const long HResultFail = unchecked((int)0x80004005);
        private const long DxgiNotFound = unchecked((int)0x887A0002);

        /// <summary>D3D_FEATURE_LEVEL_9_3, the WP8 baseline.</summary>
        private const uint FeatureLevel93 = 0x9300;

        /// <summary>
        /// The size of the device: the back buffer, and the window bounds that go with it.
        /// </summary>
        /// <remarks>
        /// Windows Phone 8 is a portrait device. Its CoreWindow and its swap chain are both
        /// 480x800 on WVGA, and a landscape game swaps the axes itself - so the size reported
        /// here is not the size the game draws in, and the two must not be conflated.
        /// <see cref="FrameCapture.Width"/> is the other one.
        /// <para>
        /// This image goes further than swapping axes: it picks a whole asset set from this
        /// size, loading a 480-wide title wordmark for a 480-wide device and a 767-wide one
        /// for an 800-wide device. Claiming to be 800x480 therefore got the landscape art
        /// laid out for a portrait viewport - every sprite 1.67x too wide, the wordmark off
        /// both edges of the screen - which looked like a projection bug and was not one.
        /// </para>
        /// <para><c>WPR_WINDOW=WxH</c> overrides it.</para>
        /// </remarks>
        public static uint BackBufferWidth { get; private set; } = 480;

        public static uint BackBufferHeight { get; private set; } = 800;

        static Direct3DRuntime()
        {
            string[] parts = Environment.GetEnvironmentVariable("WPR_WINDOW")?.Split('x', 'X') ?? [];
            if (parts.Length == 2 &&
                uint.TryParse(parts[0], out uint width) && uint.TryParse(parts[1], out uint height) &&
                width > 0 && height > 0 && width <= 4096 && height <= 4096)
            {
                BackBufferWidth = width;
                BackBufferHeight = height;
            }
        }

        /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM, what a WP8 swap chain uses.</summary>
        private const uint BackBufferFormat = 87;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;

        private long Arg(int index) => _frame.Arg(index);

        private void Return(long value) => _frame.Return(value);

        public Direct3DRuntime(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        // -------------------------------------------------------------------
        // Object identity
        //
        // A COM object is one identity behind several interface pointers, and
        // QueryInterface is what moves between them. The WinRT side of this probe hands
        // back the same pointer for any IID asked for, which holds only because those
        // statics implement one interface each. It does not hold here for a moment: the
        // image queries the device for IDXGIDevice and the back buffer for
        // ID3D11Texture2D, and answering either with the original vtable would send the
        // next call to a completely unrelated method.
        // -------------------------------------------------------------------

        private sealed class ComObject
        {
            public required string Name { get; init; }

            /// <summary>Interface name to the emulated pointer that exposes it.</summary>
            public Dictionary<string, long> Interfaces { get; } = new(StringComparer.Ordinal);

            public int RefCount { get; set; } = 1;
        }

        private readonly List<ComObject> _objects = new();
        private readonly Dictionary<long, ComObject> _byPointer = new();

        private readonly List<string> _log = new();
        private readonly List<string> _unimplemented = new();
        private readonly List<string> _unknownIids = new();

        /// <summary>Every Direct3D and DXGI call worth naming, in order.</summary>
        public IReadOnlyList<string> Log => _log;

        /// <summary>Slots that were called and have no implementation behind them.</summary>
        public IReadOnlyList<string> Unimplemented => _unimplemented;

        /// <summary>Interfaces the image asked for by IID that this layer does not know.</summary>
        public IReadOnlyList<string> UnknownIids => _unknownIids;

        /// <summary>True once the image has a device.</summary>
        public bool DeviceCreated { get; private set; }

        /// <summary>True once a swap chain exists for the window.</summary>
        public bool SwapChainCreated { get; private set; }

        /// <summary>How many frames the image presented.</summary>
        public int PresentCount { get; private set; }

        /// <summary>Draw calls issued, of every flavour.</summary>
        public int DrawCalls { get; private set; }

        /// <summary>How many times the image cleared a render target.</summary>
        public int ClearCount { get; private set; }

        /// <summary>How many times the image mapped a resource.</summary>
        public int MapCount { get; private set; }

        /// <summary>How many render targets the image last bound.</summary>
        public int RenderTargetsBound { get; private set; }

        /// <summary>How many times the image uploaded pixels or vertices to a resource.</summary>
        public int TextureUploads { get; private set; }

        /// <summary>The most recent colour the image cleared its render target to.</summary>
        public float[]? LastClearColour { get; private set; }

        /// <summary>
        /// Each time the image changes the colour it clears to, with the frame and the real
        /// seconds it happened at.
        /// </summary>
        /// <remarks>
        /// A game announces its phases by what it clears to - white for the publisher splash,
        /// sky blue for the menu - so this is a timeline of the load for free, in the one
        /// currency that matters when someone says it is slow: seconds. Frames alone cannot
        /// say it, because a frame is not a fixed amount of work.
        /// </remarks>
        public List<string> Phases { get; } = new();

        private readonly System.Diagnostics.Stopwatch _phaseClock = System.Diagnostics.Stopwatch.StartNew();

        private void NotePhase(float[] wanted)
        {
            if (LastClearColour is not null &&
                Math.Abs(LastClearColour[0] - wanted[0]) < 0.002f &&
                Math.Abs(LastClearColour[1] - wanted[1]) < 0.002f &&
                Math.Abs(LastClearColour[2] - wanted[2]) < 0.002f)
            {
                return;
            }

            if (Phases.Count >= 24)
            {
                return;
            }

            Phases.Add(
                $"{_phaseClock.Elapsed.TotalSeconds,7:F1}s  frame {PresentCount,6:N0}  " +
                $"clears to ({wanted[0]:0.00},{wanted[1]:0.00},{wanted[2]:0.00})");
        }

        /// <summary>The viewport the image last set, if it set one.</summary>
        public (float Width, float Height)? Viewport { get; private set; }

        /// <summary>Resources the image created, by kind.</summary>
        public Dictionary<string, int> ResourcesCreated { get; } = new(StringComparer.Ordinal);

        private void Note(string what) => _log.Add(what);

        private void Count(string kind)
            => ResourcesCreated[kind] = ResourcesCreated.GetValueOrDefault(kind) + 1;

        // -------------------------------------------------------------------
        // Building objects
        // -------------------------------------------------------------------

        /// <summary>
        /// Vtables, one per interface rather than one per object.
        /// </summary>
        /// <remarks>
        /// Sharing them is what makes a generous slot count affordable. Every slot costs a
        /// trap, the trap page holds a bounded number of them, and a game that creates a
        /// thousand textures would exhaust it several times over if each carried its own
        /// copy of the same eleven entries.
        ///
        /// It also happens to be what a real COM implementation does, and it is only possible
        /// because the handlers below resolve their object from the <c>this</c> pointer in r0
        /// rather than from a captured reference.
        /// </remarks>
        private readonly Dictionary<string, long> _vtables = new(StringComparer.Ordinal);

        /// <summary>
        /// How many slots past the last known method every vtable carries.
        /// </summary>
        /// <remarks>
        /// Not decoration. A vtable one slot too short does not fail as an unimplemented
        /// method - the read runs off the end into whatever is next on the heap and the CPU
        /// jumps to it, thousands of instructions from anything related. That failure has now
        /// happened three times in this probe: ICoreWindow at slot 56, ID3D11DeviceContext1
        /// at 116, and ID3D11RenderTargetView at 8. Padding turns the whole class of mistake
        /// into a log line.
        /// </remarks>
        private const int SlotPadding = 8;

        private long VtableFor(
            string interfaceName,
            int slotCount,
            IReadOnlyDictionary<int, (string Name, Action Handler)>? known)
        {
            if (_vtables.TryGetValue(interfaceName, out long existing))
            {
                return existing;
            }

            int total = slotCount + SlotPadding;
            long vtable = _emulator.AllocateHeap(total * 4);

            for (int slot = 0; slot < total; slot++)
            {
                int captured = slot;
                (string name, Action handler) = slot switch
                {
                    0 => ("QueryInterface", QueryInterface),
                    1 => ("AddRef", () => AdjustRefCount(+1)),
                    2 => ("Release", () => AdjustRefCount(-1)),
                    _ when known is not null && known.TryGetValue(slot, out (string Name, Action Handler) hit)
                        => (hit.Name, hit.Handler),
                    _ when captured >= slotCount
                        => ($"beyond{captured}", () => BeyondInterface(interfaceName, captured, slotCount)),
                    _ => ($"slot{captured}", () => Unhandled(interfaceName, captured)),
                };

                long trap = _emulator.RegisterVtableMethod($"{interfaceName}::{name}", handler);
                _emulator.WriteUInt32(vtable + (slot * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _vtables[interfaceName] = vtable;
            return vtable;
        }

        /// <summary>Builds an interface pointer on an object: a header carrying its vtable.</summary>
        private long CreateInterface(
            ComObject owner,
            string interfaceName,
            int slotCount,
            IReadOnlyDictionary<int, (string Name, Action Handler)>? known = null)
        {
            long vtable = VtableFor(interfaceName, slotCount, known);
            long instance = _emulator.AllocateHeap(8);

            _emulator.WriteUInt32(instance, (uint)vtable);
            _emulator.WriteUInt32(instance + 4, 0);

            owner.Interfaces[interfaceName] = instance;
            _byPointer[instance] = owner;
            return instance;
        }

        private ComObject NewObject(string name)
        {
            ComObject o = new() { Name = name };
            _objects.Add(o);
            return o;
        }

        /// <summary>The object behind the <c>this</c> pointer of the call being handled.</summary>
        private ComObject? Self() => _byPointer.GetValueOrDefault(Arg(0));

        private void Unhandled(string interfaceName, int slot)
        {
            string entry = $"{interfaceName}::slot{slot}  r1=0x{Arg(1):X8} r2=0x{Arg(2):X8} r3=0x{Arg(3):X8}";
            if (!_unimplemented.Contains(entry, StringComparer.Ordinal))
            {
                _unimplemented.Add(entry);
            }

            // Zero is S_OK for the HRESULT half of this surface and harmless for the void
            // half, which between them is nearly all of it.
            Return(HResultOk);
        }

        /// <summary>
        /// A call landing in the padding: the interface is wider than this layer believes.
        /// </summary>
        private void BeyondInterface(string interfaceName, int slot, int believed)
        {
            string entry = $"{interfaceName}::slot{slot} is past the {believed} slots this layer " +
                           "believes the interface has - the slot map is wrong, not just unimplemented";
            if (!_unimplemented.Contains(entry, StringComparer.Ordinal))
            {
                _unimplemented.Add(entry);
            }

            Return(HResultOk);
        }

        private void AdjustRefCount(int delta)
        {
            ComObject? owner = Self();
            if (owner is null)
            {
                Return(1);
                return;
            }

            owner.RefCount += delta;
            Return(Math.Max(owner.RefCount, 0));
        }

        // -------------------------------------------------------------------
        // QueryInterface, for real this time
        // -------------------------------------------------------------------

        private static readonly Dictionary<Guid, string> KnownIids = new()
        {
            [new Guid("00000000-0000-0000-c000-000000000046")] = "IUnknown",
            [new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")] = "ID3D11Device",
            [new Guid("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686")] = "ID3D11Device1",
            [new Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")] = "ID3D11DeviceContext",
            [new Guid("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1")] = "ID3D11DeviceContext1",
            [new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c")] = "IDXGIDevice",
            [new Guid("77db970f-6276-48ba-ba28-070143b4392c")] = "IDXGIDevice1",
            [new Guid("05008617-fbfd-4051-a790-144884b4f6a9")] = "IDXGIDevice2",
            [new Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc9")] = "IDXGIAdapter",
            [new Guid("29038f61-3839-4626-91fd-086879011a05")] = "IDXGIAdapter1",
            [new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")] = "IDXGIFactory",
            [new Guid("770aae78-f26f-4dba-a829-253c83d1b387")] = "IDXGIFactory1",
            [new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0")] = "IDXGIFactory2",
            [new Guid("310d36a0-d2e7-4c0a-aa04-6a9d23b8886a")] = "IDXGISwapChain",
            [new Guid("790a45f7-0d42-4876-983a-0a55cfe6f4aa")] = "IDXGISwapChain1",
            [new Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d")] = "ID3D11Resource",
            [new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")] = "ID3D11Texture2D",
            [new Guid("48570b85-d1ee-4fcd-a250-eb350722b037")] = "ID3D11Buffer",
            [new Guid("dfdba067-0b8d-4865-875b-d7b4516cc164")] = "ID3D11RenderTargetView",
            [new Guid("b0e06fe0-8192-4e1a-b1ca-36d7414710b2")] = "ID3D11ShaderResourceView",
            [new Guid("9fdac92a-1876-48c3-afad-25b94f84a9b6")] = "ID3D11DepthStencilView",
            [new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec")] = "IDXGISurface",
            [new Guid("aba496dd-b617-4cb8-a866-bc44d7eb1fa2")] = "IDXGISurface2",
            [new Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e")] = "IDXGIObject",
            [new Guid("3d3e0379-f9de-4d58-bb6c-18d62992f1a6")] = "IDXGIDeviceSubObject",
        };

        /// <summary>
        /// Interfaces that are the same pointer, mapped to the one this layer actually builds.
        /// </summary>
        /// <remarks>
        /// Single COM inheritance means a derived interface pointer IS the base pointer -
        /// <c>ID3D11DeviceContext1</c> is <c>ID3D11DeviceContext</c> with more slots on the
        /// end, not a separate object. So the widest version of each family is built once and
        /// handed back for every name in the family, which is both simpler and what a real
        /// implementation does.
        /// </remarks>
        private static readonly Dictionary<string, string> InterfaceAliases = new(StringComparer.Ordinal)
        {
            ["ID3D11Device"] = "ID3D11Device1",
            ["ID3D11DeviceContext"] = "ID3D11DeviceContext1",
            ["IDXGIDevice"] = "IDXGIDevice2",
            ["IDXGIDevice1"] = "IDXGIDevice2",
            ["IDXGIAdapter1"] = "IDXGIAdapter",
            ["IDXGIFactory"] = "IDXGIFactory2",
            ["IDXGIFactory1"] = "IDXGIFactory2",
            ["IDXGISwapChain"] = "IDXGISwapChain1",
            ["ID3D11Resource"] = "ID3D11Texture2D",
            ["IDXGISurface"] = "ID3D11Texture2D",
            ["IDXGISurface2"] = "ID3D11Texture2D",
            ["IDXGIObject"] = "IUnknown",
            ["IDXGIDeviceSubObject"] = "IUnknown",
        };

        private Guid ReadGuid(long address)
            => address == 0 ? Guid.Empty : new Guid(_emulator.ReadMemory(address, 16));

        /// <summary>HRESULT QueryInterface(this, REFIID iid, void** out).</summary>
        /// <remarks>
        /// Builds the requested interface on demand when its shape is known, and answers
        /// E_NOINTERFACE otherwise. Refusing matters: a caller that asked for something
        /// specific and got a yes will call a method that is not there, and a wrong vtable is
        /// far harder to diagnose than a refusal it has to handle.
        /// </remarks>
        private void QueryInterface()
        {
            ComObject? owner = Self();
            Guid iid = ReadGuid(Arg(1));
            long outPointer = Arg(2);

            if (owner is null || !KnownIids.TryGetValue(iid, out string? wanted))
            {
                string entry = $"{owner?.Name ?? "unknown object"} asked for {{{iid}}}";
                if (!_unknownIids.Contains(entry, StringComparer.Ordinal))
                {
                    _unknownIids.Add(entry);
                }

                Fail(outPointer, $"{owner?.Name ?? "?"}::QueryInterface({{{iid}}}) -> E_NOINTERFACE (unrecognised)");
                return;
            }

            long pointer = ResolveInterface(owner, wanted);
            if (pointer == 0)
            {
                Fail(outPointer, $"{owner.Name}::QueryInterface({wanted}) -> E_NOINTERFACE");
                return;
            }

            Note($"{owner.Name}::QueryInterface({wanted}) -> 0x{pointer:X8}");
            if (outPointer != 0)
            {
                _emulator.WriteUInt32(outPointer, (uint)pointer);
            }

            owner.RefCount++;
            Return(HResultOk);
        }

        private void Fail(long outPointer, string why)
        {
            Note(why);
            if (outPointer != 0)
            {
                _emulator.WriteUInt32(outPointer, 0);
            }

            Return(HResultNoInterface);
        }

        /// <summary>The pointer exposing <paramref name="wanted"/> on this object.</summary>
        private long ResolveInterface(ComObject owner, string wanted)
        {
            if (owner.Interfaces.TryGetValue(wanted, out long existing))
            {
                return existing;
            }

            if (InterfaceAliases.TryGetValue(wanted, out string? canonical))
            {
                if (owner.Interfaces.TryGetValue(canonical, out long aliased))
                {
                    return aliased;
                }

                wanted = canonical;
            }

            // IUnknown is whatever the object already leads with: every interface here
            // starts with the same three slots.
            if (wanted == "IUnknown" && owner.Interfaces.Count > 0)
            {
                return owner.Interfaces.Values.First();
            }

            return wanted switch
            {
                "IDXGIDevice2" when owner.Name == "Device" => CreateDxgiDevice(owner),
                "ID3D11Texture2D" when owner.Name.StartsWith("Texture", StringComparison.Ordinal)
                    => CreateInterface(owner, wanted, ResourceSlots, ResourceMethods()),
                "IDXGISwapChain1" when owner.Name == "SwapChain"
                    => CreateSwapChainInterface(owner),
                _ => 0,
            };
        }

        // -------------------------------------------------------------------
        // D3D11CreateDevice
        // -------------------------------------------------------------------

        private long _immediateContext;
        private long _backBufferPointer;

        // ---------------------------------------------------------------------
        // Resources with something in them
        //
        // Answering a Create call with a shaped object is enough to keep the image running,
        // and it was all this layer did: Map handed everyone the same scratch buffer,
        // UpdateSubresource counted and discarded, and the pixels the game spent its whole
        // startup decoding went nowhere. Nothing could be drawn from that.
        //
        // Every resource now owns emulated memory the size of its descriptor, and every
        // path that fills one - initial data, Map, UpdateSubresource - fills that. It costs
        // heap and buys the only thing a person can actually look at.
        // ---------------------------------------------------------------------

        private readonly Dictionary<long, FrameCapture.Resource> _resources = new();
        private readonly Dictionary<long, FrameCapture.Resource> _viewedResource = new();
        private readonly Dictionary<long, List<FrameCapture.VertexElement>> _layouts = new();

        /// <summary>One entry per input slot, because a vertex can come from several buffers.</summary>
        private readonly FrameCapture.VertexStream[] _streams =
            Enumerable.Range(0, 8).Select(_ => new FrameCapture.VertexStream(null, 0, 0)).ToArray();

        private FrameCapture.Resource? _boundIndexBuffer;
        private FrameCapture.Resource? _boundTexture;
        private FrameCapture.Resource? _boundConstants;
        private List<FrameCapture.VertexElement> _boundLayout = new();
        private int _indexOffset;
        private uint _indexFormat = 57; // DXGI_FORMAT_R16_UINT
        private uint _topology = 4;     // TRIANGLELIST

        /// <summary>The frame being assembled, and the rasteriser that turns it into pixels.</summary>
        public FrameCapture Frame { get; } = new();

        /// <summary>Where to write a PNG of the first frame that draws anything, if anywhere.</summary>
        public string? ScreenshotPath { get; set; }

        /// <summary>What the captured frame contained.</summary>
        public string? ScreenshotSummary { get; private set; }

        private FrameCapture.Resource? ResourceAt(long pointer)
            => pointer == 0 ? null : _resources.GetValueOrDefault(pointer);

        /// <summary>
        /// HRESULT D3D11CreateDevice(pAdapter, DriverType, Software, Flags, pFeatureLevels,
        /// FeatureLevels, SDKVersion, ppDevice, pFeatureLevel, ppImmediateContext).
        /// </summary>
        /// <remarks>
        /// Ten arguments, so everything from <c>pFeatureLevels</c> on is on the stack. The
        /// three that matter are all out-parameters near the end, which is why this could not
        /// be written until <see cref="CallFrame.Arg"/> could read past r3.
        /// </remarks>
        public void CreateDevice()
        {
            uint flags = (uint)Arg(3);
            long outDevice = Arg(7);
            long outFeatureLevel = Arg(8);
            long outContext = Arg(9);

            ComObject device = NewObject("Device");
            long devicePointer = CreateInterface(device, "ID3D11Device1", DeviceSlots, DeviceMethods());

            ComObject context = NewObject("Context");
            _immediateContext = CreateInterface(
                context, "ID3D11DeviceContext1", ContextSlots, ContextMethods());

            Write(outDevice, (uint)devicePointer);
            Write(outFeatureLevel, FeatureLevel93);
            Write(outContext, (uint)_immediateContext);

            DeviceCreated = true;
            Note($"D3D11CreateDevice(driverType={Arg(1)}, flags=0x{flags:X}) -> device 0x{devicePointer:X8}, " +
                 $"context 0x{_immediateContext:X8}, feature level 9_3");
            Return(HResultOk);
        }

        private void Write(long address, uint value)
        {
            if (address != 0)
            {
                _emulator.WriteUInt32(address, value);
            }
        }

        // -------------------------------------------------------------------
        // ID3D11Device1
        //
        // IUnknown at 0-2, then the ID3D11Device member order: CreateBuffer at 3 through
        // GetExceptionMode at 42. ID3D11Device1 continues at 43 and ends at 49.
        //
        // WP8 is a Direct3D 11.1 platform, so the image asks for the 11.1 interfaces by IID
        // and never touches the 11.0 ones. Building the wide version and aliasing the narrow
        // name onto it covers both.
        // -------------------------------------------------------------------

        private const int DeviceSlots = 50;

        private Dictionary<int, (string, Action)> DeviceMethods() => new()
        {
            [3] = ("CreateBuffer", MakeResource("Buffer", outIndex: 3)),
            [4] = ("CreateTexture1D", MakeResource("Texture1D", outIndex: 3)),
            [5] = ("CreateTexture2D", MakeResource("Texture2D", outIndex: 3)),
            [6] = ("CreateTexture3D", MakeResource("Texture3D", outIndex: 3)),
            [7] = ("CreateShaderResourceView", () =>
            {
                FrameCapture.Resource? viewed = ResourceAt(Arg(1));
                Count("ShaderResourceView");

                long outPointer = Arg(3);
                if (outPointer == 0)
                {
                    Return(HResultOk);
                    return;
                }

                ComObject created = NewObject($"ShaderResourceView{ResourcesCreated["ShaderResourceView"]}");
                long pointer = CreateInterface(created, "ID3D11ShaderResourceView", ViewSlots, ViewMethods());
                if (viewed is not null)
                {
                    _viewedResource[pointer] = viewed;
                }

                _emulator.WriteUInt32(outPointer, (uint)pointer);
                Return(HResultOk);
            }),
            [8] = ("CreateUnorderedAccessView", MakeView("UnorderedAccessView", outIndex: 3)),
            [9] = ("CreateRenderTargetView", MakeView("RenderTargetView", outIndex: 3)),
            [10] = ("CreateDepthStencilView", MakeView("DepthStencilView", outIndex: 3)),
            [11] = ("CreateInputLayout", () =>
            {
                // D3D11_INPUT_ELEMENT_DESC: SemanticName (a char*), SemanticIndex, Format,
                // InputSlot, AlignedByteOffset, InputSlotClass, InstanceDataStepRate.
                long descs = Arg(1);
                int count = (int)Math.Clamp(Arg(2), 0, 32);
                var elements = new List<FrameCapture.VertexElement>();

                for (int i = 0; i < count && descs != 0; i++)
                {
                    long entry = descs + (i * 28);
                    string semantic = _frame.ReadNarrowString(_emulator.ReadUInt32(entry + 0, 0), 32);
                    elements.Add(new FrameCapture.VertexElement(
                        semantic,
                        _emulator.ReadUInt32(entry + 4, 0),
                        _emulator.ReadUInt32(entry + 8, 0),
                        (int)_emulator.ReadUInt32(entry + 16, 0),
                        (int)_emulator.ReadUInt32(entry + 12, 0)));
                }

                Count("InputLayout");
                long outPointer = Arg(5);
                if (outPointer == 0)
                {
                    Return(HResultOk);
                    return;
                }

                ComObject created = NewObject($"InputLayout{ResourcesCreated["InputLayout"]}");
                long pointer = CreateInterface(created, "ID3D11InputLayout", DeviceChildSlots);
                _layouts[pointer] = elements;
                Note($"CreateInputLayout: {string.Join(", ", elements.Select(e => $"{e.Semantic}{e.Index}@{e.Offset} fmt {e.Format}"))}");
                _emulator.WriteUInt32(outPointer, (uint)pointer);
                Return(HResultOk);
            }),
            // Every Create*Shader takes pShaderBytecode, BytecodeLength, pClassLinkage and
            // the out-parameter, so the out-parameter is index 4 with this counted - not 3.
            // Index 3 is pClassLinkage, which is almost always null, so this wrote nothing
            // and answered S_OK: the image kept whatever its own memory already held where
            // the shader pointer belonged.
            [12] = ("CreateVertexShader", MakeChild("VertexShader", outIndex: 4)),
            [13] = ("CreateGeometryShader", MakeChild("GeometryShader", outIndex: 4)),
            [16] = ("CreateHullShader", MakeChild("HullShader", outIndex: 4)),
            [17] = ("CreateDomainShader", MakeChild("DomainShader", outIndex: 4)),
            [18] = ("CreateComputeShader", MakeChild("ComputeShader", outIndex: 4)),
            [15] = ("CreatePixelShader", MakeChild("PixelShader", outIndex: 4)),
            // One out-parameter and nothing else. Left unwritten it is the same mistake the
            // COM entry points made: S_OK with the caller's own memory handed back as an
            // object pointer.
            [19] = ("CreateClassLinkage", MakeChild("ClassLinkage", outIndex: 1)),
            [20] = ("CreateBlendState", MakeChild("BlendState", outIndex: 2)),
            [21] = ("CreateDepthStencilState", MakeChild("DepthStencilState", outIndex: 2)),
            [22] = ("CreateRasterizerState", MakeChild("RasterizerState", outIndex: 2)),
            [23] = ("CreateSamplerState", MakeChild("SamplerState", outIndex: 2)),
            [24] = ("CreateQuery", MakeChild("Query", outIndex: 2)),
            [25] = ("CreatePredicate", MakeChild("Predicate", outIndex: 2)),
            [29] = ("CheckFormatSupport", () =>
            {
                // Claim every format is fully supported. A game that asks usually wants to
                // know whether it can skip a fallback, and a fallback is more code to get
                // through, not less.
                Write(Arg(2), 0x000FFFFF);
                Return(HResultOk);
            }),
            [30] = ("CheckMultisampleQualityLevels", () =>
            {
                Write(Arg(3), 1);
                Return(HResultOk);
            }),
            [33] = ("CheckFeatureSupport", () =>
            {
                // Blank the structure rather than filling it: every field is a capability
                // flag, and zero means "not supported", which is the truthful answer here.
                long buffer = Arg(2);
                int size = (int)Arg(3);
                if (buffer != 0 && size > 0 && size < 4096)
                {
                    _emulator.WriteMemory(buffer, new byte[size]);
                }

                Return(HResultOk);
            }),
            [34] = ("GetPrivateData", () => Return(DxgiNotFound)),
            [37] = ("GetFeatureLevel", () => Return(FeatureLevel93)), // the enum, not an HRESULT
            [38] = ("GetCreationFlags", () => Return(0)),
            [39] = ("GetDeviceRemovedReason", () => Return(HResultOk)),
            [40] = ("GetImmediateContext", ReturnImmediateContext),
            [41] = ("SetExceptionMode", () => Return(HResultOk)),
            [42] = ("GetExceptionMode", () => Return(0)),
            [43] = ("GetImmediateContext1", ReturnImmediateContext),
            [45] = ("CreateBlendState1", MakeChild("BlendState", outIndex: 2)),
            [46] = ("CreateRasterizerState1", MakeChild("RasterizerState", outIndex: 2)),
        };

        private void ReturnImmediateContext()
        {
            Write(Arg(1), (uint)_immediateContext);
            Return(HResultOk); // returns void
        }

        /// <summary>A Create* method producing something the image can map or query.</summary>
        private Action MakeResource(string kind, int outIndex) => () =>
        {
            long outPointer = Arg(outIndex);
            Count(kind);

            if (outPointer == 0)
            {
                Return(HResultOk);
                return;
            }

            ComObject created = NewObject($"{kind}{ResourcesCreated[kind]}");
            long pointer = CreateInterface(created, $"ID3D11{kind}", ResourceSlots, ResourceMethods());

            FrameCapture.Resource resource = new() { Name = created.Name };
            _resources[pointer] = resource;

            if (kind == "Texture2D")
            {
                DescribeTexture(resource, Arg(1), Arg(2));
            }
            else if (kind == "Buffer")
            {
                DescribeBuffer(resource, Arg(1), Arg(2));
            }

            _emulator.WriteUInt32(outPointer, (uint)pointer);
            Return(HResultOk);
        };

        /// <summary>
        /// D3D11_TEXTURE2D_DESC: Width, Height, MipLevels, ArraySize, Format, SampleDesc,
        /// Usage, BindFlags, CPUAccessFlags, MiscFlags - then D3D11_SUBRESOURCE_DATA is
        /// pSysMem, SysMemPitch, SysMemSlicePitch.
        /// </summary>
        private void DescribeTexture(FrameCapture.Resource resource, long desc, long initial)
        {
            if (desc == 0)
            {
                return;
            }

            resource.PixelWidth = (int)Math.Clamp(_emulator.ReadUInt32(desc + 0, 0), 0, 8192);
            resource.PixelHeight = (int)Math.Clamp(_emulator.ReadUInt32(desc + 4, 0), 0, 8192);
            resource.Format = _emulator.ReadUInt32(desc + 16, 0);

            // A block-compressed texture is stored as 4x4 blocks, so both its pitch and its
            // row count are in blocks. BC1 is eight bytes a block, BC2 and BC3 sixteen.
            resource.BlockBytes = resource.Format switch
            {
                70 or 71 or 72 => 8,                       // BC1
                73 or 74 or 75 or 76 or 77 or 78 => 16,    // BC2, BC3
                _ => 0,
            };

            if (resource.BlockBytes > 0)
            {
                resource.RowPitch = Math.Max(1, (resource.PixelWidth + 3) / 4) * resource.BlockBytes;
                resource.Rows = Math.Max(1, (resource.PixelHeight + 3) / 4);
            }
            else
            {
                // The 16-bit formats a phone game uses for its UI: B5G6R5, B5G5R5A1 and
                // B4G4R4A4. Sizing their rows at four bytes a pixel puts every row at twice
                // its real stride and drops half of each one.
                resource.PixelBytes = resource.Format switch
                {
                    85 or 86 or 115 => 2,
                    _ => 4,
                };

                resource.RowPitch = resource.PixelWidth * resource.PixelBytes;
                resource.Rows = resource.PixelHeight;
            }

            resource.StorageSize = (long)resource.RowPitch * resource.Rows;

            if (resource.StorageSize is <= 0 or > 64 * 1024 * 1024)
            {
                resource.StorageSize = 0;
                return;
            }

            resource.Storage = _emulator.AllocateHeap(resource.StorageSize);

            if (initial == 0 || resource.Storage == 0)
            {
                return;
            }

            long source = _emulator.ReadUInt32(initial + 0, 0);
            int pitch = (int)_emulator.ReadUInt32(initial + 4, 0);
            CopyRows(resource, source, pitch);
        }

        /// <summary>D3D11_BUFFER_DESC opens with ByteWidth.</summary>
        private void DescribeBuffer(FrameCapture.Resource resource, long desc, long initial)
        {
            if (desc == 0)
            {
                return;
            }

            resource.StorageSize = _emulator.ReadUInt32(desc + 0, 0);
            if (resource.StorageSize is <= 0 or > 64 * 1024 * 1024)
            {
                resource.StorageSize = 0;
                return;
            }

            resource.Storage = _emulator.AllocateHeap(resource.StorageSize);

            long source = initial == 0 ? 0 : _emulator.ReadUInt32(initial + 0, 0);
            if (source == 0 || resource.Storage == 0)
            {
                return;
            }

            try
            {
                _emulator.WriteMemory(
                    resource.Storage, _emulator.ReadMemory(source, (int)resource.StorageSize));
                resource.HasContent = true;
            }
            catch (Exception)
            {
                // A descriptor that lies about its own size is the image's problem, not a
                // reason to stop.
            }
        }

        /// <summary>
        /// Copies image rows in, honouring a source pitch that need not match ours.
        /// </summary>
        private void CopyRows(FrameCapture.Resource resource, long source, int sourcePitch)
        {
            if (source == 0 || resource.Storage == 0 || resource.Rows <= 0)
            {
                return;
            }

            int pitch = sourcePitch > 0 ? sourcePitch : resource.RowPitch;
            int copy = Math.Min(pitch, resource.RowPitch);

            try
            {
                for (int y = 0; y < Math.Max(resource.Rows, 1); y++)
                {
                    _emulator.WriteMemory(
                        resource.Storage + ((long)y * resource.RowPitch),
                        _emulator.ReadMemory(source + ((long)y * pitch), copy));
                }

                resource.HasContent = true;
            }
            catch (Exception)
            {
                // Ran off the end of whatever the image handed us; keep what landed.
            }
        }

        /// <summary>A Create* method producing a view, which has GetResource and GetDesc.</summary>
        private Action MakeView(string kind, int outIndex)
            => Creator(kind, outIndex, $"ID3D11{kind}", ViewSlots, ViewMethods());

        /// <summary>A Create* method producing a device child with no interesting behaviour.</summary>
        private Action MakeChild(string kind, int outIndex)
            => Creator(kind, outIndex, $"ID3D11{kind}", DeviceChildSlots, null);

        private Action Creator(
            string kind,
            int outIndex,
            string interfaceName,
            int slotCount,
            IReadOnlyDictionary<int, (string Name, Action Handler)>? methods) => () =>
        {
            long outPointer = Arg(outIndex);
            Count(kind);

            // A null out-pointer means "just tell me whether this would work", which is a
            // documented use of every D3D11 Create call and must not allocate.
            if (outPointer == 0)
            {
                Return(HResultOk);
                return;
            }

            ComObject created = NewObject($"{kind}{ResourcesCreated[kind]}");
            long pointer = CreateInterface(created, interfaceName, slotCount, methods);
            _emulator.WriteUInt32(outPointer, (uint)pointer);
            Return(HResultOk);
        };

        // ID3D11DeviceChild: IUnknown 0-2, GetDevice 3, GetPrivateData 4, SetPrivateData 5,
        // SetPrivateDataInterface 6. State objects add only GetDesc at 7.
        private const int DeviceChildSlots = 8;

        // ID3D11View adds GetResource at 7; every concrete view adds GetDesc at 8.
        private const int ViewSlots = 9;

        // ID3D11Resource adds GetType 7, SetEvictionPriority 8, GetEvictionPriority 9;
        // ID3D11Texture2D and ID3D11Buffer both add GetDesc at 10.
        private const int ResourceSlots = 11;

        private Dictionary<int, (string, Action)> ViewMethods() => new()
        {
            [8] = ("GetDesc", () =>
            {
                // D3D11_RENDER_TARGET_VIEW_DESC opens with Format then ViewDimension. The
                // image reads the first field back and keeps it, so a zero here would tell it
                // the render target has DXGI_FORMAT_UNKNOWN.
                long desc = Arg(1);
                if (desc == 0)
                {
                    return;
                }

                _emulator.WriteUInt32(desc + 0, BackBufferFormat);
                _emulator.WriteUInt32(desc + 4, 4); // D3D11_RTV_DIMENSION_TEXTURE2D
                _emulator.WriteUInt32(desc + 8, 0);
                _emulator.WriteUInt32(desc + 12, 0);
            }),
        };

        private Dictionary<int, (string, Action)> ResourceMethods() => new()
        {
            [7] = ("GetType", () => Return(3)), // D3D11_RESOURCE_DIMENSION_TEXTURE2D
            [10] = ("GetDesc", () =>
            {
                // D3D11_TEXTURE2D_DESC: Width, Height, MipLevels, ArraySize, Format,
                // SampleDesc{Count,Quality}, Usage, BindFlags, CPUAccessFlags, MiscFlags.
                long desc = Arg(1);
                if (desc == 0)
                {
                    return;
                }

                _emulator.WriteUInt32(desc + 0, BackBufferWidth);
                _emulator.WriteUInt32(desc + 4, BackBufferHeight);
                _emulator.WriteUInt32(desc + 8, 1);
                _emulator.WriteUInt32(desc + 12, 1);
                _emulator.WriteUInt32(desc + 16, BackBufferFormat);
                _emulator.WriteUInt32(desc + 20, 1);
                _emulator.WriteUInt32(desc + 24, 0);
                _emulator.WriteUInt32(desc + 28, 0);
                _emulator.WriteUInt32(desc + 32, 0x20 | 0x8); // RENDER_TARGET | SHADER_RESOURCE
                _emulator.WriteUInt32(desc + 36, 0);
                _emulator.WriteUInt32(desc + 40, 0);
            }),
        };

        // -------------------------------------------------------------------
        // ID3D11DeviceContext1
        //
        // ID3D11DeviceContext runs from ID3D11DeviceChild at 0-6 to FinishCommandList at 115;
        // ID3D11DeviceContext1 adds CopySubresourceRegion1 at 116 through DiscardView1 at 134.
        // -------------------------------------------------------------------

        private const int ContextSlots = 135;

        private Dictionary<int, (string, Action)> ContextMethods() => new()
        {
            [7] = ("VSSetConstantBuffers", () =>
            {
                // (StartSlot, NumBuffers, ppConstantBuffers)
                if (Arg(2) > 0 && Arg(3) != 0)
                {
                    _boundConstants = ResourceAt(_emulator.ReadUInt32(Arg(3), 0));
                }

                Return(HResultOk);
            }),
            [8] = ("PSSetShaderResources", () =>
            {
                // (StartSlot, NumViews, ppShaderResourceViews)
                if (Arg(2) > 0 && Arg(3) != 0)
                {
                    long view = _emulator.ReadUInt32(Arg(3), 0);
                    _boundTexture = view == 0 ? null : _viewedResource.GetValueOrDefault(view);
                }

                Return(HResultOk);
            }),
            [12] = ("DrawIndexed", () =>
            {
                // (IndexCount, StartIndexLocation, BaseVertexLocation)
                RecordDraw((int)Arg(1), (int)Arg(2), _frame.SignedArg(3), indexed: true);
                Return(HResultOk);
            }),
            [13] = ("Draw", () =>
            {
                // (VertexCount, StartVertexLocation)
                RecordDraw((int)Arg(1), (int)Arg(2), 0, indexed: false);
                Return(HResultOk);
            }),
            [17] = ("IASetInputLayout", () =>
            {
                _boundLayout = _layouts.GetValueOrDefault(Arg(1)) ?? new List<FrameCapture.VertexElement>();
                Return(HResultOk);
            }),
            [18] = ("IASetVertexBuffers", () =>
            {
                // (StartSlot, NumBuffers, ppVertexBuffers, pStrides, pOffsets) - arrays, one
                // entry per slot, and this image uses two of them.
                int start = (int)Arg(1);
                int count = (int)Arg(2);

                for (int i = 0; i < count && start + i < _streams.Length; i++)
                {
                    int slot = start + i;
                    _streams[slot] = new FrameCapture.VertexStream(
                        Arg(3) == 0 ? null : ResourceAt(_emulator.ReadUInt32(Arg(3) + (i * 4), 0)),
                        Arg(4) == 0 ? 0 : (int)_emulator.ReadUInt32(Arg(4) + (i * 4), 0),
                        Arg(5) == 0 ? 0 : (int)_emulator.ReadUInt32(Arg(5) + (i * 4), 0));
                }

                Return(HResultOk);
            }),
            [19] = ("IASetIndexBuffer", () =>
            {
                // (pIndexBuffer, Format, Offset)
                _boundIndexBuffer = ResourceAt(Arg(1));
                _indexFormat = (uint)Arg(2);
                _indexOffset = (int)Arg(3);
                Return(HResultOk);
            }),
            [24] = ("IASetPrimitiveTopology", () =>
            {
                _topology = (uint)Arg(1);
                Return(HResultOk);
            }),
            [14] = ("Map", MapResource),
            [15] = ("Unmap", () => Return(HResultOk)),
            [20] = ("DrawIndexedInstanced", CountDraw("DrawIndexedInstanced")),
            [21] = ("DrawInstanced", CountDraw("DrawInstanced")),
            [33] = ("OMSetRenderTargets", () =>
            {
                RenderTargetsBound = (int)Arg(1);
                Return(HResultOk);
            }),
            [44] = ("RSSetViewports", () =>
            {
                // void RSSetViewports(UINT NumViewports, const D3D11_VIEWPORT*), and a
                // D3D11_VIEWPORT is six floats starting TopLeftX, TopLeftY, Width, Height.
                long viewports = Arg(2);
                if (Arg(1) > 0 && viewports != 0)
                {
                    if (Viewport is null)
                    {
                        Note($"RSSetViewports {BitConverter.ToSingle(_emulator.ReadMemory(viewports + 8, 4))}" +
                             $"x{BitConverter.ToSingle(_emulator.ReadMemory(viewports + 12, 4))}");
                    }

                    Viewport = (
                        BitConverter.ToSingle(_emulator.ReadMemory(viewports + 8, 4)),
                        BitConverter.ToSingle(_emulator.ReadMemory(viewports + 12, 4)));
                }

                Return(HResultOk);
            }),
            [48] = ("UpdateSubresource", () =>
            {
                // (pDstResource, DstSubresource, pDstBox, pSrcData, SrcRowPitch, SrcDepthPitch)
                FrameCapture.Resource? destination = ResourceAt(Arg(1));
                if (destination is not null && Arg(4) != 0)
                {
                    if (destination.Rows > 0)
                    {
                        CopyRows(destination, Arg(4), (int)Arg(5));
                    }
                    else if (destination.Storage != 0 && destination.StorageSize > 0)
                    {
                        try
                        {
                            _emulator.WriteMemory(
                                destination.Storage,
                                _emulator.ReadMemory(Arg(4), (int)destination.StorageSize));
                            destination.HasContent = true;
                        }
                        catch (Exception)
                        {
                            // Short source; keep whatever landed.
                        }
                    }
                }

                // void UpdateSubresource(pDstResource, DstSubresource, pDstBox, pSrcData,
                // SrcRowPitch, SrcDepthPitch) - this is the texture upload path, and how the
                // image gets its decoded PVR pixels onto the GPU. Counting them is the
                // measure of how much art a frame actually had behind it.
                TextureUploads++;
                Return(HResultOk);
            }),
            [50] = ("ClearRenderTargetView", () =>
            {
                // void ClearRenderTargetView(ID3D11RenderTargetView*, const FLOAT[4]).
                long colour = Arg(2);
                if (colour != 0)
                {
                    float[] wanted =
                    [
                        BitConverter.ToSingle(_emulator.ReadMemory(colour + 0, 4)),
                        BitConverter.ToSingle(_emulator.ReadMemory(colour + 4, 4)),
                        BitConverter.ToSingle(_emulator.ReadMemory(colour + 8, 4)),
                        BitConverter.ToSingle(_emulator.ReadMemory(colour + 12, 4)),
                    ];

                    NotePhase(wanted);
                    LastClearColour = wanted;
                }

                ClearCount++;
                Frame.BeginFrame(LastClearColour ?? [0f, 0f, 0f, 1f]);
                Return(HResultOk);
            }),
            [53] = ("ClearDepthStencilView", () => Return(HResultOk)),
        };

        private Action CountDraw(string kind) => () =>
        {
            DrawCalls++;
            Count(kind);
            Return(HResultOk);
        };

        /// <summary>
        /// Remembers a draw and everything bound at the moment it was issued.
        /// </summary>
        private void RecordDraw(int count, int start, int baseVertex, bool indexed)
        {
            DrawCalls++;
            Count(indexed ? "DrawIndexed" : "Draw");

            Frame.Record(FrameCapture.Snapshot(_emulator, new FrameCapture.DrawCall(
                count,
                start,
                baseVertex,
                _streams.ToArray(),
                indexed ? _boundIndexBuffer : null,
                _indexFormat,
                _indexOffset,
                _boundTexture,
                _boundLayout,
                ReadTransform(),
                _topology)));
        }

        /// <summary>
        /// The first sixteen floats of the bound vertex constant buffer, as a matrix.
        /// </summary>
        /// <remarks>
        /// Standing in for the vertex shader, which this layer does not run. A 2D engine
        /// puts its projection - usually just an orthographic matrix - at the front of the
        /// first constant buffer, and applying it turns whatever coordinate space the
        /// vertices are in into clip space. When the guess is wrong the geometry lands
        /// somewhere absurd and is clipped away, which is visible rather than silent.
        /// </remarks>
        private float[]? ReadTransform()
        {
            if (_boundConstants is null || _boundConstants.Storage == 0 || _boundConstants.StorageSize < 64)
            {
                return null;
            }

            try
            {
                byte[] raw = _emulator.ReadMemory(_boundConstants.Storage, 64);
                float[] matrix = new float[16];
                for (int i = 0; i < 16; i++)
                {
                    matrix[i] = BitConverter.ToSingle(raw, i * 4);
                    if (!float.IsFinite(matrix[i]))
                    {
                        return null;
                    }
                }

                return matrix;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// HRESULT Map(pResource, Subresource, MapType, MapFlags, D3D11_MAPPED_SUBRESOURCE*).
        /// </summary>
        /// <remarks>
        /// The one context method that cannot do nothing. The image maps a buffer to write
        /// vertices into it, and it writes through the pointer this hands back - so that
        /// pointer has to be real emulated memory, big enough for whatever it decides to put
        /// there. One scratch buffer serves every map: nothing reads any of it back.
        /// </remarks>
        private void MapResource()
        {
            // Arg(5), not Arg(4). Counting this, Map takes six: this, pResource,
            // Subresource, MapType, MapFlags, pMappedSubresource - so the out-parameter is
            // the *second* stack slot. Reading the first gave MapFlags, which is zero, which
            // looked exactly like a caller passing no out-parameter at all.
            //
            // The consequence was not a crash but a lie the image believed: every Map
            // answered E_FAIL, the game took its no-mapped-buffer path, and a vertex copy
            // several frames later ran off the end of a sixteen-byte vector using a
            // destination that path had never initialised.
            long mapped = Arg(5);
            if (mapped == 0)
            {
                Return(HResultFail);
                return;
            }

            // The resource's own storage, not a shared scratch. One buffer for everybody was
            // fine while nothing read any of it back; it is useless the moment the vertices
            // a draw refers to have to still be there when the draw happens.
            FrameCapture.Resource? resource = ResourceAt(Arg(1));
            long buffer = resource?.Storage ?? 0;
            int pitch = resource?.RowPitch > 0 ? resource.RowPitch : (int)(BackBufferWidth * 4);

            if (buffer == 0)
            {
                _mapScratch = _mapScratch != 0 ? _mapScratch : _emulator.AllocateHeap(MapScratchSize);
                buffer = _mapScratch;
            }
            else if (resource is not null)
            {
                resource.HasContent = true;
            }

            _emulator.WriteUInt32(mapped + 0, (uint)buffer);
            _emulator.WriteUInt32(mapped + 4, (uint)pitch);
            _emulator.WriteUInt32(mapped + 8, (uint)(resource?.StorageSize ?? MapScratchSize));

            MapCount++;
            Return(HResultOk);
        }

        /// <summary>
        /// Four megabytes, which is more than a WVGA back buffer and far more than any vertex
        /// batch this game will build in one go.
        /// </summary>
        private const long MapScratchSize = 4 * 1024 * 1024;

        /// <summary>Fallback for a Map against a resource this layer never saw created.</summary>
        private long _mapScratch;

        // -------------------------------------------------------------------
        // DXGI
        //
        // The route to a window: device -> IDXGIDevice -> GetParent or GetAdapter ->
        // GetParent -> IDXGIFactory2::CreateSwapChainForCoreWindow. Every one of these
        // derives from IDXGIObject, so slots 3-6 are SetPrivateData, SetPrivateDataInterface,
        // GetPrivateData and GetParent before anything specific starts at 7.
        // -------------------------------------------------------------------

        private const int DxgiObjectSlots = 7;

        /// <summary>IDXGIObject::GetParent, slot 6 on every DXGI interface.</summary>
        private const int SlotGetParent = 6;

        /// <summary>HRESULT GetParent(REFIID, void** ppParent).</summary>
        /// <remarks>
        /// The IID is logged rather than checked. DXGI has exactly one parent per object, so
        /// a caller asking for the wrong interface on it would be a bug in the caller, and
        /// seeing which IID was asked for is worth more here than refusing it. This image is
        /// the reason: it asks for the adapter with an IID one nibble off the documented
        /// IID_IDXGIAdapter, which a strict check would have refused for no benefit.
        /// </remarks>
        private void ReturnParent(long parent, string what)
        {
            Write(Arg(2), (uint)parent);
            Note($"GetParent({{{ReadGuid(Arg(1))}}}) -> {what} 0x{parent:X8}");
            Return(HResultOk);
        }

        private long CreateDxgiDevice(ComObject device)
        {
            // IDXGIDevice: GetAdapter 7, CreateSurface 8, QueryResourceResidency 9,
            // SetGPUThreadPriority 10, GetGPUThreadPriority 11. IDXGIDevice1 adds
            // SetMaximumFrameLatency 12 and GetMaximumFrameLatency 13; IDXGIDevice2 adds
            // OfferResources 14, ReclaimResources 15, EnqueueSetEvent 16.
            Dictionary<int, (string, Action)> methods = new()
            {
                [SlotGetParent] = ("GetParent", () => ReturnParent(AdapterPointer(), "adapter")),
                [DxgiObjectSlots + 0] = ("GetAdapter", () =>
                {
                    long pointer = AdapterPointer();
                    Write(Arg(1), (uint)pointer);
                    Note($"IDXGIDevice::GetAdapter -> 0x{pointer:X8}");
                    Return(HResultOk);
                }),
                [DxgiObjectSlots + 3] = ("SetGPUThreadPriority", () => Return(HResultOk)),
                [DxgiObjectSlots + 5] = ("SetMaximumFrameLatency", () => Return(HResultOk)),
                [DxgiObjectSlots + 6] = ("GetMaximumFrameLatency", () =>
                {
                    Write(Arg(1), 1);
                    Return(HResultOk);
                }),
            };

            return CreateInterface(device, "IDXGIDevice2", 17, methods);
        }

        private ComObject? _adapter;
        private ComObject? _factory;
        private ComObject? _swapChain;

        private long AdapterPointer()
        {
            if (_adapter is not null)
            {
                return _adapter.Interfaces["IDXGIAdapter"];
            }

            _adapter = NewObject("Adapter");
            Dictionary<int, (string, Action)> methods = new()
            {
                [SlotGetParent] = ("GetParent", () => ReturnParent(FactoryPointer(), "factory")),
                [DxgiObjectSlots + 0] = ("EnumOutputs", () => Return(DxgiNotFound)),
                [DxgiObjectSlots + 1] = ("GetDesc", () =>
                {
                    // DXGI_ADAPTER_DESC opens with WCHAR Description[128].
                    if (Arg(1) != 0)
                    {
                        _emulator.WriteMemory(Arg(1), new byte[256 + 40]);
                    }

                    Return(HResultOk);
                }),
                [DxgiObjectSlots + 2] = ("CheckInterfaceSupport", () => Return(HResultOk)),
            };

            return CreateInterface(_adapter, "IDXGIAdapter", 12, methods);
        }

        private long FactoryPointer()
        {
            if (_factory is not null)
            {
                return _factory.Interfaces["IDXGIFactory2"];
            }

            _factory = NewObject("Factory");

            // IDXGIFactory: EnumAdapters 7, MakeWindowAssociation 8, GetWindowAssociation 9,
            // CreateSwapChain 10, CreateSoftwareAdapter 11. IDXGIFactory1: EnumAdapters1 12,
            // IsCurrent 13. IDXGIFactory2: IsWindowedStereoEnabled 14,
            // CreateSwapChainForHwnd 15, CreateSwapChainForCoreWindow 16, and on to
            // CreateSwapChainForComposition at 24.
            Dictionary<int, (string, Action)> methods = new()
            {
                [7] = ("EnumAdapters", EnumAdapters),
                [10] = ("CreateSwapChain", () => CreateSwapChain(outIndex: 3, forWindow: false)),
                [12] = ("EnumAdapters1", EnumAdapters),
                [13] = ("IsCurrent", () => Return(1)),
                [14] = ("IsWindowedStereoEnabled", () => Return(0)),
                [15] = ("CreateSwapChainForHwnd", () => CreateSwapChain(outIndex: 6, forWindow: false)),
                [16] = ("CreateSwapChainForCoreWindow", () => CreateSwapChain(outIndex: 5, forWindow: true)),
                [24] = ("CreateSwapChainForComposition", () => CreateSwapChain(outIndex: 4, forWindow: false)),
            };

            return CreateInterface(_factory, "IDXGIFactory2", 25, methods);
        }

        private void EnumAdapters()
        {
            // Enumeration ends by returning DXGI_ERROR_NOT_FOUND, so index zero is the only
            // one that succeeds. Answering every index would spin the caller's loop forever.
            if (Arg(1) != 0)
            {
                Return(DxgiNotFound);
                return;
            }

            Write(Arg(2), (uint)AdapterPointer());
            Return(HResultOk);
        }

        /// <summary>
        /// HRESULT CreateSwapChainForCoreWindow(pDevice, pWindow, pDesc, pRestrictToOutput,
        /// ppSwapChain) - five arguments, so the out-parameter is the first stack slot.
        /// </summary>
        private void CreateSwapChain(int outIndex, bool forWindow)
        {
            long outPointer = Arg(outIndex);
            _swapChain ??= NewObject("SwapChain");

            long pointer = _swapChain.Interfaces.TryGetValue("IDXGISwapChain1", out long existing)
                ? existing
                : CreateSwapChainInterface(_swapChain);

            Write(outPointer, (uint)pointer);
            SwapChainCreated = true;
            long desc = Arg(outIndex - 2);
            string asked = desc == 0
                ? "no desc"
                : $"desc {_emulator.ReadUInt32(desc, 0)}x{_emulator.ReadUInt32(desc + 4, 0)} " +
                  $"fmt {_emulator.ReadUInt32(desc + 8, 0)}";

            Note(forWindow
                ? $"CreateSwapChainForCoreWindow(window=0x{Arg(1):X8}, {asked}) -> 0x{pointer:X8}"
                : $"CreateSwapChain({asked}) -> 0x{pointer:X8}");
            Return(HResultOk);
        }

        private long CreateSwapChainInterface(ComObject owner)
        {
            // IDXGIDeviceSubObject adds GetDevice at 7. IDXGISwapChain: Present 8,
            // GetBuffer 9, SetFullscreenState 10, GetFullscreenState 11, GetDesc 12,
            // ResizeBuffers 13, ResizeTarget 14, GetContainingOutput 15,
            // GetFrameStatistics 16, GetLastPresentCount 17. IDXGISwapChain1: GetDesc1 18,
            // GetFullscreenDesc 19, GetHwnd 20, GetCoreWindow 21, Present1 22, and on to
            // GetRotation at 29.
            Dictionary<int, (string, Action)> methods = new()
            {
                [SlotGetParent] = ("GetParent", () => ReturnParent(FactoryPointer(), "factory")),
                [7] = ("GetDevice", () =>
                {
                    Write(Arg(2), (uint)_objects.First(o => o.Name == "Device").Interfaces["ID3D11Device1"]);
                    Return(HResultOk);
                }),
                [8] = ("Present", Present),
                [9] = ("GetBuffer", () =>
                {
                    // HRESULT GetBuffer(UINT Buffer, REFIID, void** ppSurface).
                    long pointer = BackBufferPointer();
                    Write(Arg(3), (uint)pointer);
                    Note($"IDXGISwapChain::GetBuffer({Arg(1)}, {{{ReadGuid(Arg(2))}}}) -> 0x{pointer:X8}");
                    Return(HResultOk);
                }),
                [11] = ("GetFullscreenState", () =>
                {
                    Write(Arg(1), 0); // never full screen; a phone has no such concept
                    Write(Arg(2), 0);
                    Return(HResultOk);
                }),
                [12] = ("GetDesc", SwapChainDesc),
                [13] = ("ResizeBuffers", () => Return(HResultOk)),
                [18] = ("GetDesc1", SwapChainDesc1),
                [22] = ("Present1", Present),
            };

            return CreateInterface(owner, "IDXGISwapChain1", 30, methods);
        }

        /// <summary>
        /// DXGI_SWAP_CHAIN_DESC: a DXGI_MODE_DESC (Width, Height, RefreshRate numerator and
        /// denominator, Format, ScanlineOrdering, Scaling), then SampleDesc, BufferUsage,
        /// BufferCount, OutputWindow, Windowed, SwapEffect, Flags.
        /// </summary>
        private void SwapChainDesc()
        {
            long desc = Arg(1);
            if (desc == 0)
            {
                return;
            }

            _emulator.WriteUInt32(desc + 0, BackBufferWidth);
            _emulator.WriteUInt32(desc + 4, BackBufferHeight);
            _emulator.WriteUInt32(desc + 8, 60);
            _emulator.WriteUInt32(desc + 12, 1);
            _emulator.WriteUInt32(desc + 16, BackBufferFormat);
            _emulator.WriteUInt32(desc + 20, 0);
            _emulator.WriteUInt32(desc + 24, 0);
            _emulator.WriteUInt32(desc + 28, 1); // SampleDesc.Count
            _emulator.WriteUInt32(desc + 32, 0);
            _emulator.WriteUInt32(desc + 36, 0x20); // DXGI_USAGE_RENDER_TARGET_OUTPUT
            _emulator.WriteUInt32(desc + 40, 2); // BufferCount
            _emulator.WriteUInt32(desc + 44, 0); // OutputWindow - none, this is a CoreWindow
            _emulator.WriteUInt32(desc + 48, 1); // Windowed
            _emulator.WriteUInt32(desc + 52, 3); // FLIP_SEQUENTIAL, the WP8 swap effect
            _emulator.WriteUInt32(desc + 56, 0);
        }

        /// <summary>
        /// DXGI_SWAP_CHAIN_DESC1: Width, Height, Format, Stereo, SampleDesc, BufferUsage,
        /// BufferCount, Scaling, SwapEffect, AlphaMode, Flags.
        /// </summary>
        private void SwapChainDesc1()
        {
            long desc = Arg(1);
            if (desc == 0)
            {
                return;
            }

            _emulator.WriteUInt32(desc + 0, BackBufferWidth);
            _emulator.WriteUInt32(desc + 4, BackBufferHeight);
            _emulator.WriteUInt32(desc + 8, BackBufferFormat);
            _emulator.WriteUInt32(desc + 12, 0);
            _emulator.WriteUInt32(desc + 16, 1);
            _emulator.WriteUInt32(desc + 20, 0);
            _emulator.WriteUInt32(desc + 24, 0x20);
            _emulator.WriteUInt32(desc + 28, 2);
            _emulator.WriteUInt32(desc + 32, 0);
            _emulator.WriteUInt32(desc + 36, 3);
            _emulator.WriteUInt32(desc + 40, 0);
            _emulator.WriteUInt32(desc + 44, 0);
        }

        private void Present()
        {
            PresentCount++;
            if (PresentCount == 1)
            {
                Note($"FIRST FRAME PRESENTED: {FrameDescription()}");
            }

            CaptureIfWanted();

            // A live host wants every frame, not a photograph of one. Rasterising here is a
            // few milliseconds for a 2D title, and it runs on the emulator's thread - the
            // subscriber gets a private copy and must marshal it to wherever it draws.
            if (FramePresented is { } subscriber)
            {
                try
                {
                    subscriber(Frame.Rasterise(_emulator, out _), FrameCapture.Width, FrameCapture.Height);
                }
                catch (Exception ex)
                {
                    Note($"frame delivery failed: {ex.Message}");
                }
            }

            Return(HResultOk);
        }

        /// <summary>Raised with the rasterised RGBA pixels of each presented frame.</summary>
        public event Action<byte[], int, int>? FramePresented;

        /// <summary>Which frame to photograph, or zero for none.</summary>
        public int ScreenshotFrame { get; set; }

        /// <summary>
        /// Photograph every this many frames from <see cref="ScreenshotFrame"/> on, or zero
        /// for a single shot.
        /// </summary>
        /// <remarks>
        /// Driving a game blind through its own menus means guessing where to tap and finding
        /// out several minutes later whether the guess was right. A run takes minutes and a
        /// frame takes milliseconds, so taking one picture per run is the wrong trade by three
        /// orders of magnitude: a strided capture turns each attempt into a contact sheet.
        /// </remarks>
        public int ScreenshotEvery { get; set; }

        /// <summary>How many frames have been written.</summary>
        public int ScreenshotsWritten { get; private set; }

        private void CaptureIfWanted()
        {
            if (ScreenshotPath is null || PresentCount < ScreenshotFrame)
            {
                return;
            }

            if (ScreenshotEvery <= 0)
            {
                if (ScreenshotSummary is not null)
                {
                    return;
                }
            }
            else if ((PresentCount - ScreenshotFrame) % ScreenshotEvery != 0)
            {
                return;
            }

            string path = ScreenshotEvery <= 0
                ? ScreenshotPath
                : Path.Combine(
                    Path.GetDirectoryName(ScreenshotPath) is { Length: > 0 } directory ? directory : ".",
                    $"{Path.GetFileNameWithoutExtension(ScreenshotPath)}-{PresentCount:D6}" +
                    Path.GetExtension(ScreenshotPath));

            try
            {
                byte[] pixels = Frame.Rasterise(_emulator, out string summary);
                FrameCapture.WritePng(path, pixels, FrameCapture.Width, FrameCapture.Height);
                ScreenshotsWritten++;
                ScreenshotSummary = $"frame {PresentCount}: {summary}";
                Note($"captured frame {PresentCount} to {path} - {summary}");
            }
            catch (Exception ex)
            {
                ScreenshotSummary = $"capture failed: {ex.Message}";
            }
        }

        private string FrameDescription()
        {
            string clear = LastClearColour is null
                ? "no clear"
                : $"cleared to ({LastClearColour[0]:0.00},{LastClearColour[1]:0.00}," +
                  $"{LastClearColour[2]:0.00},{LastClearColour[3]:0.00})";
            return $"{clear}, {DrawCalls} draw(s), {RenderTargetsBound} render target(s) bound";
        }

        private long BackBufferPointer()
        {
            if (_backBufferPointer != 0)
            {
                return _backBufferPointer;
            }

            ComObject backBuffer = NewObject("Texture2D_BackBuffer");
            _backBufferPointer = CreateInterface(
                backBuffer, "ID3D11Texture2D", ResourceSlots, ResourceMethods());
            return _backBufferPointer;
        }

        /// <summary>A one-line summary of how far graphics got.</summary>
        public string Summary()
        {
            string clear = LastClearColour is null
                ? "no clear"
                : $"clear=({LastClearColour[0]:0.00},{LastClearColour[1]:0.00}," +
                  $"{LastClearColour[2]:0.00},{LastClearColour[3]:0.00})";
            string viewport = Viewport is null
                ? "no viewport"
                : $"viewport={Viewport.Value.Width:0}x{Viewport.Value.Height:0}";

            return $"device={(DeviceCreated ? "yes" : "no")} " +
                   $"swapchain={(SwapChainCreated ? "yes" : "no")} " +
                   $"presents={PresentCount} draws={DrawCalls} clears={ClearCount} " +
                   $"maps={MapCount} uploads={TextureUploads} {viewport} {clear}";
        }
    }
}
