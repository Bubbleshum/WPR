using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WPR.Wp8Native;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// ID3D11DeviceContext1, immediate. Keeps what is bound and turns each clear and draw into a
    /// <see cref="GpuCommand"/> with copies of everything it used (buffers are replaced, never
    /// written in place, so a copy is the array itself). <see cref="TakeFrame"/> hands the
    /// commands to the replay.
    /// </summary>
    internal sealed unsafe class HostContext : DeviceChild
    {
        private static readonly string[] Names = BuildNames();
        private static readonly VTable Table = Build();

        private readonly List<GpuCommand> _commands = new();
        private readonly HostBuffer?[] _vsConstants = new HostBuffer?[15];
        private readonly HostBuffer?[] _psConstants = new HostBuffer?[15];
        private readonly (int First, int Count)[] _vsRanges = new (int, int)[15];
        private readonly (int First, int Count)[] _psRanges = new (int, int)[15];
        private readonly HostView?[] _psResources = new HostView?[16];
        private readonly HostState?[] _psSamplers = new HostState?[16];
        private readonly (HostBuffer? Buffer, int Stride, int Offset)[] _vertexBuffers = new (HostBuffer?, int, int)[16];
        private HostShader? _vertexShader;
        private HostShader? _pixelShader;
        private HostInputLayout? _layout;
        private HostBuffer? _indexBuffer;
        private int _indexFormat = 57;
        private int _indexOffset;
        private int _topology;
        private HostView? _target;
        private HostView? _depth;
        private HostState? _blend;
        private float[] _blendFactor = [1f, 1f, 1f, 1f];
        private uint _sampleMask = uint.MaxValue;
        private HostState? _depthState;
        private int _stencilRef;
        private HostState? _raster;
        private float[]? _viewport;
        private int[]? _scissor;

        public HostContext(HostDevice device) : base(device, Table, Iid.ID3D11DeviceContext, Iid.ID3D11DeviceContext1)
        {
        }

        /// <summary>The commands recorded since the last call, as one frame; null when nothing was recorded.</summary>
        public GpuFrame? TakeFrame()
        {
            if (_commands.Count == 0)
            {
                return null;
            }

            var frame = new GpuFrame { Commands = _commands.ToArray() };
            _commands.Clear();
            return frame;
        }

        public int Recorded => _commands.Count;

        private static string[] BuildNames()
        {
            var names = new string[135];
            string[] known =
            [
                "QueryInterface", "AddRef", "Release", "GetDevice", "GetPrivateData", "SetPrivateData", "SetPrivateDataInterface",
                "VSSetConstantBuffers", "PSSetShaderResources", "PSSetShader", "PSSetSamplers", "VSSetShader", "DrawIndexed", "Draw",
                "Map", "Unmap", "PSSetConstantBuffers", "IASetInputLayout", "IASetVertexBuffers", "IASetIndexBuffer",
                "DrawIndexedInstanced", "DrawInstanced", "GSSetConstantBuffers", "GSSetShader", "IASetPrimitiveTopology",
                "VSSetShaderResources", "VSSetSamplers", "Begin", "End", "GetData", "SetPredication", "GSSetShaderResources",
                "GSSetSamplers", "OMSetRenderTargets", "OMSetRenderTargetsAndUnorderedAccessViews", "OMSetBlendState",
                "OMSetDepthStencilState", "SOSetTargets", "DrawAuto", "DrawIndexedInstancedIndirect", "DrawInstancedIndirect",
                "Dispatch", "DispatchIndirect", "RSSetState", "RSSetViewports", "RSSetScissorRects", "CopySubresourceRegion",
                "CopyResource", "UpdateSubresource", "CopyStructureCount", "ClearRenderTargetView", "ClearUnorderedAccessViewUint",
                "ClearUnorderedAccessViewFloat", "ClearDepthStencilView", "GenerateMips", "SetResourceMinLOD", "GetResourceMinLOD",
                "ResolveSubresource", "ExecuteCommandList", "HSSetShaderResources", "HSSetShader", "HSSetSamplers",
                "HSSetConstantBuffers", "DSSetShaderResources", "DSSetShader", "DSSetSamplers", "DSSetConstantBuffers",
                "CSSetShaderResources", "CSSetUnorderedAccessViews", "CSSetShader", "CSSetSamplers", "CSSetConstantBuffers",
                "VSGetConstantBuffers", "PSGetShaderResources", "PSGetShader", "PSGetSamplers", "VSGetShader", "PSGetConstantBuffers",
                "IAGetInputLayout", "IAGetVertexBuffers", "IAGetIndexBuffer", "GSGetConstantBuffers", "GSGetShader",
                "IAGetPrimitiveTopology", "VSGetShaderResources", "VSGetSamplers", "GetPredication", "GSGetShaderResources",
                "GSGetSamplers", "OMGetRenderTargets", "OMGetRenderTargetsAndUnorderedAccessViews", "OMGetBlendState",
                "OMGetDepthStencilState", "SOGetTargets", "RSGetState", "RSGetViewports", "RSGetScissorRects",
                "HSGetShaderResources", "HSGetShader", "HSGetSamplers", "HSGetConstantBuffers", "DSGetShaderResources",
                "DSGetShader", "DSGetSamplers", "DSGetConstantBuffers", "CSGetShaderResources", "CSGetUnorderedAccessViews",
                "CSGetShader", "CSGetSamplers", "CSGetConstantBuffers", "ClearState", "Flush", "GetType", "GetContextFlags",
                "FinishCommandList", "CopySubresourceRegion1", "UpdateSubresource1", "DiscardResource", "DiscardView",
                "VSSetConstantBuffers1", "HSSetConstantBuffers1", "DSSetConstantBuffers1", "GSSetConstantBuffers1",
                "PSSetConstantBuffers1", "CSSetConstantBuffers1", "VSGetConstantBuffers1", "HSGetConstantBuffers1",
                "DSGetConstantBuffers1", "GSGetConstantBuffers1", "PSGetConstantBuffers1", "CSGetConstantBuffers1",
                "SwapDeviceContextState", "ClearView", "DiscardView1",
            ];
            known.CopyTo(names, 0);
            return names;
        }

        private static VTable Build()
        {
            Slots s = ChildSlots();
            s[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&VSSetConstantBuffers;
            s[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&PSSetShaderResources;
            s[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)&PSSetShader;
            s[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&PSSetSamplers;
            s[11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)&VSSetShader;
            s[12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, void>)&DrawIndexed;
            s[13] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)&Draw;
            s[14] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, byte*, int>)&Map;
            s[15] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)&Unmap;
            s[16] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&PSSetConstantBuffers;
            s[17] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&IASetInputLayout;
            s[18] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)&IASetVertexBuffers;
            s[19] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, uint, void>)&IASetIndexBuffer;
            s[20] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int, uint, void>)&DrawIndexedInstanced;
            s[21] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, void>)&DrawInstanced;
            s[22] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&Ignore3;
            s[23] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)&IgnoreShader;
            s[24] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, void>)&IASetPrimitiveTopology;
            s[25] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&Ignore3;
            s[26] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&Ignore3;
            s[27] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&Ignore1;
            s[28] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&Ignore1;
            s[29] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, uint, uint, int>)&GetData;
            s[30] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void>)&SetPredication;
            s[31] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&Ignore3;
            s[32] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&Ignore3;
            s[33] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)&OMSetRenderTargets;
            s[34] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, uint, uint, IntPtr*, uint*, void>)&OMSetRenderTargetsAndUnorderedAccessViews;
            s[35] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, uint, void>)&OMSetBlendState;
            s[36] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)&OMSetDepthStencilState;
            s[37] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, void>)&SOSetTargets;
            s[43] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&RSSetState;
            s[44] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, float*, void>)&RSSetViewports;
            s[45] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int*, void>)&RSSetScissorRects;
            s[46] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, int*, void>)&CopySubresourceRegion;
            s[47] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)&CopyResource;
            s[48] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int*, byte*, uint, uint, void>)&UpdateSubresource;
            s[50] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)&ClearRenderTargetView;
            s[53] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, float, byte, void>)&ClearDepthStencilView;
            s[54] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&Ignore1;
            for (int i = 59; i <= 71; i++)
            {
                // Hull, domain and compute stages: 9_3 has none; setting nothing on them is what a title expects.
                s[i] = i is 60 or 64 or 69
                    ? (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)&IgnoreShader
                    : i == 68
                        ? (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)&Ignore4
                        : (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&Ignore3;
            }

            s[72] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&VSGetConstantBuffers;
            s[73] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&PSGetShaderResources;
            s[74] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr*, uint*, void>)&PSGetShader;
            s[75] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&PSGetSamplers;
            s[76] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr*, uint*, void>)&VSGetShader;
            s[77] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)&PSGetConstantBuffers;
            s[78] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)&IAGetInputLayout;
            s[79] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)&IAGetVertexBuffers;
            s[80] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int*, uint*, void>)&IAGetIndexBuffer;
            s[83] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, void>)&IAGetPrimitiveTopology;
            s[89] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr*, void>)&OMGetRenderTargets;
            s[91] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, float*, uint*, void>)&OMGetBlendState;
            s[92] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, void>)&OMGetDepthStencilState;
            s[94] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)&RSGetState;
            s[95] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, float*, void>)&RSGetViewports;
            s[96] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int*, void>)&RSGetScissorRects;
            // ClearState is slot 110, as Names says (VSSetConstantBuffers 7 + 103 methods). These
            // were one slot late until 2026-10-20: a Flush ran ClearState, ClearState was
            // unimplemented, and every ID3D11DeviceContext1 method sat on its neighbour's slot.
            s[110] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void>)&ClearState;
            s[111] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void>)&Flush;
            s[112] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&GetType;
            s[113] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&GetContextFlags;
            s[116] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int*, byte*, uint, uint, uint, void>)&UpdateSubresource1;
            s[117] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&Ignore1;   // DiscardResource
            s[118] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&Ignore1;   // DiscardView
            s[119] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)&VSSetConstantBuffers1;
            s[123] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)&PSSetConstantBuffers1;
            for (int i = 120; i <= 124; i++)
            {
                if (i != 123)
                {
                    s[i] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)&Ignore5;
                }
            }

            s[133] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, uint, void>)&DiscardView1;
            return new VTable("ID3D11DeviceContext1", 134, s, Names);
        }

        private static HostContext Self(IntPtr self) => From<HostContext>(self);

        // ---- binding ----

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void VSSetConstantBuffers(IntPtr self, uint start, uint count, IntPtr* buffers) => SetBuffers(Self(self)._vsConstants, Self(self)._vsRanges, start, count, buffers, null, null);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSSetConstantBuffers(IntPtr self, uint start, uint count, IntPtr* buffers) => SetBuffers(Self(self)._psConstants, Self(self)._psRanges, start, count, buffers, null, null);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void VSSetConstantBuffers1(IntPtr self, uint start, uint count, IntPtr* buffers, uint* first, uint* number)
            => SetBuffers(Self(self)._vsConstants, Self(self)._vsRanges, start, count, buffers, first, number);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSSetConstantBuffers1(IntPtr self, uint start, uint count, IntPtr* buffers, uint* first, uint* number)
            => SetBuffers(Self(self)._psConstants, Self(self)._psRanges, start, count, buffers, first, number);

        private static void SetBuffers(HostBuffer?[] slots, (int, int)[] ranges, uint start, uint count, IntPtr* buffers, uint* first, uint* number)
        {
            for (uint i = 0; i < count && start + i < slots.Length; i++)
            {
                Hold(ref slots[start + i], buffers == null ? null : FromOrNull<HostBuffer>(buffers[i]));
                ranges[start + i] = first == null ? (0, 0) : ((int)first[i], (int)number[i]);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSSetShaderResources(IntPtr self, uint start, uint count, IntPtr* views)
        {
            HostContext c = Self(self);
            for (uint i = 0; i < count && start + i < c._psResources.Length; i++)
            {
                Hold(ref c._psResources[start + i], views == null ? null : FromOrNull<HostView>(views[i]));
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSSetSamplers(IntPtr self, uint start, uint count, IntPtr* samplers)
        {
            HostContext c = Self(self);
            for (uint i = 0; i < count && start + i < c._psSamplers.Length; i++)
            {
                Hold(ref c._psSamplers[start + i], samplers == null ? null : FromOrNull<HostState>(samplers[i]));
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSSetShader(IntPtr self, IntPtr shader, IntPtr* instances, uint count) => Hold(ref Self(self)._pixelShader, FromOrNull<HostShader>(shader));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void VSSetShader(IntPtr self, IntPtr shader, IntPtr* instances, uint count) => Hold(ref Self(self)._vertexShader, FromOrNull<HostShader>(shader));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IASetInputLayout(IntPtr self, IntPtr layout) => Hold(ref Self(self)._layout, FromOrNull<HostInputLayout>(layout));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IASetVertexBuffers(IntPtr self, uint start, uint count, IntPtr* buffers, uint* strides, uint* offsets)
        {
            HostContext c = Self(self);
            for (uint i = 0; i < count && start + i < c._vertexBuffers.Length; i++)
            {
                Hold(ref c._vertexBuffers[start + i].Buffer, buffers == null ? null : FromOrNull<HostBuffer>(buffers[i]));
                c._vertexBuffers[start + i].Stride = strides == null ? 0 : (int)strides[i];
                c._vertexBuffers[start + i].Offset = offsets == null ? 0 : (int)offsets[i];
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IASetIndexBuffer(IntPtr self, IntPtr buffer, int format, uint offset)
        {
            HostContext c = Self(self);
            Hold(ref c._indexBuffer, FromOrNull<HostBuffer>(buffer));
            c._indexFormat = format;
            c._indexOffset = (int)offset;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IASetPrimitiveTopology(IntPtr self, int topology) => Self(self)._topology = topology;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OMSetRenderTargets(IntPtr self, uint count, IntPtr* views, IntPtr depth)
        {
            HostContext c = Self(self);
            Hold(ref c._target, count > 0 && views != null ? FromOrNull<HostView>(views[0]) : null);
            Hold(ref c._depth, FromOrNull<HostView>(depth));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OMSetRenderTargetsAndUnorderedAccessViews(IntPtr self, uint count, IntPtr* views, IntPtr depth, uint uavStart, uint uavCount, IntPtr* uavs, uint* initial)
        {
            if (count == uint.MaxValue)
            {
                return; // D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL
            }

            HostContext c = Self(self);
            Hold(ref c._target, count > 0 && views != null ? FromOrNull<HostView>(views[0]) : null);
            Hold(ref c._depth, FromOrNull<HostView>(depth));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OMSetBlendState(IntPtr self, IntPtr state, float* factor, uint mask)
        {
            HostContext c = Self(self);
            Hold(ref c._blend, FromOrNull<HostState>(state));
            c._blendFactor = factor == null ? [1f, 1f, 1f, 1f] : [factor[0], factor[1], factor[2], factor[3]];
            c._sampleMask = mask;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OMSetDepthStencilState(IntPtr self, IntPtr state, uint reference)
        {
            HostContext c = Self(self);
            Hold(ref c._depthState, FromOrNull<HostState>(state));
            c._stencilRef = (int)reference;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void RSSetState(IntPtr self, IntPtr state) => Hold(ref Self(self)._raster, FromOrNull<HostState>(state));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void RSSetViewports(IntPtr self, uint count, float* viewports)
            => Self(self)._viewport = count == 0 || viewports == null ? null : new ReadOnlySpan<float>(viewports, 6).ToArray();

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void RSSetScissorRects(IntPtr self, uint count, int* rects)
            => Self(self)._scissor = count == 0 || rects == null ? null : new ReadOnlySpan<int>(rects, 4).ToArray();

        // ---- getters (each hands out a reference) ----

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void VSGetConstantBuffers(IntPtr self, uint start, uint count, IntPtr* buffers) => GetSlots(Self(self)._vsConstants, start, count, buffers);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSGetConstantBuffers(IntPtr self, uint start, uint count, IntPtr* buffers) => GetSlots(Self(self)._psConstants, start, count, buffers);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSGetShaderResources(IntPtr self, uint start, uint count, IntPtr* views) => GetSlots(Self(self)._psResources, start, count, views);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSGetSamplers(IntPtr self, uint start, uint count, IntPtr* samplers) => GetSlots(Self(self)._psSamplers, start, count, samplers);

        private static void GetSlots<T>(T?[] slots, uint start, uint count, IntPtr* result) where T : HostObject
        {
            for (uint i = 0; i < count; i++)
            {
                Out(result + i, start + i < slots.Length ? slots[start + i] : null);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void PSGetShader(IntPtr self, IntPtr* shader, IntPtr* instances, uint* count)
        {
            Out(shader, Self(self)._pixelShader);
            if (count != null)
            {
                *count = 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void VSGetShader(IntPtr self, IntPtr* shader, IntPtr* instances, uint* count)
        {
            Out(shader, Self(self)._vertexShader);
            if (count != null)
            {
                *count = 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IAGetInputLayout(IntPtr self, IntPtr* layout) => Out(layout, Self(self)._layout);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IAGetVertexBuffers(IntPtr self, uint start, uint count, IntPtr* buffers, uint* strides, uint* offsets)
        {
            HostContext c = Self(self);
            for (uint i = 0; i < count; i++)
            {
                var (buffer, stride, offset) = start + i < c._vertexBuffers.Length ? c._vertexBuffers[start + i] : default;
                if (buffers != null)
                {
                    Out(buffers + i, buffer);
                }

                if (strides != null)
                {
                    strides[i] = (uint)stride;
                }

                if (offsets != null)
                {
                    offsets[i] = (uint)offset;
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IAGetIndexBuffer(IntPtr self, IntPtr* buffer, int* format, uint* offset)
        {
            HostContext c = Self(self);
            Out(buffer, c._indexBuffer);
            if (format != null)
            {
                *format = c._indexFormat;
            }

            if (offset != null)
            {
                *offset = (uint)c._indexOffset;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IAGetPrimitiveTopology(IntPtr self, int* topology) => *topology = Self(self)._topology;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OMGetRenderTargets(IntPtr self, uint count, IntPtr* views, IntPtr* depth)
        {
            HostContext c = Self(self);
            for (uint i = 0; i < count && views != null; i++)
            {
                Out(views + i, i == 0 ? c._target : null);
            }

            Out(depth, c._depth);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OMGetBlendState(IntPtr self, IntPtr* state, float* factor, uint* mask)
        {
            HostContext c = Self(self);
            Out(state, c._blend);
            if (factor != null)
            {
                c._blendFactor.CopyTo(new Span<float>(factor, 4));
            }

            if (mask != null)
            {
                *mask = c._sampleMask;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OMGetDepthStencilState(IntPtr self, IntPtr* state, uint* reference)
        {
            HostContext c = Self(self);
            Out(state, c._depthState);
            if (reference != null)
            {
                *reference = (uint)c._stencilRef;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void RSGetState(IntPtr self, IntPtr* state) => Out(state, Self(self)._raster);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void RSGetViewports(IntPtr self, uint* count, float* viewports)
        {
            HostContext c = Self(self);
            if (viewports != null && *count > 0 && c._viewport is { } v)
            {
                v.CopyTo(new Span<float>(viewports, 6));
            }

            *count = c._viewport is null ? 0u : 1u;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void RSGetScissorRects(IntPtr self, uint* count, int* rects)
        {
            HostContext c = Self(self);
            if (rects != null && *count > 0 && c._scissor is { } r)
            {
                r.CopyTo(new Span<int>(rects, 4));
            }

            *count = c._scissor is null ? 0u : 1u;
        }

        // ---- resources ----

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Map(IntPtr self, IntPtr resource, uint subresource, int mapType, uint flags, byte* mapped)
        {
            if (FromOrNull<HostObject>(resource) is HostBuffer buffer)
            {
                *(IntPtr*)mapped = buffer.Map(mapType);
                *(int*)(mapped + sizeof(IntPtr)) = buffer.Data.Length;
                *(int*)(mapped + sizeof(IntPtr) + 4) = buffer.Data.Length;
                return 0;
            }

            if (FromOrNull<HostObject>(resource) is HostTexture2D texture && subresource == 0)
            {
                *(IntPtr*)mapped = texture.Map(out int pitch);
                *(int*)(mapped + sizeof(IntPtr)) = pitch;
                *(int*)(mapped + sizeof(IntPtr) + 4) = pitch * texture.Height;
                return 0;
            }

            HostLog.WriteOnce("map" + subresource, $"Map of subresource {subresource} is not supported");
            return unchecked((int)0x80070057);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Unmap(IntPtr self, IntPtr resource, uint subresource)
        {
            switch (FromOrNull<HostObject>(resource))
            {
                case HostBuffer buffer:
                    buffer.Unmap();
                    break;
                case HostTexture2D texture when subresource == 0:
                    texture.Unmap();
                    break;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void UpdateSubresource(IntPtr self, IntPtr resource, uint subresource, int* box, byte* data, uint rowPitch, uint depthPitch)
            => Update(resource, subresource, box, data, rowPitch);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void UpdateSubresource1(IntPtr self, IntPtr resource, uint subresource, int* box, byte* data, uint rowPitch, uint depthPitch, uint flags)
            => Update(resource, subresource, box, data, rowPitch);

        private static void Update(IntPtr resource, uint subresource, int* box, byte* data, uint rowPitch)
        {
            switch (FromOrNull<HostObject>(resource))
            {
                case HostBuffer buffer:
                {
                    int left = box == null ? 0 : box[0];
                    int right = box == null ? buffer.Data.Length : box[3];
                    buffer.Write(new ReadOnlySpan<byte>(data, Math.Max(0, right - left)), left);
                    break;
                }

                case HostTexture2D texture when subresource == 0:
                    texture.Upload(data, (int)rowPitch, box == null ? 0 : box[0], box == null ? 0 : box[1],
                        box == null ? texture.Width : box[3], box == null ? texture.Height : box[4]);
                    break;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void CopyResource(IntPtr self, IntPtr destination, IntPtr source) => Copy(Self(self), destination, source, 0, 0, null);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void CopySubresourceRegion(IntPtr self, IntPtr destination, uint dstSub, uint x, uint y, uint z, IntPtr source, uint srcSub, int* box)
            => Copy(Self(self), destination, source, (int)x, (int)y, box);

        private static void Copy(HostContext c, IntPtr destination, IntPtr source, int x, int y, int* box)
        {
            if (FromOrNull<HostObject>(destination) is HostTexture2D to && FromOrNull<HostObject>(source) is HostTexture2D from && to.IsRenderTarget && from.IsRenderTarget)
            {
                c._commands.Add(new GpuCopy(to.Target, from.Target, x, y, box == null ? null : [box[0], box[1], box[3], box[4]]));
                return;
            }

            if (FromOrNull<HostObject>(destination) is HostBuffer toBuffer && FromOrNull<HostObject>(source) is HostBuffer fromBuffer)
            {
                int left = box == null ? 0 : box[0];
                int right = box == null ? fromBuffer.Data.Length : box[3];
                toBuffer.Write(fromBuffer.Data.AsSpan(left, Math.Max(0, Math.Min(right, fromBuffer.Data.Length) - left)), x);
                return;
            }

            HostLog.WriteOnce("copy", "a copy between these resources is not supported");
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetData(IntPtr self, IntPtr query, byte* data, uint size, uint flags)
        {
            if (data != null && size > 0)
            {
                new Span<byte>(data, (int)size).Clear();
                if (FromOrNull<HostQuery>(query) is { QueryType: 0 })
                {
                    *data = 1; // an event query: the GPU is done
                }
            }

            return 0;
        }

        // ---- clears and draws ----

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void ClearRenderTargetView(IntPtr self, IntPtr view, float* colour)
        {
            if (FromOrNull<HostView>(view) is { } target)
            {
                Self(self)._commands.Add(new GpuClearColour(target.Texture.Target, [colour[0], colour[1], colour[2], colour[3]]));
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void ClearDepthStencilView(IntPtr self, IntPtr view, uint flags, float depth, byte stencil)
        {
            if (FromOrNull<HostView>(view) is { } target)
            {
                Self(self)._commands.Add(new GpuClearDepth(target.Texture.Target, (int)flags, depth, stencil));
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void DiscardView1(IntPtr self, IntPtr view, int* rects, uint count)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void DrawIndexed(IntPtr self, uint count, uint start, int baseVertex) => Self(self).RecordIndexed((int)count, (int)start, baseVertex);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void DrawIndexedInstanced(IntPtr self, uint count, uint instances, uint start, int baseVertex, uint startInstance)
            => Self(self).RecordIndexed((int)count, (int)start, baseVertex);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Draw(IntPtr self, uint count, uint start) => Self(self).RecordSequence((int)count, (int)start);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void DrawInstanced(IntPtr self, uint count, uint instances, uint start, uint startInstance) => Self(self).RecordSequence((int)count, (int)start);

        private void RecordIndexed(int count, int start, int baseVertex)
        {
            if (count > 0 && _indexBuffer is { } indices)
            {
                Record(indices.Indices(_indexFormat, _indexOffset, start, count), baseVertex, count);
            }
        }

        private readonly Dictionary<(int, int), int[]> _sequences = new();

        private void RecordSequence(int count, int start)
        {
            if (count <= 0)
            {
                return;
            }

            if (!_sequences.TryGetValue((start, count), out int[]? indices))
            {
                if (_sequences.Count > 256)
                {
                    _sequences.Clear();
                }

                indices = new int[count];
                for (int i = 0; i < count; i++)
                {
                    indices[i] = start + i;
                }

                _sequences[(start, count)] = indices;
            }

            Record(indices, 0, count);
        }

        private void Record(int[] indices, int baseVertex, int count)
        {
            if (_vertexShader is not { } vertex || _pixelShader is not { } pixel || _layout is not { } layout || _target is not { } target)
            {
                HostLog.WriteOnce("draw-unbound", $"a draw with nothing bound to draw it (vs={_vertexShader is not null} ps={_pixelShader is not null} layout={_layout is not null} target={_target is not null}) was skipped");
                return;
            }

            var streams = new FrameCapture.VertexStream[_vertexBuffers.Length];
            for (int slot = 0; slot < streams.Length; slot++)
            {
                var (buffer, stride, offset) = _vertexBuffers[slot];
                streams[slot] = buffer is null
                    ? new FrameCapture.VertexStream(null, 0, 0)
                    : stride > 0 && offset % stride == 0
                        ? new FrameCapture.VertexStream(null, stride, 0) { Data = buffer.Data, FirstVertex = -(offset / stride) }
                        : new FrameCapture.VertexStream(null, stride, 0) { Data = buffer.Data.AsSpan(Math.Min(offset, buffer.Data.Length)).ToArray() };
            }

            var geometry = new FrameCapture.DrawCall(count, 0, baseVertex, streams, null, (uint)_indexFormat, 0, null, layout.Elements, null, (uint)_topology)
            {
                Indices = indices,
            };

            var textures = new Dictionary<int, GpuTexture>();
            for (int slot = 0; slot < _psResources.Length; slot++)
            {
                if (_psResources[slot]?.Texture.AsShaderInput() is { } texture)
                {
                    textures[slot] = texture;
                }
            }

            var samplers = new Dictionary<int, byte[]>();
            for (int slot = 0; slot < _psSamplers.Length; slot++)
            {
                if (_psSamplers[slot] is { } sampler)
                {
                    samplers[slot] = sampler.Raw;
                }
            }

            GpuTarget gpuTarget = target.Texture.Target;
            _commands.Add(new GpuDraw(
                vertex.Shader,
                pixel.Shader,
                Constants(_vsConstants, _vsRanges),
                Constants(_psConstants, _psRanges),
                textures,
                samplers,
                _blend?.Raw,
                _blendFactor,
                _depthState?.Raw,
                _stencilRef,
                _raster?.Raw,
                gpuTarget,
                _depth?.Texture.Target,
                _viewport ?? [0f, 0f, gpuTarget.Width, gpuTarget.Height, 0f, 1f],
                geometry,
                _scissor));
        }

        private static Dictionary<int, byte[]> Constants(HostBuffer?[] slots, (int First, int Count)[] ranges)
        {
            var constants = new Dictionary<int, byte[]>();
            for (int slot = 0; slot < slots.Length; slot++)
            {
                if (slots[slot] is not { } buffer)
                {
                    continue;
                }

                (int first, int number) = ranges[slot];
                constants[slot] = number == 0 && first == 0
                    ? buffer.Data
                    : buffer.Data.AsSpan(Math.Min(first * 16, buffer.Data.Length), Math.Min(number * 16, Math.Max(0, buffer.Data.Length - (first * 16)))).ToArray();
            }

            return constants;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void ClearState(IntPtr self)
        {
            HostContext c = Self(self);
            for (int i = 0; i < c._vsConstants.Length; i++)
            {
                Hold(ref c._vsConstants[i], null);
                Hold(ref c._psConstants[i], null);
            }

            for (int i = 0; i < c._psResources.Length; i++)
            {
                Hold(ref c._psResources[i], null);
                Hold(ref c._psSamplers[i], null);
                Hold(ref c._vertexBuffers[i].Buffer, null);
            }

            Array.Clear(c._vsRanges);
            Array.Clear(c._psRanges);
            Array.Clear(c._vertexBuffers);
            Hold(ref c._vertexShader, null);
            Hold(ref c._pixelShader, null);
            Hold(ref c._layout, null);
            Hold(ref c._indexBuffer, null);
            c._topology = 0;
            Hold(ref c._target, null);
            Hold(ref c._depth, null);
            Hold(ref c._blend, null);
            c._blendFactor = [1f, 1f, 1f, 1f];
            Hold(ref c._depthState, null);
            Hold(ref c._raster, null);
            c._viewport = null;
            c._scissor = null;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Flush(IntPtr self)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetType(IntPtr self) => 0; // D3D11_DEVICE_CONTEXT_IMMEDIATE

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint GetContextFlags(IntPtr self) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void SetPredication(IntPtr self, IntPtr predicate, int value)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void SOSetTargets(IntPtr self, uint count, IntPtr* targets, uint* offsets)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Ignore1(IntPtr self, IntPtr a)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Ignore3(IntPtr self, uint start, uint count, IntPtr* items)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Ignore4(IntPtr self, uint start, uint count, IntPtr* items, uint* extra)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Ignore5(IntPtr self, uint start, uint count, IntPtr* items, uint* a, uint* b)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void IgnoreShader(IntPtr self, IntPtr shader, IntPtr* instances, uint count)
        {
        }
    }
}
