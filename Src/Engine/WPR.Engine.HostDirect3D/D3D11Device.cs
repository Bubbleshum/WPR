using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// ID3D11Device1 at feature level 9_3 (what a WP8 phone was, and what the replay can run: each
    /// shader's Aon9 program), with an IDXGIDevice2 face. One per launch; its immediate context
    /// records what is drawn.
    /// </summary>
    internal sealed unsafe class HostDevice : HostObject
    {
        private static readonly string[] Names =
        [
            "QueryInterface", "AddRef", "Release", "CreateBuffer", "CreateTexture1D", "CreateTexture2D", "CreateTexture3D",
            "CreateShaderResourceView", "CreateUnorderedAccessView", "CreateRenderTargetView", "CreateDepthStencilView",
            "CreateInputLayout", "CreateVertexShader", "CreateGeometryShader", "CreateGeometryShaderWithStreamOutput",
            "CreatePixelShader", "CreateHullShader", "CreateDomainShader", "CreateComputeShader", "CreateClassLinkage",
            "CreateBlendState", "CreateDepthStencilState", "CreateRasterizerState", "CreateSamplerState", "CreateQuery",
            "CreatePredicate", "CreateCounter", "CreateDeferredContext", "OpenSharedResource", "CheckFormatSupport",
            "CheckMultisampleQualityLevels", "CheckCounterInfo", "CheckCounter", "CheckFeatureSupport", "GetPrivateData",
            "SetPrivateData", "SetPrivateDataInterface", "GetFeatureLevel", "GetCreationFlags", "GetDeviceRemovedReason",
            "GetImmediateContext", "SetExceptionMode", "GetExceptionMode", "GetImmediateContext1", "CreateDeferredContext1",
            "CreateBlendState1", "CreateRasterizerState1", "CreateDeviceContextState", "OpenSharedResource1", "OpenSharedResourceByName",
        ];

        private static readonly VTable Table = Build();
        private static readonly VTable DxgiTable = BuildDxgi();
        private readonly Dictionary<Guid, byte[]> _privateData = new();

        public HostDevice() : base(Table, Iid.ID3D11Device, Iid.ID3D11Device1)
        {
            AddFace(DxgiTable, Iid.IDXGIDevice, Iid.IDXGIDevice1, Iid.IDXGIDevice2, Iid.IDXGIObject);
            Context = new HostContext(this);
            Adapter = new HostAdapter();
        }

        public HostContext Context { get; }

        public HostAdapter Adapter { get; }

        /// <summary>State objects by content, so equal descs share one object (as Direct3D does).</summary>
        internal Dictionary<string, HostState> States { get; } = new();

        public const int FeatureLevel = 0x9300;

        private static VTable Build()
        {
            var s = new Slots
            {
                [3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, IntPtr*, int>)&CreateBuffer,
                [5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, IntPtr*, int>)&CreateTexture2D,
                [7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, IntPtr*, int>)&CreateShaderResourceView,
                [9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, IntPtr*, int>)&CreateRenderTargetView,
                [10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, IntPtr*, int>)&CreateDepthStencilView,
                [11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, byte*, nuint, IntPtr*, int>)&CreateInputLayout,
                [12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreateVertexShader,
                [15] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)&CreatePixelShader,
                [20] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)&CreateBlendState,
                [21] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)&CreateDepthStencilState,
                [22] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)&CreateRasterizerState,
                [23] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)&CreateSamplerState,
                [24] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)&CreateQuery,
                [29] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)&CheckFormatSupport,
                [30] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint, uint*, int>)&CheckMultisampleQualityLevels,
                [33] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, byte*, uint, int>)&CheckFeatureSupport,
                [34] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, void*, int>)&GetPrivateData,
                [35] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, void*, int>)&SetPrivateData,
                [36] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)&SetPrivateDataInterface,
                [37] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&GetFeatureLevel,
                [38] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&GetCreationFlags,
                [39] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&GetDeviceRemovedReason,
                [40] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)&GetImmediateContext,
                [41] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&SetExceptionMode,
                [42] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&GetExceptionMode,
                [43] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)&GetImmediateContext,
                [45] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)&CreateBlendState1,
                [46] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)&CreateRasterizerState,
            };
            return new VTable("ID3D11Device1", 50, s, Names);
        }

        private static int Created(IntPtr* result, HostObject value)
        {
            if (result != null)
            {
                *result = value.Pointer;
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateBuffer(IntPtr self, byte* desc, byte* initial, IntPtr* buffer)
        {
            HostDevice device = From<HostDevice>(self);
            byte* bits = initial != null ? *(byte**)initial : null;
            return buffer == null ? 1 : Created(buffer, new HostBuffer(device, desc, bits));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateTexture2D(IntPtr self, byte* desc, byte* initial, IntPtr* texture)
            => texture == null ? 1 : Created(texture, new HostTexture2D(From<HostDevice>(self), desc, initial));

        private static int CreateView(IntPtr self, IntPtr resource, byte* desc, IntPtr* view, HostView.Kind kind)
        {
            if (FromOrNull<HostTexture2D>(resource) is not { } texture)
            {
                HostLog.WriteOnce("view" + kind, $"a {kind} view of something other than a 2D texture is not supported");
                return unchecked((int)0x80070057);
            }

            return view == null ? 1 : Created(view, new HostView(From<HostDevice>(self), kind, texture, desc));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateShaderResourceView(IntPtr self, IntPtr resource, byte* desc, IntPtr* view)
            => CreateView(self, resource, desc, view, HostView.Kind.ShaderResource);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateRenderTargetView(IntPtr self, IntPtr resource, byte* desc, IntPtr* view)
            => CreateView(self, resource, desc, view, HostView.Kind.RenderTarget);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateDepthStencilView(IntPtr self, IntPtr resource, byte* desc, IntPtr* view)
            => CreateView(self, resource, desc, view, HostView.Kind.DepthStencil);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateInputLayout(IntPtr self, byte* elements, uint count, byte* bytecode, nuint length, IntPtr* layout)
            => layout == null ? 1 : Created(layout, new HostInputLayout(From<HostDevice>(self), elements, (int)count));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateVertexShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => shader == null ? 1 : Created(shader, new HostShader(From<HostDevice>(self), pixel: false, bytecode, length));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreatePixelShader(IntPtr self, byte* bytecode, nuint length, IntPtr linkage, IntPtr* shader)
            => shader == null ? 1 : Created(shader, new HostShader(From<HostDevice>(self), pixel: true, bytecode, length));

        private static int CreateState(IntPtr self, byte* desc, IntPtr* state, HostState.Kind kind)
            => state == null ? 1 : Created(state, HostState.Create(From<HostDevice>(self), kind, desc));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateBlendState(IntPtr self, byte* desc, IntPtr* state) => CreateState(self, desc, state, HostState.Kind.Blend);

        /// <summary>D3D11_BLEND_DESC1: render targets carry LogicOpEnable/LogicOp too; reshaped to D3D11_BLEND_DESC.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateBlendState1(IntPtr self, byte* desc1, IntPtr* state)
        {
            byte* desc = stackalloc byte[264];
            new ReadOnlySpan<byte>(desc1, 8).CopyTo(new Span<byte>(desc, 8));
            for (int i = 0; i < 8; i++)
            {
                byte* from = desc1 + 8 + (i * 40);
                byte* to = desc + 8 + (i * 32);
                *(int*)to = *(int*)from;                                              // BlendEnable
                new ReadOnlySpan<byte>(from + 8, 24).CopyTo(new Span<byte>(to + 4, 24)); // Src..BlendOpAlpha
                *(int*)(to + 28) = *(int*)(from + 36);                                // write mask
            }

            return CreateState(self, desc, state, HostState.Kind.Blend);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateDepthStencilState(IntPtr self, byte* desc, IntPtr* state) => CreateState(self, desc, state, HostState.Kind.DepthStencil);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateRasterizerState(IntPtr self, byte* desc, IntPtr* state) => CreateState(self, desc, state, HostState.Kind.Rasterizer);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateSamplerState(IntPtr self, byte* desc, IntPtr* state) => CreateState(self, desc, state, HostState.Kind.Sampler);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateQuery(IntPtr self, byte* desc, IntPtr* query) => query == null ? 1 : Created(query, new HostQuery(From<HostDevice>(self), desc));

        /// <summary>Everything a 9_3 device does with a texture format, for any format the host can store.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CheckFormatSupport(IntPtr self, int format, uint* support)
        {
            *support = 0x0FFFFFFF;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CheckMultisampleQualityLevels(IntPtr self, int format, uint count, uint* levels)
        {
            *levels = count <= 1 ? 1u : 0u;
            return 0;
        }

        /// <summary>Every optional feature answered "not supported" (zeroed), which a 9_3 device is.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CheckFeatureSupport(IntPtr self, int feature, byte* data, uint size)
        {
            new Span<byte>(data, (int)size).Clear();
            if (feature == 2 && size >= 4)
            {
                // D3D11_FEATURE_FORMAT_SUPPORT: keep the format, answer its support.
                *(uint*)(data + 4) = 0x0FFFFFFF;
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetPrivateData(IntPtr self, Guid* guid, uint* size, void* data)
        {
            HostDevice device = From<HostDevice>(self);
            if (!device._privateData.TryGetValue(*guid, out byte[]? stored))
            {
                *size = 0;
                return unchecked((int)0x887A0002);
            }

            if (data != null)
            {
                stored.AsSpan(0, (int)Math.Min(*size, (uint)stored.Length)).CopyTo(new Span<byte>(data, (int)*size));
            }

            *size = (uint)stored.Length;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetPrivateData(IntPtr self, Guid* guid, uint size, void* data)
        {
            HostDevice device = From<HostDevice>(self);
            if (data == null)
            {
                device._privateData.Remove(*guid);
            }
            else
            {
                device._privateData[*guid] = new ReadOnlySpan<byte>(data, (int)size).ToArray();
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetPrivateDataInterface(IntPtr self, Guid* guid, IntPtr value) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetFeatureLevel(IntPtr self) => FeatureLevel;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint GetCreationFlags(IntPtr self) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetDeviceRemovedReason(IntPtr self) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetImmediateContext(IntPtr self, IntPtr* context) => Out(context, From<HostDevice>(self).Context);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetExceptionMode(IntPtr self, uint flags) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint GetExceptionMode(IntPtr self) => 0;

        // ---- IDXGIDevice2 face ----

        private static VTable BuildDxgi()
        {
            var s = new Slots
            {
                [3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, void*, int>)&DxgiOk3,
                [4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)&DxgiOk2,
                [6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&GetParent,
                [7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetAdapter,
                [10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&SetGpuThreadPriority,
                [11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetGpuThreadPriority,
                [12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&SetMaximumFrameLatency,
                [13] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetMaximumFrameLatency,
            };
            return new VTable("IDXGIDevice2", 17, s,
            [
                "QueryInterface", "AddRef", "Release", "SetPrivateData", "SetPrivateDataInterface", "GetPrivateData", "GetParent",
                "GetAdapter", "CreateSurface", "QueryResourceResidency", "SetGPUThreadPriority", "GetGPUThreadPriority",
                "SetMaximumFrameLatency", "GetMaximumFrameLatency", "OfferResources", "ReclaimResources", "EnqueueSetEvent",
            ]);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int DxgiOk3(IntPtr self, Guid* guid, uint size, void* data) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int DxgiOk2(IntPtr self, Guid* guid, IntPtr value) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetParent(IntPtr self, Guid* iid, IntPtr* parent) => HostAdapter.Query(From<HostDevice>(self).Adapter, iid, parent);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetAdapter(IntPtr self, IntPtr* adapter)
        {
            Out(adapter, From<HostDevice>(self).Adapter);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetGpuThreadPriority(IntPtr self, int priority) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetGpuThreadPriority(IntPtr self, int* priority)
        {
            *priority = 0;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetMaximumFrameLatency(IntPtr self, uint latency) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetMaximumFrameLatency(IntPtr self, uint* latency)
        {
            *latency = 3;
            return 0;
        }
    }

    /// <summary>IDXGIAdapter2 for the host GPU: described as a phone's, with no outputs.</summary>
    internal sealed unsafe class HostAdapter : HostObject
    {
        private static readonly VTable Table = Build();

        public HostAdapter() : base(Table, Iid.IDXGIAdapter, Iid.IDXGIAdapter1, Iid.IDXGIAdapter2, Iid.IDXGIObject)
        {
        }

        internal HostFactory? Factory { get; set; }

        /// <summary>Hands out <paramref name="adapter"/> (or its factory) through a QueryInterface-shaped call.</summary>
        public static int Query(HostObject target, Guid* iid, IntPtr* result)
        {
            IntPtr pointer = target.Pointer;
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)pointer)[0])(pointer, iid, result);
        }

        private static VTable Build()
        {
            var s = new Slots
            {
                [3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, void*, int>)&Ok3,
                [4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)&Ok2,
                [6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&GetParent,
                [7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&EnumOutputs,
                [8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)&GetDesc,
                [9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, long*, int>)&CheckInterfaceSupport,
                [10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)&GetDesc,
                [11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)&GetDesc,
            };
            return new VTable("IDXGIAdapter2", 12, s);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Ok3(IntPtr self, Guid* guid, uint size, void* data) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Ok2(IntPtr self, Guid* guid, IntPtr value) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetParent(IntPtr self, Guid* iid, IntPtr* parent)
        {
            HostAdapter adapter = From<HostAdapter>(self);
            adapter.Factory ??= new HostFactory(adapter);
            return Query(adapter.Factory, iid, parent);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int EnumOutputs(IntPtr self, uint index, IntPtr* output)
        {
            *output = IntPtr.Zero;
            return unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND
        }

        /// <summary>DXGI_ADAPTER_DESC(1/2): a name, a phone GPU's ids, 256 MB of shared memory.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetDesc(IntPtr self, byte* desc)
        {
            new Span<byte>(desc, 304).Clear();
            "WPR Direct3D 11 (9_3)".AsSpan().CopyTo(new Span<char>(desc, 128));
            *(uint*)(desc + 256) = 0x5143; // Qualcomm
            *(nuint*)(desc + 272 + (2 * sizeof(nuint))) = 256 * 1024 * 1024;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CheckInterfaceSupport(IntPtr self, Guid* iid, long* version)
        {
            *version = 0;
            return 0;
        }
    }

    /// <summary>IDXGIFactory2 with one adapter. Swap chains are not made here: WP8 titles present through the host.</summary>
    internal sealed unsafe class HostFactory : HostObject
    {
        private static readonly VTable Table = Build();

        public HostFactory(HostAdapter adapter) : base(Table, Iid.IDXGIFactory, Iid.IDXGIFactory1, Iid.IDXGIFactory2, Iid.IDXGIObject)
        {
            Adapter = adapter;
            adapter.Factory ??= this;
        }

        public HostAdapter Adapter { get; }

        private static VTable Build()
        {
            var s = new Slots
            {
                [7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&EnumAdapters,
                [12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&EnumAdapters,
                [13] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&IsCurrent,
            };
            return new VTable("IDXGIFactory2", 25, s);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int EnumAdapters(IntPtr self, uint index, IntPtr* adapter)
        {
            if (index != 0)
            {
                *adapter = IntPtr.Zero;
                return unchecked((int)0x887A0002);
            }

            Out(adapter, From<HostFactory>(self).Adapter);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int IsCurrent(IntPtr self) => 1;
    }
}
