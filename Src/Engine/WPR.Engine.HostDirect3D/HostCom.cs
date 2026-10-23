using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// A vtable in native memory: slot i is a C# <c>[UnmanagedCallersOnly]</c> function, or, when WPR
    /// does not implement it, a reporter that logs "Interface::slot N" once and answers E_NOTIMPL.
    /// Slots 0-2 are always IUnknown.
    /// </summary>
    internal sealed unsafe class VTable
    {
        private static readonly List<(string Name, string[] Names)> Tables = new();
        private static readonly HashSet<int> Reported = new();
        private static readonly IntPtr UnimplementedTarget = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Unimplemented;

        /// <param name="unknown">
        /// False for an interface with no IUnknown in front of it (XAudio2's voices): slot 0 is
        /// then the interface's own first method.
        /// </param>
        public VTable(string name, int count, IReadOnlyDictionary<int, IntPtr> slots, string[]? names = null, bool unknown = true)
        {
            Name = name;
            int table;
            lock (Tables)
            {
                table = Tables.Count;
                Tables.Add((name, names ?? []));
            }

            int first = unknown ? 3 : 0;
            var batch = new NativeCode.Batch();
            var pending = new Dictionary<int, int>();
            for (int i = first; i < count; i++)
            {
                if (!slots.ContainsKey(i))
                {
                    pending[i] = batch.Reporter(UnimplementedTarget, (table << 10) | i);
                }
            }

            IntPtr[] thunks = batch.Commit();
            var vtbl = (IntPtr*)NativeMemory.AllocZeroed((nuint)(count * sizeof(IntPtr)));
            if (unknown)
            {
                vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&HostObject.QueryInterface;
                vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HostObject.AddRef;
                vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HostObject.Release;
            }

            for (int i = first; i < count; i++)
            {
                vtbl[i] = slots.TryGetValue(i, out IntPtr fn) ? fn : thunks[pending[i]];
            }

            Pointer = (IntPtr)vtbl;
        }

        public string Name { get; }

        public IntPtr Pointer { get; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Unimplemented(IntPtr self)
        {
            int id = *NativeCode.LastSlot;
            bool first;
            lock (Reported)
            {
                first = Reported.Add(id);
            }

            if (first)
            {
                (string name, string[] names) = Tables[id >> 10];
                int slot = id & 0x3FF;
                string method = slot < names.Length && names[slot] is { Length: > 0 } n ? n : "slot " + slot;
                HostLog.Write($"unimplemented: {name}::{method} (slot {slot}); answered E_NOTIMPL");
            }

            return unchecked((int)0x80004001);
        }
    }

    internal static class HostLog
    {
        public static void Write(string line) => Trace.WriteLine("[wpr-d3d] " + line);

        private static readonly HashSet<string> Once = new();

        public static void WriteOnce(string key, string line)
        {
            lock (Once)
            {
                if (!Once.Add(key))
                {
                    return;
                }
            }

            Write(line);
        }
    }

    /// <summary>
    /// A host-native COM object: one or more interface faces, each a native block of
    /// <c>[vtable][GCHandle]</c>, so a vtable function finds the C# object from its <c>this</c>.
    /// One reference count for all faces, as COM requires. At zero the faces and the handle are
    /// freed; using one after that is the caller's bug, as on real Direct3D.
    /// </summary>
    internal abstract unsafe class HostObject
    {
        private readonly List<(Guid[] Iids, IntPtr Face)> _faces = new();
        private GCHandle _handle;
        private int _refs = 1;

        protected HostObject(VTable primary, params Guid[] iids)
        {
            _handle = GCHandle.Alloc(this);
            Pointer = AddFace(primary, iids);
        }

        /// <summary>The primary interface pointer, with no reference added.</summary>
        public IntPtr Pointer { get; }

        protected IntPtr AddFace(VTable vtable, params Guid[] iids)
        {
            var block = (IntPtr*)NativeMemory.Alloc((nuint)(2 * sizeof(IntPtr)));
            block[0] = vtable.Pointer;
            block[1] = GCHandle.ToIntPtr(_handle);
            _faces.Add((iids, (IntPtr)block));
            return (IntPtr)block;
        }

        public static T From<T>(IntPtr face) where T : class => (T)GCHandle.FromIntPtr(((IntPtr*)face)[1]).Target!;

        public static T? FromOrNull<T>(IntPtr face) where T : class => face == IntPtr.Zero ? null : GCHandle.FromIntPtr(((IntPtr*)face)[1]).Target as T;

        /// <summary>Adds a reference and returns the primary pointer: what an out-parameter receives.</summary>
        public IntPtr AddRefPointer()
        {
            Interlocked.Increment(ref _refs);
            return Pointer;
        }

        /// <summary>
        /// Puts <paramref name="value"/> in a binding slot, holding a reference to it and dropping
        /// the one on what was there: a Direct3D context keeps what is bound alive, and SharpDX
        /// releases its wrapper of a state right after binding it.
        /// </summary>
        public static void Hold<T>(ref T? slot, T? value) where T : HostObject
        {
            if (ReferenceEquals(slot, value))
            {
                return;
            }

            value?.AddRefPointer();
            T? old = slot;
            slot = value;
            old?.ReleaseReference();
        }

        public static void Out(IntPtr* destination, HostObject? value)
        {
            if (destination != null)
            {
                *destination = value?.AddRefPointer() ?? IntPtr.Zero;
            }
        }

        protected virtual void Destroyed()
        {
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
        {
            HostObject obj = From<HostObject>(self);
            Guid wanted = *iid;
            if (wanted == Iid.IUnknown)
            {
                *result = obj.AddRefPointer();
                return 0;
            }

            foreach ((Guid[] iids, IntPtr face) in obj._faces)
            {
                if (Array.IndexOf(iids, wanted) >= 0)
                {
                    Interlocked.Increment(ref obj._refs);
                    *result = face;
                    return 0;
                }
            }

            HostLog.WriteOnce(obj.GetType().Name + wanted, $"{obj.GetType().Name}: QueryInterface {{{wanted}}} refused");
            *result = IntPtr.Zero;
            return unchecked((int)0x80004002);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static uint AddRef(IntPtr self) => (uint)Interlocked.Increment(ref From<HostObject>(self)._refs);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static uint Release(IntPtr self) => From<HostObject>(self).ReleaseReference();

        /// <summary>Drops one reference, as Release does from native code.</summary>
        public uint ReleaseReference()
        {
            int left = Interlocked.Decrement(ref _refs);
            if (left == 0)
            {
                Destroyed();
                foreach ((_, IntPtr face) in _faces)
                {
                    NativeMemory.Free((void*)face);
                }

                _faces.Clear();
                _handle.Free();
            }

            return (uint)Math.Max(0, left);
        }
    }

    /// <summary>Interface ids the host answers.</summary>
    internal static class Iid
    {
        public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
        public static readonly Guid ID3D11DeviceChild = new("1841e5c8-16b0-489b-bcc8-44cfb0d5deae");
        public static readonly Guid ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        public static readonly Guid ID3D11Device1 = new("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
        public static readonly Guid ID3D11DeviceContext = new("c0bfa96c-e089-44fb-8eaf-26f8796190da");
        public static readonly Guid ID3D11DeviceContext1 = new("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1");
        public static readonly Guid ID3D11Resource = new("dc8e63f3-d12b-4952-b47b-5e45026a862d");
        public static readonly Guid ID3D11Buffer = new("48570b85-d1ee-4fcd-a250-eb350722b037");
        public static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public static readonly Guid ID3D11View = new("839d1216-bb2e-412b-b7f4-a9dbebe08ed1");
        public static readonly Guid ID3D11ShaderResourceView = new("b0e06fe0-8192-4e1a-b1ca-36d7414710b2");
        public static readonly Guid ID3D11RenderTargetView = new("dfdba067-0b8d-4865-875b-d7b4516cc164");
        public static readonly Guid ID3D11DepthStencilView = new("9fdac92a-1876-48c3-afad-25b94f84a9b6");
        public static readonly Guid ID3D11VertexShader = new("3b301d64-d678-4289-8897-22f8928b72f3");
        public static readonly Guid ID3D11PixelShader = new("ea82e40d-51dc-4f33-93d4-db7c9125ae8c");
        public static readonly Guid ID3D11InputLayout = new("e4819ddc-4cf0-4025-bd26-5de82a3e07b7");
        public static readonly Guid ID3D11BlendState = new("75b68faa-347d-4159-8f45-a0640f01cd9a");
        public static readonly Guid ID3D11BlendState1 = new("cc86fabe-da55-401d-85e7-e3c9de2877e9");
        public static readonly Guid ID3D11DepthStencilState = new("03823efb-8d8f-4e1c-9aa2-f64bb2cbfdf1");
        public static readonly Guid ID3D11RasterizerState = new("9bb4ab81-ab1a-4d8f-b506-fc04200b6ee7");
        public static readonly Guid ID3D11RasterizerState1 = new("1217d7a6-5039-418c-b042-9cbe256afd6e");
        public static readonly Guid ID3D11SamplerState = new("da6fea51-564c-4487-9810-f0d0f9b4e3a5");
        public static readonly Guid ID3D11Query = new("d6c00747-87b7-425e-b84d-44d108560afd");
        public static readonly Guid ID3D11Asynchronous = new("4b35d0cd-1e15-4258-9c98-1b1333f6dd3b");
        public static readonly Guid IDXGIObject = new("aec22fb8-76f3-4639-9be0-28eb43a67a2e");
        public static readonly Guid IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        public static readonly Guid IDXGIDevice1 = new("77db970f-6276-48ba-ba28-070143b4392c");
        public static readonly Guid IDXGIDevice2 = new("05008617-fbfd-4051-a790-144884b4f6a9");
        public static readonly Guid IDXGIAdapter = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
        public static readonly Guid IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
        public static readonly Guid IDXGIAdapter2 = new("0aa1ae0a-fa0e-4b84-8644-e05ff8e5acb5");
        public static readonly Guid IDXGIFactory = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
        public static readonly Guid IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        public static readonly Guid IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        public static readonly Guid IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
        public static readonly Guid IDXGISurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    }

    /// <summary>Helpers for building a slot table from function pointers.</summary>
    internal sealed class Slots : Dictionary<int, IntPtr>
    {
    }
}
