using System.Security.Cryptography;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// COM objects the host builds in guest memory for vccorlib and the WinRT/COM ABI API sets:
    /// boxed values, Platform exceptions and types, streams, error infos, the object context.
    /// </summary>
    /// <remarks>
    /// An object is a run of vptrs, one per interface, at <c>this + 4*i</c> - the layout MSVC gives a
    /// class with several interface bases, so a pointer to interface i is <c>base + 4*i</c> and every
    /// slot handler finds the object by subtracting its own interface's offset. QueryInterface answers
    /// IUnknown, IInspectable and IAgileObject with interface 0 (nothing here has an apartment, so
    /// everything is agile) and every other IID with the interface that declares it, AddRef'd;
    /// anything else is E_NOINTERFACE with the out-parameter nulled. Reference counts are kept on the
    /// host; an object is never freed (a class may release what it owns when its count reaches 0).
    /// </remarks>
    public sealed partial class HostStubs
    {
        internal static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
        internal static readonly Guid IidInspectable = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");
        internal static readonly Guid IidAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
        internal static readonly Guid IidPropertyValue = new("4bd682dd-7554-40e9-9a9b-82654ede7e62");
        internal static readonly Guid IidWeakReferenceSource = new("00000038-0000-0000-c000-000000000046");

        /// <summary>vccorlib's Platform::Details::IPrintable (ToString), as its own code QIs for it.</summary>
        internal static readonly Guid IidPrintable = new("de0cbaeb-8065-4a45-96b1-c9d443f9bab3");

        /// <summary>vccorlib's Platform::Details::IEquatable (Equals, GetHashCode).</summary>
        internal static readonly Guid IidEquatable = new("de0cbaec-e077-4e92-84be-c6d48dca3006");

        /// <summary>The generic IIDs of IReference`1 and IReferenceArray`1.</summary>
        internal static readonly Guid IidReferenceGeneric = new("61c17706-2d65-11e0-9ae8-d48564015472");
        internal static readonly Guid IidReferenceArrayGeneric = new("61c17707-2d65-11e0-9ae8-d48564015472");

        private const long SOk = 0;
        private const long SFalse = 1;
        private const long ENoInterface = unchecked((int)0x80004002);
        private const long EPointer = unchecked((int)0x80004003);
        private const long EInvalidArg = unchecked((int)0x80070057);
        private const long EOutOfMemory = unchecked((int)0x8007000E);
        private const long EBounds = unchecked((int)0x8000000B);
        private const long EUnexpected = unchecked((int)0x8000FFFF);
        private const long TypeMismatch = unchecked((int)0x80028CA0);   // TYPE_E_TYPEMISMATCH
        private const long Overflow = unchecked((int)0x8002000A);       // DISP_E_OVERFLOW

        /// <summary>
        /// The IID of a WinRT parameterised interface instance, by the algorithm the MIDL compiler
        /// and RoGetParameterizedTypeInstanceIID use: SHA-1 over the WinRT namespace GUID followed by
        /// the UTF-8 type signature, truncated to a version-5 GUID.
        /// </summary>
        internal static Guid ParameterizedIid(Guid generic, string argumentSignature)
        {
            byte[] ns = [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];
            byte[] signature = Encoding.UTF8.GetBytes($"pinterface({{{generic:D}}};{argumentSignature})");
            byte[] hash = SHA1.HashData([.. ns, .. signature]);
            hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(
                (hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3],
                (short)((hash[4] << 8) | hash[5]),
                (short)((hash[6] << 8) | hash[7]),
                hash[8..16]);
        }

        // -------------------------------------------------------------------------
        // Classes and instances
        // -------------------------------------------------------------------------

        /// <summary>One interface of a host class: its IIDs and the methods after IUnknown/IInspectable.</summary>
        internal sealed record HostInterface(
            string Name,
            Guid[] Iids,
            bool Inspectable,
            params (string Name, Action<HostObject> Handler)[] Methods);

        internal sealed class HostClass(string runtimeClassName, HostInterface[] interfaces)
        {
            public string RuntimeClassName { get; } = runtimeClassName;

            public HostInterface[] Interfaces { get; } = interfaces;

            public long[] Vtables { get; } = new long[interfaces.Length];

            /// <summary>Runs when the reference count reaches zero.</summary>
            public Action<HostObject>? OnFinalRelease { get; init; }
        }

        internal sealed class HostObject(long address, HostClass hostClass)
        {
            public long Address { get; } = address;

            public HostClass Class { get; } = hostClass;

            public int References { get; set; } = 1;

            public object? State { get; set; }

            /// <summary>The pointer to interface <paramref name="index"/> of this object.</summary>
            public long Interface(int index) => Address + (index * 4);
        }

        private readonly Dictionary<long, HostObject> _hostObjects = new();

        /// <summary>The host object whose interface 0 is at <paramref name="address"/>, if any.</summary>
        internal HostObject? HostObjectAt(long address) => _hostObjects.GetValueOrDefault(address);

        /// <summary>The host object any interface pointer of which is <paramref name="pointer"/>.</summary>
        internal HostObject? HostObjectFor(long pointer)
        {
            for (int i = 0; i < 8; i++)
            {
                if (_hostObjects.TryGetValue(pointer - (i * 4), out HostObject? found) && i < found.Class.Interfaces.Length)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Allocates a zeroed object of <paramref name="size"/> bytes (at least one vptr per
        /// interface) and attaches <paramref name="hostClass"/> to it.
        /// </summary>
        internal HostObject CreateHostObject(HostClass hostClass, int size = 0, object? state = null)
        {
            size = Math.Max(size, hostClass.Interfaces.Length * 4);
            long address = _emulator.AllocateHeap(size);
            _emulator.WriteMemory(address, new byte[size]);
            return AttachHostObject(address, hostClass, state);
        }

        /// <summary>Writes the class's vptrs into memory the image allocated, and tracks it.</summary>
        internal HostObject AttachHostObject(long address, HostClass hostClass, object? state = null)
        {
            BuildVtables(hostClass);
            for (int i = 0; i < hostClass.Vtables.Length; i++)
            {
                _emulator.WriteUInt32(address + (i * 4), (uint)hostClass.Vtables[i]);
            }

            var created = new HostObject(address, hostClass) { State = state };
            _hostObjects[address] = created;
            return created;
        }

        private void BuildVtables(HostClass hostClass)
        {
            if (hostClass.Vtables[0] != 0)
            {
                return;
            }

            for (int i = 0; i < hostClass.Interfaces.Length; i++)
            {
                HostInterface face = hostClass.Interfaces[i];
                int index = i;
                var slots = new List<(string Name, Action Handler)>
                {
                    ("QueryInterface", () => WithObject(index, "QueryInterface", HostQueryInterface)),
                    ("AddRef", () => WithObject(index, "AddRef", o => Return(++o.References))),
                    ("Release", () => WithObject(index, "Release", HostRelease)),
                };

                if (face.Inspectable)
                {
                    slots.Add(("GetIids", () => WithObject(index, "GetIids", HostGetIids)));
                    slots.Add(("GetRuntimeClassName", () => WithObject(index, "GetRuntimeClassName", o =>
                    {
                        WriteOut(Arg(1), NewHString(o.Class.RuntimeClassName));
                        Return(SOk);
                    })));
                    slots.Add(("GetTrustLevel", () => WithObject(index, "GetTrustLevel", _ =>
                    {
                        WriteOut(Arg(1), 0);   // BaseTrust
                        Return(SOk);
                    })));
                }

                foreach ((string name, Action<HostObject> handler) in face.Methods)
                {
                    slots.Add((name, () => WithObject(index, name, handler)));
                }

                long vtable = _emulator.AllocateHeap(slots.Count * 4);
                for (int s = 0; s < slots.Count; s++)
                {
                    long trap = _emulator.RegisterVtableMethod($"{hostClass.RuntimeClassName} ({face.Name})::{slots[s].Name}", slots[s].Handler);
                    _emulator.WriteUInt32(vtable + (s * 4), (uint)ArmEmulator.ThumbEntry(trap));
                }

                hostClass.Vtables[i] = vtable;
            }
        }

        /// <summary>Runs a slot handler against the object its interface pointer belongs to.</summary>
        private void WithObject(int interfaceIndex, string method, Action<HostObject> handler)
        {
            long self = Arg(0) - (interfaceIndex * 4);
            if (_hostObjects.TryGetValue(self, out HostObject? found))
            {
                handler(found);
                return;
            }

            // A host vtable reached through memory that is no longer one of its objects.
            string name = $"host object 0x{self:X8}::{method} (object not tracked)";
            DefaultedCalls[name] = DefaultedCalls.GetValueOrDefault(name) + 1;
            Return(EUnexpected);
        }

        private void HostQueryInterface(HostObject o)
        {
            long outPointer = Arg(2);
            if (outPointer == 0)
            {
                Return(EPointer);
                return;
            }

            Guid iid = Arg(1) == 0 ? Guid.Empty : _emulator.ReadGuid(Arg(1));
            long? answer = null;
            if (iid == IidUnknown || iid == IidAgileObject || (iid == IidInspectable && o.Class.Interfaces[0].Inspectable))
            {
                answer = o.Address;
            }
            else
            {
                for (int i = 0; i < o.Class.Interfaces.Length && answer is null; i++)
                {
                    if (o.Class.Interfaces[i].Iids.Contains(iid))
                    {
                        answer = o.Interface(i);
                    }
                }
            }

            if (answer is null)
            {
                _emulator.WriteUInt32(outPointer, 0);
                Return(ENoInterface);
                return;
            }

            o.References++;
            _emulator.WriteUInt32(outPointer, (uint)answer.Value);
            Return(SOk);
        }

        private void HostRelease(HostObject o)
        {
            int count = --o.References;
            if (count == 0)
            {
                o.Class.OnFinalRelease?.Invoke(o);
            }

            Return(Math.Max(count, 0));
        }

        /// <summary>HRESULT GetIids(ULONG* count, IID** iids): the class's IIDs in CoTaskMem memory.</summary>
        private void HostGetIids(HostObject o)
        {
            Guid[] iids = o.Class.Interfaces.SelectMany(face => face.Iids).Distinct().ToArray();
            long block = iids.Length == 0 ? 0 : _emulator.AllocateHeap(iids.Length * 16);
            for (int i = 0; i < iids.Length; i++)
            {
                _emulator.WriteMemory(block + (i * 16), iids[i].ToByteArray());
            }

            WriteOut(Arg(1), iids.Length);
            WriteOut(Arg(2), block);
            Return(SOk);
        }

        /// <summary>
        /// A plain IInspectable vtable for Platform::Object and its value-type bases, which a ref
        /// class's constructor installs before the derived class installs its own. It belongs to no
        /// tracked object: an object only carries it while its constructors run.
        /// </summary>
        private long BaseInspectableVtable(string runtimeClassName)
        {
            if (_baseVtables.TryGetValue(runtimeClassName, out long cached))
            {
                return cached;
            }

            (string Name, Action Handler)[] slots =
            [
                ("QueryInterface", () =>
                {
                    Guid iid = Arg(1) == 0 ? Guid.Empty : _emulator.ReadGuid(Arg(1));
                    bool known = iid == IidUnknown || iid == IidInspectable || iid == IidAgileObject;
                    WriteOut(Arg(2), known ? Arg(0) : 0);
                    Return(Arg(2) == 0 ? EPointer : known ? SOk : ENoInterface);
                }),
                ("AddRef", () => Return(1)),
                ("Release", () => Return(1)),
                ("GetIids", () =>
                {
                    WriteOut(Arg(1), 0);
                    WriteOut(Arg(2), 0);
                    Return(SOk);
                }),
                ("GetRuntimeClassName", () =>
                {
                    WriteOut(Arg(1), NewHString(runtimeClassName));
                    Return(SOk);
                }),
                ("GetTrustLevel", () =>
                {
                    WriteOut(Arg(1), 0);
                    Return(SOk);
                }),
            ];

            long vtable = _emulator.AllocateHeap(slots.Length * 4);
            for (int i = 0; i < slots.Length; i++)
            {
                long trap = _emulator.RegisterVtableMethod($"{runtimeClassName} (base vtable)::{slots[i].Name}", slots[i].Handler);
                _emulator.WriteUInt32(vtable + (i * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _baseVtables[runtimeClassName] = vtable;
            return vtable;
        }

        private readonly Dictionary<string, long> _baseVtables = new(StringComparer.Ordinal);

        // -------------------------------------------------------------------------
        // Calling COM methods from inside an import
        // -------------------------------------------------------------------------

        /// <summary>
        /// Calls method <paramref name="slot"/> of the interface at <paramref name="self"/> - guest code
        /// or a host trap alike - and runs <paramref name="then"/> with r0 holding its result. Only from
        /// inside a handler; the chain must end in <see cref="FinishImport"/>.
        /// </summary>
        private void CallMethod(string name, long self, int slot, long[] arguments, Action then)
        {
            long function = _emulator.ReadUInt32(_emulator.ReadUInt32(self) + (slot * 4));
            _emulator.CallEmulated(name, function, [self, .. arguments], then, recycle: true);
        }

        /// <summary>Ends an import whose handler called back into the guest: r0, then back to its caller.</summary>
        private void FinishImport(long callerReturn, long result)
        {
            Return(result);
            _emulator.ContinueAt(callerReturn);
        }

        /// <summary>A zeroed scratch block for out-parameters of a chained call.</summary>
        private long Scratch(int size)
        {
            long block = _emulator.AllocateHeap(size);
            _emulator.WriteMemory(block, new byte[size]);
            return block;
        }

        private bool IsFailure(long hresult) => unchecked((int)(uint)hresult) < 0;

        private void WriteGuid(long address, Guid value) => _emulator.WriteMemory(address, value.ToByteArray());

        /// <summary>A BSTR (length-prefixed, NUL-terminated) in guest memory; null for a null text.</summary>
        private long AllocateBstr(string? text)
        {
            if (text is null)
            {
                return 0;
            }

            byte[] chars = Encoding.Unicode.GetBytes(text);
            long block = _emulator.AllocateHeap(4 + chars.Length + 2);
            _emulator.WriteUInt32(block, (uint)chars.Length);
            _emulator.WriteMemory(block + 4, [.. chars, 0, 0]);
            return block + 4;
        }

        private void ReturnFloatResult(float value)
        {
            _frame.ReturnFloat(value);
            Return(BitConverter.SingleToUInt32Bits(value));
        }

        private float FloatAt(long address) => BitConverter.ToSingle(_emulator.ReadMemory(address, 4));

        /// <summary>A new HSTRING for <paramref name="text"/>; the empty string is the null HSTRING, as in WinRT.</summary>
        private long NewHString(string? text) => string.IsNullOrEmpty(text) ? 0 : _strings.Create(text);
    }
}
