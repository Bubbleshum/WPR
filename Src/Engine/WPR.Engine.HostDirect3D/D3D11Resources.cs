using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WPR.Wp8Native;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// The ID3D11DeviceChild part every Direct3D object shares (slots 3-6: GetDevice and private
    /// data). Private data is kept, because SharpDX's DebugName goes through it.
    /// </summary>
    internal abstract unsafe class DeviceChild : HostObject
    {
        private static long _nextKey;
        private readonly Dictionary<Guid, byte[]> _privateData = new();

        protected DeviceChild(HostDevice device, VTable vtable, params Guid[] iids)
            : base(vtable, [.. iids, Iid.ID3D11DeviceChild])
        {
            Device = device;
            Key = Interlocked.Increment(ref _nextKey);
        }

        public HostDevice Device { get; }

        /// <summary>A process-unique key: what the replay caches shaders, textures and targets by.</summary>
        public long Key { get; }

        /// <summary>Slots 3-6 for any device child's vtable.</summary>
        public static Slots ChildSlots() => new()
        {
            [3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)&GetDevice,
            [4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, void*, int>)&GetPrivateData,
            [5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, void*, int>)&SetPrivateData,
            [6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)&SetPrivateDataInterface,
        };

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetDevice(IntPtr self, IntPtr* device) => Out(device, From<DeviceChild>(self).Device);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetPrivateData(IntPtr self, Guid* guid, uint* size, void* data)
        {
            DeviceChild child = From<DeviceChild>(self);
            if (!child._privateData.TryGetValue(*guid, out byte[]? stored))
            {
                *size = 0;
                return unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND
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
            DeviceChild child = From<DeviceChild>(self);
            if (data == null)
            {
                child._privateData.Remove(*guid);
            }
            else
            {
                child._privateData[*guid] = new ReadOnlySpan<byte>(data, (int)size).ToArray();
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetPrivateDataInterface(IntPtr self, Guid* guid, IntPtr value) => 0;
    }

    /// <summary>
    /// ID3D11Buffer. Its contents are a managed array that is REPLACED, never written in place, on
    /// every Map/Unmap and UpdateSubresource: a recorded draw keeps the array it was recorded
    /// with, and the replay recognises an unchanged buffer by the array's identity.
    /// </summary>
    internal sealed unsafe class HostBuffer : DeviceChild
    {
        private static readonly VTable Table = Build();
        private readonly byte[] _desc;
        private IntPtr _scratch;

        public HostBuffer(HostDevice device, byte* desc, byte* initial) : base(device, Table, Iid.ID3D11Buffer, Iid.ID3D11Resource)
        {
            _desc = new ReadOnlySpan<byte>(desc, 24).ToArray();
            int size = Math.Max(0, *(int*)desc);
            Data = new byte[size];
            if (initial != null)
            {
                new ReadOnlySpan<byte>(initial, size).CopyTo(Data);
            }
        }

        public byte[] Data { get; private set; }

        public int BindFlags => BitConverter.ToInt32(_desc, 8);

        public IntPtr Map(int mapType)
        {
            if (_scratch == IntPtr.Zero)
            {
                _scratch = (IntPtr)NativeMemory.Alloc((nuint)Math.Max(1, Data.Length));
            }

            var span = new Span<byte>((void*)_scratch, Data.Length);
            if (mapType == 4)
            {
                span.Clear(); // WRITE_DISCARD
            }
            else
            {
                Data.CopyTo(span);
            }

            return _scratch;
        }

        public void Unmap()
        {
            var mapped = new ReadOnlySpan<byte>((void*)_scratch, Data.Length);
            if (mapped.SequenceEqual(Data))
            {
                return; // unchanged: the replay keeps everything it built from this array
            }

            byte[] next = NextArray();
            mapped.CopyTo(next);
            Replace(next);
        }

        public void Write(ReadOnlySpan<byte> bytes, int offset)
        {
            byte[] next = NextArray();
            Data.CopyTo(next, 0);
            if (offset < next.Length)
            {
                bytes[..Math.Min(bytes.Length, next.Length - offset)].CopyTo(next.AsSpan(offset));
            }

            Replace(next);
        }

        // ---- reusing replaced arrays ----
        //
        // Data is replaced, never written in place, because a recorded draw keeps the array it was
        // recorded with until its frame is replayed. A fresh array per Map was LOH churn for a big
        // dynamic buffer (Arkadium's sprite batch: ~400 KB, twice a frame): managed memory climbed
        // to ~1 GB between gen2 collections while ~250 MB was live. A replaced array is retired,
        // and reused once the frame that could have recorded it has been replayed
        // (FrameReplayed); the replay first forgets anything it cached by that array's identity
        // (Recycling).

        /// <summary>Replays completed; an array retired before the current value is free to reuse.</summary>
        private static long _replayedFrames;

        /// <summary>Called with an array about to be reused, so caches keyed by its identity drop it.</summary>
        internal static Action<byte[]>? Recycling;

        private const int RetiredKept = 4;
        private readonly List<(byte[] Array, long Frame)> _retired = new();

        /// <summary>The host replayed a frame: everything retired before now is unreferenced by draws.</summary>
        internal static void FrameReplayed() => _replayedFrames++;

        /// <summary>Back to a fresh start for a new launch.</summary>
        internal static void Reset()
        {
            _replayedFrames = 0;
            Recycling = null;
        }

        private byte[] NextArray()
        {
            for (int i = 0; i < _retired.Count; i++)
            {
                (byte[] array, long frame) = _retired[i];
                if (frame < _replayedFrames && array.Length == Data.Length)
                {
                    _retired.RemoveAt(i);
                    Recycling?.Invoke(array);
                    return array;
                }
            }

            return GC.AllocateUninitializedArray<byte>(Data.Length);
        }

        private void Replace(byte[] next)
        {
            _retired.Add((Data, _replayedFrames));
            if (_retired.Count > RetiredKept)
            {
                _retired.RemoveAt(0);
            }

            Data = next;
            _indicesFor = null;
        }

        // Indices resolved for the buffer's current contents (Cocos2D draws every sprite from one
        // six-index buffer).
        private byte[]? _indicesFor;
        private (int, int, int, int) _indicesKey;
        private int[]? _indices;

        public int[] Indices(int format, int offset, int start, int count)
        {
            var key = (offset, start, count, format);
            if (_indices is not null && ReferenceEquals(_indicesFor, Data) && _indicesKey == key)
            {
                return _indices;
            }

            int size = format == 42 /* R32_UINT */ ? 4 : 2;
            var indices = new int[Math.Max(0, count)];
            int at = offset + (start * size);
            for (int i = 0; i < indices.Length && at + size <= Data.Length; i++, at += size)
            {
                indices[i] = size == 4 ? (int)BitConverter.ToUInt32(Data, at) : BitConverter.ToUInt16(Data, at);
            }

            _indicesFor = Data;
            _indicesKey = key;
            return _indices = indices;
        }

        protected override void Destroyed()
        {
            if (_scratch != IntPtr.Zero)
            {
                NativeMemory.Free((void*)_scratch);
                _scratch = IntPtr.Zero;
            }
        }

        private static VTable Build()
        {
            Slots slots = ChildSlots();
            slots[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, void>)&GetType;
            slots[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, void>)&SetEvictionPriority;
            slots[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&GetEvictionPriority;
            slots[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&GetDesc;
            return new VTable("ID3D11Buffer", 11, slots);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetType(IntPtr self, int* type) => *type = 1; // D3D11_RESOURCE_DIMENSION_BUFFER

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void SetEvictionPriority(IntPtr self, uint priority)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint GetEvictionPriority(IntPtr self) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetDesc(IntPtr self, byte* desc) => From<HostBuffer>(self)._desc.CopyTo(new Span<byte>(desc, 24));
    }

    /// <summary>
    /// ID3D11Texture2D: mip 0 kept as RGBA (or BC blocks) for the replay, or a render target the
    /// replay owns. The host's back buffer is one with <see cref="IsBackBuffer"/>.
    /// </summary>
    internal sealed unsafe class HostTexture2D : DeviceChild
    {
        private static readonly VTable Table = Build();
        private readonly byte[] _desc;
        private byte[]? _rgba;
        private byte[]? _blocks;
        private int _version;
        private IntPtr _scratch;
        private int _scratchPitch;

        public HostTexture2D(HostDevice device, byte* desc, byte* initialData, bool backBuffer = false)
            : base(device, Table, Iid.ID3D11Texture2D, Iid.ID3D11Resource)
        {
            _desc = new ReadOnlySpan<byte>(desc, 44).ToArray();
            IsBackBuffer = backBuffer;
            if (initialData != null)
            {
                // D3D11_SUBRESOURCE_DATA: pSysMem, SysMemPitch, SysMemSlicePitch; mip 0 is the first.
                byte* bits = *(byte**)initialData;
                int pitch = *(int*)(initialData + sizeof(IntPtr));
                if (bits != null)
                {
                    Upload(bits, pitch, 0, 0, Width, Height);
                }
            }
        }

        public int Width => BitConverter.ToInt32(_desc, 0);

        public int Height => BitConverter.ToInt32(_desc, 4);

        public int Format => BitConverter.ToInt32(_desc, 16);

        public int BindFlags => BitConverter.ToInt32(_desc, 32);

        public bool IsRenderTarget => IsBackBuffer || (BindFlags & 0x20) != 0;

        public bool IsBackBuffer { get; }

        public GpuTarget Target => new(IsBackBuffer ? GpuTarget.BackBufferKey : Key, Width, Height);

        public TextureImage? Image { get; private set; }

        public GpuTexture? AsShaderInput() => IsRenderTarget ? new GpuTexture(null, Target) : Image is { } image ? new GpuTexture(image, null) : null;

        /// <summary>Writes a region of mip 0 from rows at <paramref name="pitch"/>.</summary>
        public void Upload(byte* bits, int pitch, int left, int top, int right, int bottom)
        {
            int blockFormat = TextureFormats.BlockFormat(Format);
            if (blockFormat != 0)
            {
                int blockBytes = blockFormat == 1 ? 8 : 16;
                int blocksWide = Math.Max(1, (Width + 3) / 4), blocksHigh = Math.Max(1, (Height + 3) / 4);
                _blocks ??= new byte[blocksWide * blocksHigh * blockBytes];
                int rowBytes = ((right - left + 3) / 4) * blockBytes;
                for (int by = top / 4; by < Math.Min(blocksHigh, (bottom + 3) / 4); by++)
                {
                    new ReadOnlySpan<byte>(bits + ((by - (top / 4)) * pitch), rowBytes)
                        .CopyTo(_blocks.AsSpan((by * blocksWide * blockBytes) + ((left / 4) * blockBytes)));
                }

                Image = new TextureImage(Key, ++_version, Width, Height, [], (byte[])_blocks.Clone(), blockFormat);
                return;
            }

            _rgba ??= new byte[Width * Height * 4];
            if (!TextureFormats.ToRgba(Format, bits, pitch, left, top, right, bottom, _rgba, Width))
            {
                HostLog.WriteOnce("texfmt" + Format, $"texture format {Format} is not decoded; drawn transparent");
            }

            Image = new TextureImage(Key, ++_version, Width, Height, (byte[])_rgba.Clone());
        }

        public IntPtr Map(out int pitch)
        {
            int bytes = TextureFormats.RowBytes(Format, Width);
            int rows = TextureFormats.BlockFormat(Format) != 0 ? Math.Max(1, (Height + 3) / 4) : Height;
            if (_scratch == IntPtr.Zero)
            {
                _scratch = (IntPtr)NativeMemory.AllocZeroed((nuint)Math.Max(1, bytes * rows));
                _scratchPitch = bytes;
            }

            pitch = _scratchPitch;
            return _scratch;
        }

        public void Unmap() => Upload((byte*)_scratch, _scratchPitch, 0, 0, Width, Height);

        public void CopyDescTo(byte* desc) => _desc.CopyTo(new Span<byte>(desc, 44));

        protected override void Destroyed()
        {
            if (_scratch != IntPtr.Zero)
            {
                NativeMemory.Free((void*)_scratch);
                _scratch = IntPtr.Zero;
            }
        }

        private static VTable Build()
        {
            Slots slots = ChildSlots();
            slots[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, void>)&GetType;
            slots[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, void>)&SetEvictionPriority;
            slots[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&GetEvictionPriority;
            slots[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&GetDesc;
            return new VTable("ID3D11Texture2D", 11, slots);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetType(IntPtr self, int* type) => *type = 3; // D3D11_RESOURCE_DIMENSION_TEXTURE2D

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void SetEvictionPriority(IntPtr self, uint priority)
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint GetEvictionPriority(IntPtr self) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetDesc(IntPtr self, byte* desc) => From<HostTexture2D>(self).CopyDescTo(desc);
    }

    /// <summary>A view (shader resource, render target, depth stencil): the texture it views, and its desc.</summary>
    internal sealed unsafe class HostView : DeviceChild
    {
        private static readonly VTable SrvTable = Build("ID3D11ShaderResourceView");
        private static readonly VTable RtvTable = Build("ID3D11RenderTargetView");
        private static readonly VTable DsvTable = Build("ID3D11DepthStencilView");
        private readonly byte[] _desc;

        public enum Kind
        {
            ShaderResource,
            RenderTarget,
            DepthStencil,
        }

        public HostView(HostDevice device, Kind kind, HostTexture2D texture, byte* desc)
            : base(device, kind switch { Kind.ShaderResource => SrvTable, Kind.RenderTarget => RtvTable, _ => DsvTable },
                kind switch { Kind.ShaderResource => Iid.ID3D11ShaderResourceView, Kind.RenderTarget => Iid.ID3D11RenderTargetView, _ => Iid.ID3D11DepthStencilView },
                Iid.ID3D11View)
        {
            ViewKind = kind;
            Texture = texture;
            texture.AddRefPointer();
            _desc = desc != null ? new ReadOnlySpan<byte>(desc, 32).ToArray() : DefaultDesc(texture.Format);
        }

        public Kind ViewKind { get; }

        public HostTexture2D Texture { get; }

        protected override void Destroyed() => ReleaseObject(Texture);

        internal static void ReleaseObject(HostObject obj)
        {
            IntPtr pointer = obj.Pointer;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)pointer)[2])(pointer);
        }

        private static byte[] DefaultDesc(int format)
        {
            var desc = new byte[32];
            BitConverter.TryWriteBytes(desc.AsSpan(0), format);
            BitConverter.TryWriteBytes(desc.AsSpan(4), 4); // TEXTURE2D
            return desc;
        }

        private static VTable Build(string name)
        {
            Slots slots = ChildSlots();
            slots[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)&GetResource;
            slots[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&GetDesc;
            return new VTable(name, 9, slots);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetResource(IntPtr self, IntPtr* resource) => Out(resource, From<HostView>(self).Texture);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetDesc(IntPtr self, byte* desc)
        {
            HostView view = From<HostView>(self);
            int size = view.ViewKind == Kind.DepthStencil ? 24 : view.ViewKind == Kind.RenderTarget ? 20 : 24;
            view._desc.AsSpan(0, size).CopyTo(new Span<byte>(desc, size));
        }
    }

    /// <summary>A vertex or pixel shader: its DXBC, which the replay runs through its Aon9 chunk.</summary>
    internal sealed unsafe class HostShader : DeviceChild
    {
        private static readonly VTable VertexTable = new("ID3D11VertexShader", 7, ChildSlots());
        private static readonly VTable PixelTable = new("ID3D11PixelShader", 7, ChildSlots());

        public HostShader(HostDevice device, bool pixel, byte* bytecode, nuint length)
            : base(device, pixel ? PixelTable : VertexTable, pixel ? Iid.ID3D11PixelShader : Iid.ID3D11VertexShader)
        {
            IsPixel = pixel;
            Shader = new GpuShader(Key, new ReadOnlySpan<byte>(bytecode, (int)length).ToArray());
        }

        public bool IsPixel { get; }

        public GpuShader Shader { get; }
    }

    /// <summary>ID3D11InputLayout, resolved to the replay's element list (AppendAligned offsets filled in).</summary>
    internal sealed unsafe class HostInputLayout : DeviceChild
    {
        private static readonly VTable Table = new("ID3D11InputLayout", 7, ChildSlots());

        public HostInputLayout(HostDevice device, byte* elements, int count) : base(device, Table, Iid.ID3D11InputLayout)
        {
            var resolved = new List<FrameCapture.VertexElement>(count);
            var next = new Dictionary<int, int>();
            int stride = sizeof(IntPtr) + (6 * sizeof(int));
            for (int i = 0; i < count; i++)
            {
                byte* e = elements + (i * stride);
                string semantic = Marshal.PtrToStringAnsi(*(IntPtr*)e) ?? string.Empty;
                int* fields = (int*)(e + sizeof(IntPtr));
                int index = fields[0], format = fields[1], slot = fields[2], offset = fields[3];
                if (offset == -1)
                {
                    offset = next.GetValueOrDefault(slot);
                }

                resolved.Add(new FrameCapture.VertexElement(semantic.ToUpperInvariant(), (uint)index, (uint)format, offset, slot));
                next[slot] = offset + TextureFormats.ElementSize(format);
            }

            Elements = resolved;
        }

        public IReadOnlyList<FrameCapture.VertexElement> Elements { get; }
    }

    /// <summary>
    /// A state object: its raw D3D11 desc, which the replay reads byte for byte. As Direct3D does,
    /// creating a state equal to an existing one hands back that one (with a reference added), so
    /// a title that makes a blend state per call (Cocos2D) does not grow the device.
    /// </summary>
    internal sealed unsafe class HostState : DeviceChild
    {
        private static readonly VTable BlendTable = Build("ID3D11BlendState");
        private static readonly VTable DepthTable = Build("ID3D11DepthStencilState");
        private static readonly VTable RasterTable = Build("ID3D11RasterizerState");
        private static readonly VTable SamplerTable = Build("ID3D11SamplerState");

        public enum Kind
        {
            Blend,
            DepthStencil,
            Rasterizer,
            Sampler,
        }

        private HostState(HostDevice device, Kind kind, byte[] raw)
            : base(device, kind switch { Kind.Blend => BlendTable, Kind.DepthStencil => DepthTable, Kind.Rasterizer => RasterTable, _ => SamplerTable },
                kind switch
                {
                    Kind.Blend => [Iid.ID3D11BlendState, Iid.ID3D11BlendState1],
                    Kind.DepthStencil => [Iid.ID3D11DepthStencilState],
                    Kind.Rasterizer => [Iid.ID3D11RasterizerState, Iid.ID3D11RasterizerState1],
                    _ => [Iid.ID3D11SamplerState],
                })
        {
            StateKind = kind;
            Raw = raw;
        }

        public Kind StateKind { get; }

        /// <summary>The desc as the replay reads it: blend 40 bytes (render target 0), depth 52, raster 40, sampler 52.</summary>
        public byte[] Raw { get; }

        /// <summary>The full desc as created (a blend desc's eight render targets), for GetDesc.</summary>
        private byte[] _full = [];

        public static int SizeOf(Kind kind) => kind switch { Kind.Blend => 264, Kind.DepthStencil => 52, Kind.Rasterizer => 40, _ => 52 };

        public static HostState Create(HostDevice device, Kind kind, byte* desc)
        {
            byte[] full = new ReadOnlySpan<byte>(desc, SizeOf(kind)).ToArray();
            string key = kind + Convert.ToHexString(full);
            lock (device.States)
            {
                if (device.States.TryGetValue(key, out HostState? known) && known.TryAddRef())
                {
                    return known;
                }

                byte[] raw = kind == Kind.Blend ? full.AsSpan(0, 40).ToArray() : full;
                var state = new HostState(device, kind, raw) { _full = full };
                device.States[key] = state;
                return state;
            }
        }

        private bool _dead;

        private bool TryAddRef()
        {
            if (_dead)
            {
                return false;
            }

            AddRefPointer();
            return true;
        }

        protected override void Destroyed() => _dead = true;

        private static VTable Build(string name)
        {
            Slots slots = ChildSlots();
            slots[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&GetDesc;
            slots[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&GetDesc;
            return new VTable(name, 9, slots);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetDesc(IntPtr self, byte* desc)
        {
            HostState state = From<HostState>(self);
            state._full.CopyTo(new Span<byte>(desc, state._full.Length));
        }
    }

    /// <summary>ID3D11Query: answered at once (an event or occlusion query is "done", with no samples counted).</summary>
    internal sealed unsafe class HostQuery : DeviceChild
    {
        private static readonly VTable Table = Build();
        private readonly byte[] _desc;

        public HostQuery(HostDevice device, byte* desc) : base(device, Table, Iid.ID3D11Query, Iid.ID3D11Asynchronous)
            => _desc = new ReadOnlySpan<byte>(desc, 8).ToArray();

        public int QueryType => BitConverter.ToInt32(_desc, 0);

        private static VTable Build()
        {
            Slots slots = ChildSlots();
            slots[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&GetDataSize;
            slots[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&GetDesc;
            return new VTable("ID3D11Query", 9, slots);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint GetDataSize(IntPtr self) => From<HostQuery>(self).QueryType switch { 0 => 4u, 1 or 3 => 8u, 2 => 16u, _ => 8u };

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetDesc(IntPtr self, byte* desc) => From<HostQuery>(self)._desc.CopyTo(new Span<byte>(desc, 8));
    }
}
