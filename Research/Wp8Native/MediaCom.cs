using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// A host-side COM object behind one or more guest interface pointers: the shared machinery
    /// for the Media Foundation, WASAPI and XAPO objects (<see cref="MediaFoundation"/>,
    /// <see cref="PhoneAudio"/>, <see cref="X3DAudioLibrary"/>).
    /// </summary>
    /// <remarks>
    /// Unlike the Direct3D layer these objects carry real reference counts: Media Foundation
    /// code hands samples and buffers around by AddRef/Release and frees on the last Release,
    /// and an object that never died would grow without bound in a streaming loop.
    /// </remarks>
    internal abstract class MediaObject
    {
        public int Refs = 1;

        /// <summary>Interface name -> the guest pointer exposing it.</summary>
        public readonly Dictionary<string, long> Pointers = new(StringComparer.Ordinal);

        /// <summary>IID -> the guest pointer answering QueryInterface for it.</summary>
        public readonly Dictionary<Guid, long> ByIid = new();

        public abstract string Kind { get; }

        public bool Dead { get; set; }

        /// <summary>The pointer QueryInterface(IID_IUnknown) answers with: the first one exposed.</summary>
        public long Primary { get; set; }

        /// <summary>Host-side clean-up on the final Release (memory the object owns).</summary>
        public Action? Finalizer { get; set; }

        /// <summary>
        /// Runs once on the final Release. Returns COM pointers this object held references on,
        /// which are released after it (host objects directly, guest ones by calling Release).
        /// </summary>
        public virtual IEnumerable<long> Destroy() => [];
    }

    /// <summary>One guest call to make from a handler: a function and its word arguments.</summary>
    internal readonly record struct GuestCall(string Name, long Function, long[] Args);

    /// <summary>What a guest-call script (<see cref="MediaCom.Run"/>) shares with its caller.</summary>
    internal sealed class GuestOp
    {
        /// <summary>r0 of the call last yielded.</summary>
        public long Last;

        /// <summary>The script's HRESULT.</summary>
        public long Hr;

        /// <summary>What it produced: a length, a count of bytes.</summary>
        public long Value;
    }

    /// <summary>An interface shape: its name, the IIDs that resolve to it, and its methods.</summary>
    internal sealed record MediaInterface(
        string Name,
        Guid[] Iids,
        int Slots,
        Func<Dictionary<int, (string Name, Action Handler)>> Methods,
        int BlockSize = 8,
        bool HasUnknown = true);

    internal sealed class MediaCom
    {
        public const long SOk = 0;
        public const long SFalse = 1;
        public const long ENotImpl = unchecked((int)0x80004001);
        public const long ENoInterface = unchecked((int)0x80004002);
        public const long EPointer = unchecked((int)0x80004003);
        public const long EFail = unchecked((int)0x80004005);
        public const long EUnexpected = unchecked((int)0x8000FFFF);
        public const long EInvalidArg = unchecked((int)0x80070057);
        public const long EOutOfMemory = unchecked((int)0x8007000E);
        public const long EHandle = unchecked((int)0x80070006);
        public const long EAccessDenied = unchecked((int)0x80070005);
        public const long ENotSufficientBuffer = unchecked((int)0x8007007A);
        public const long ENotFound = unchecked((int)0x80070490);
        public const long ClassNotAvailable = unchecked((int)0x80040111);
        public const long NoAggregation = unchecked((int)0x80040110);

        public static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");

        private readonly ArmEmulator _e;
        private readonly CallFrame _f;
        private readonly Dictionary<long, MediaObject> _byPointer = new();
        private readonly Dictionary<string, long> _vtables = new(StringComparer.Ordinal);
        private readonly List<string> _unimplemented = new();

        public MediaCom(ArmEmulator emulator, CallFrame frame)
        {
            _e = emulator;
            _f = frame;
        }

        public ArmEmulator Emulator => _e;

        public CallFrame Frame => _f;

        /// <summary>Methods and functions reached that answer a documented failure instead of working.</summary>
        public IReadOnlyList<string> Unimplemented => _unimplemented;

        /// <summary>Live host objects, by kind - for the report and the tests.</summary>
        public int LiveObjects(string kind) => _byPointer.Values.Distinct().Count(o => o.Kind == kind && !o.Dead);

        public long Arg(int index) => _f.Arg(index);

        public void Return(long value) => _f.Return(value);

        public void Record(string what)
        {
            if (!_unimplemented.Contains(what, StringComparer.Ordinal))
            {
                _unimplemented.Add(what);
            }
        }

        // ------------------------------------------------------------------------------
        // Memory helpers
        // ------------------------------------------------------------------------------

        public long Alloc(long size)
        {
            long p = _e.AllocateHeap(Math.Max(size, 4));
            if (p != 0)
            {
                _e.WriteMemory(p, new byte[Math.Max(size, 4)]);
            }

            return p;
        }

        public void Free(long p)
        {
            if (p != 0)
            {
                _e.FreeHeap(p);
            }
        }

        public uint U32(long address) => _e.ReadUInt32(address);

        public ulong U64(long address) => BitConverter.ToUInt64(_e.ReadMemory(address, 8));

        public ushort U16(long address) => BitConverter.ToUInt16(_e.ReadMemory(address, 2));

        public void W32(long address, long value)
        {
            if (address != 0)
            {
                _e.WriteUInt32(address, unchecked((uint)value));
            }
        }

        public void W64(long address, ulong value)
        {
            if (address != 0)
            {
                _e.WriteMemory(address, BitConverter.GetBytes(value));
            }
        }

        public void WFloat(long address, float value) => W32(address, BitConverter.SingleToUInt32Bits(value));

        public float RFloat(long address) => BitConverter.UInt32BitsToSingle(_e.ReadUInt32(address));

        public Guid ReadGuid(long address) => address == 0 ? Guid.Empty : new Guid(_e.ReadMemory(address, 16));

        public void WriteGuid(long address, Guid value)
        {
            if (address != 0)
            {
                _e.WriteMemory(address, value.ToByteArray());
            }
        }

        public string ReadWide(long address, int max = 32 * 1024) => address == 0 ? string.Empty : _e.ReadUtf16String(address, max);

        /// <summary>A CoTaskMemAlloc'd, NUL-terminated copy of <paramref name="text"/>.</summary>
        public long AllocWide(string text)
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
            long p = Alloc(bytes.Length);
            _e.WriteMemory(p, bytes);
            return p;
        }

        /// <summary>A BSTR: length prefix, characters, NUL; the pointer is past the prefix.</summary>
        public long AllocBstr(string text)
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
            long p = Alloc(bytes.Length + 4);
            _e.WriteUInt32(p, (uint)(bytes.Length - 2));
            _e.WriteMemory(p + 4, bytes);
            return p + 4;
        }

        public void SetDouble(double value) => _f.ReturnDouble(value);

        public double DoubleArg(int index) => _f.DoubleArg(index);

        public float FloatArg(int index) => _f.FloatArg(index);

        // ------------------------------------------------------------------------------
        // Objects and vtables
        // ------------------------------------------------------------------------------

        public MediaObject? Lookup(long pointer) =>
            pointer != 0 && _byPointer.TryGetValue(pointer, out MediaObject? o) && !o.Dead ? o : null;

        public T? Self<T>() where T : MediaObject => Lookup(Arg(0)) as T;

        /// <summary>Gives <paramref name="obj"/> a pointer for <paramref name="shape"/>, once.</summary>
        public long Expose(MediaObject obj, MediaInterface shape)
        {
            if (obj.Pointers.TryGetValue(shape.Name, out long existing))
            {
                return existing;
            }

            long block = Alloc(Math.Max(shape.BlockSize, 8));
            _e.WriteUInt32(block, (uint)VtableFor(shape));
            obj.Pointers[shape.Name] = block;
            foreach (Guid iid in shape.Iids)
            {
                obj.ByIid.TryAdd(iid, block);
            }

            if (obj.Primary == 0)
            {
                obj.Primary = block;
            }

            _byPointer[block] = obj;
            return block;
        }

        private long VtableFor(MediaInterface shape)
        {
            if (_vtables.TryGetValue(shape.Name, out long existing))
            {
                return existing;
            }

            Dictionary<int, (string Name, Action Handler)> methods = shape.Methods();
            const int padding = 8;
            long vtable = _e.AllocateHeap((shape.Slots + padding) * 4);
            for (int slot = 0; slot < shape.Slots + padding; slot++)
            {
                int captured = slot;
                (string name, Action handler) = shape.HasUnknown && slot < 3
                    ? (slot switch { 0 => "QueryInterface", 1 => "AddRef", _ => "Release" },
                       slot switch { 0 => (Action)QueryInterface, 1 => AddRef, _ => Release })
                    : methods.TryGetValue(slot, out var hit)
                        ? hit
                        : ($"slot{captured}", () =>
                        {
                            Record($"{shape.Name}::slot{captured} (no handler) -> E_NOTIMPL");
                            Return(ENotImpl);
                        });

                long trap = _e.RegisterVtableMethod($"{shape.Name}::{name}", handler);
                _e.WriteUInt32(vtable + (slot * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _vtables[shape.Name] = vtable;
            return vtable;
        }

        /// <summary>Lazily built interfaces an object may expose on QueryInterface.</summary>
        public Func<MediaObject, Guid, long>? LateInterface { get; set; }

        private void QueryInterface()
        {
            MediaObject? self = Lookup(Arg(0));
            Guid iid = ReadGuid(Arg(1));
            long outPointer = Arg(2);
            if (outPointer == 0)
            {
                Return(EPointer);
                return;
            }

            long found = self is null ? 0 : QueryHost(self, iid);
            W32(outPointer, found);
            if (found == 0)
            {
                Return(ENoInterface);
                return;
            }

            self!.Refs++;
            Return(SOk);
        }

        /// <summary>The pointer <paramref name="obj"/> answers for <paramref name="iid"/>, or 0; no AddRef.</summary>
        public long QueryHost(MediaObject obj, Guid iid)
        {
            if (iid == IidUnknown)
            {
                return obj.Primary;
            }

            if (obj.ByIid.TryGetValue(iid, out long pointer))
            {
                return pointer;
            }

            return LateInterface?.Invoke(obj, iid) ?? 0;
        }

        private void AddRef()
        {
            MediaObject? self = Lookup(Arg(0));
            Return(self is null ? 1 : ++self.Refs);
        }

        private void Release()
        {
            MediaObject? self = Lookup(Arg(0));
            if (self is null)
            {
                Return(0);
                return;
            }

            if (--self.Refs > 0)
            {
                Return(self.Refs);
                return;
            }

            List<long> foreign = Kill(self);
            ReleaseGuest(foreign, () => Return(0));
        }

        /// <summary>Destroys a host object whose count reached zero; returns the guest pointers it held.</summary>
        private List<long> Kill(MediaObject self)
        {
            self.Dead = true;
            self.Finalizer?.Invoke();
            List<long> foreign = new();
            foreach (long held in self.Destroy())
            {
                if (held == 0)
                {
                    continue;
                }

                if (Lookup(held) is { } host)
                {
                    if (--host.Refs <= 0)
                    {
                        foreign.AddRange(Kill(host));
                    }
                }
                else
                {
                    foreign.Add(held);
                }
            }

            foreach (long pointer in self.Pointers.Values)
            {
                _byPointer.Remove(pointer);
                Free(pointer);
            }

            return foreign;
        }

        // ------------------------------------------------------------------------------
        // References on arbitrary COM pointers, host or guest
        // ------------------------------------------------------------------------------

        /// <summary>Adds a reference on <paramref name="pointer"/> (host objects at once; guest ones queued).</summary>
        public void AddRefAny(long pointer, List<GuestCall> guestCalls)
        {
            if (pointer == 0)
            {
                return;
            }

            if (Lookup(pointer) is { } host)
            {
                host.Refs++;
            }
            else if (GuestMethod(pointer, 1) is { } fn)
            {
                guestCalls.Add(new GuestCall("IUnknown::AddRef", fn, [pointer]));
            }
        }

        /// <summary>Releases a reference on <paramref name="pointer"/>.</summary>
        public void ReleaseAny(long pointer, List<GuestCall> guestCalls)
        {
            if (pointer == 0)
            {
                return;
            }

            if (Lookup(pointer) is { } host)
            {
                if (--host.Refs <= 0)
                {
                    foreach (long p in Kill(host))
                    {
                        ReleaseAny(p, guestCalls);
                    }
                }
            }
            else if (GuestMethod(pointer, 2) is { } fn)
            {
                guestCalls.Add(new GuestCall("IUnknown::Release", fn, [pointer]));
            }
        }

        private void ReleaseGuest(List<long> pointers, Action finish)
        {
            List<GuestCall> calls = new();
            foreach (long p in pointers)
            {
                ReleaseAny(p, calls);
            }

            Then(calls, _ => finish());
        }

        /// <summary>A guest object's method <paramref name="slot"/>, if it is code.</summary>
        public long? GuestMethod(long pointer, int slot)
        {
            if (pointer == 0)
            {
                return null;
            }

            long vtable = _e.ReadUInt32(pointer, 0);
            if (vtable == 0)
            {
                return null;
            }

            long fn = _e.ReadUInt32(vtable + (slot * 4), 0);
            return _e.IsExecutableCode(fn) || _e.TrapNameAt(fn & ~1L) is not null ? fn : null;
        }

        /// <summary>
        /// Makes <paramref name="calls"/> into guest code one after another, then runs
        /// <paramref name="finish"/> with their r0 results, which sets the handler's own result.
        /// </summary>
        /// <remarks>
        /// Only legal from a trap handler. With nothing to call, <paramref name="finish"/> runs at
        /// once and the handler returns normally; otherwise the handler's return is taken over and
        /// resumed at the original caller once the last call returns.
        /// </remarks>
        public void Then(IReadOnlyList<GuestCall> calls, Action<IReadOnlyList<long>> finish)
        {
            int index = 0;
            Sequence(results => index < calls.Count ? calls[index++] : null, finish);
        }

        /// <summary>
        /// As <see cref="Then"/>, with each call chosen once the previous one's result is known;
        /// <paramref name="next"/> returns null when there are no more.
        /// </summary>
        public void Sequence(Func<IReadOnlyList<long>, GuestCall?> next, Action<IReadOnlyList<long>> finish)
        {
            List<long> results = new();
            GuestCall? first = next(results);
            if (first is null)
            {
                finish(results);
                return;
            }

            long resume = _e.ReturnAddress;

            void Run(GuestCall call)
            {
                _e.CallEmulated(call.Name, call.Function, call.Args, () =>
                {
                    results.Add(_e.ReadRegister(Arm.UC_ARM_REG_R0) & 0xFFFFFFFFL);
                    GuestCall? following = next(results);
                    if (following is { } c)
                    {
                        Run(c);
                        return;
                    }

                    finish(results);
                    _e.ContinueAt(resume);
                }, recycle: true);
            }

            Run(first.Value);
        }

        /// <summary>
        /// Runs a script of guest calls - an iterator that yields each call and reads its r0 from
        /// <see cref="GuestOp.Last"/> when resumed - then <paramref name="finish"/>, which sets the
        /// handler's result. Same rules as <see cref="Then"/>.
        /// </summary>
        /// <remarks>
        /// The way to chain guest calls: a <paramref name="finish"/> (of this, Then or Sequence)
        /// must never start more of them, because the original caller is resumed straight after it.
        /// Scripts compose with <c>foreach (var c in inner) yield return c;</c> over one shared op.
        /// </remarks>
        public void Run(GuestOp op, IEnumerable<GuestCall> script, Action finish)
        {
            IEnumerator<GuestCall> steps = script.GetEnumerator();
            Sequence(
                results =>
                {
                    if (results.Count > 0)
                    {
                        op.Last = results[^1];
                    }

                    return steps.MoveNext() ? steps.Current : null;
                },
                _ => finish());
        }

        /// <summary>
        /// QueryInterface on any pointer into <paramref name="outPointer"/>; <paramref name="finish"/>
        /// gets the HRESULT.
        /// </summary>
        public void QueryAny(long pointer, Guid iid, long outPointer, Action<long> finish)
        {
            if (Lookup(pointer) is { } host)
            {
                long found = QueryHost(host, iid);
                W32(outPointer, found);
                if (found != 0)
                {
                    host.Refs++;
                }

                finish(found == 0 ? ENoInterface : SOk);
                return;
            }

            if (GuestMethod(pointer, 0) is not { } qi)
            {
                W32(outPointer, 0);
                finish(EPointer);
                return;
            }

            long iidCopy = Alloc(16);
            WriteGuid(iidCopy, iid);
            Then([new GuestCall("IUnknown::QueryInterface", qi, [pointer, iidCopy, outPointer])], results =>
            {
                Free(iidCopy);
                finish(results[0]);
            });
        }

        /// <summary>
        /// Builds a free-standing function the guest can be handed as a code pointer (a queued
        /// callback): it runs <paramref name="handler"/> as a trap.
        /// </summary>
        public long HostFunction(string name, Action handler) => ArmEmulator.ThumbEntry(_e.RegisterVtableMethod(name, handler));
    }
}
