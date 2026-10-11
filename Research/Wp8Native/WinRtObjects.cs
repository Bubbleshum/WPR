using System.Security.Cryptography;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The system runtime classes implemented to their WinRT contract (storage, streams, the package,
    /// collections, boxing, Uri, launcher, timers, ...): <see cref="SystemClasses"/>.
    /// </summary>
    /// <remarks>
    /// Everything here is registered from one place, <see cref="RegisterSystemClasses"/>, called last
    /// in the constructor, so a class implemented here replaces any earlier discovery stand-in.
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private SystemClasses? _systemClasses;

        /// <summary>The system classes, built on first use and registered from the constructor.</summary>
        private SystemClasses Classes => _systemClasses ??= new SystemClasses(this);

        private void RegisterSystemClasses()
        {
            try
            {
                Classes.Register();
            }
            catch (Exception ex)
            {
                // A defect here must not cost a game its runtime: whatever registered stays, the rest
                // are stand-ins as before, and the slot-drift list (which tests require empty) says why.
                _slotDrift.Add($"system classes failed to register: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The activation factory for <paramref name="className"/> as the interface
        /// <paramref name="iid"/>: a class with several statics/factory interfaces (Uri's
        /// <c>IUriRuntimeClassFactory</c> and <c>IUriEscapeStatics</c>, DataWriter's
        /// <c>IActivationFactory</c> and <c>IDataWriterFactory</c>) has a different vtable for each,
        /// all with their first member at slot 6. Falls back to the class's default factory pointer.
        /// </summary>
        public long? GetActivationFactory(string className, Guid iid)
            => Classes.FactoryInterface(className, iid) ?? GetActivationFactory(className);

        /// <summary>Opens a URI outside the game (the host's browser or store). Null: there is none, and <c>LaunchUriAsync</c> answers false.</summary>
        public Func<string, bool>? UriLauncher { get; set; }

        /// <summary>Starts the device's vibration motor for a duration. Null: no motor, and VibrationDevice.GetDefault answers nullptr.</summary>
        public Action<TimeSpan>? VibrateHost { get; set; }

        /// <summary>Stops the vibration motor.</summary>
        public Action? CancelVibrationHost { get; set; }

        /// <summary>
        /// What the system classes were asked to do that they could only half do, or that a game did
        /// wrong (an interface it asked for that the object does not implement), in order.
        /// </summary>
        public IReadOnlyList<string> SystemClassLog => Classes.Log;

        /// <summary>
        /// The pinterface IID of a parameterized interface, by WinRT's own algorithm: a UUIDv5 of
        /// the type signature (<c>pinterface({9fc2b0bb-...};string)</c>) in WinRT's namespace.
        /// </summary>
        public static Guid ParameterizedIid(string signature)
        {
            byte[] ns = [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];
            byte[] text = Encoding.UTF8.GetBytes(signature);
            byte[] hash = SHA1.HashData([.. ns, .. text]);
            hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

            // The hash is big-endian; a Guid's first three fields are stored little-endian.
            return new Guid(
                (int)((uint)hash[0] << 24 | (uint)hash[1] << 16 | (uint)hash[2] << 8 | hash[3]),
                (short)(hash[4] << 8 | hash[5]),
                (short)(hash[6] << 8 | hash[7]),
                hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
        }

        /// <summary>
        /// Real WinRT objects: several interfaces each, one vtable per (class, interface) shared by
        /// every instance, and a host-side object found from the interface pointer in r0.
        /// </summary>
        /// <remarks>
        /// <para>The discovery objects in <c>WinRtRuntime.cs</c> answer QueryInterface with themselves
        /// for every IID, which is right for a single-interface static and wrong for nearly every
        /// instance class: a StorageFile is an IStorageFile, an IStorageItem and an
        /// IRandomAccessStreamReference, and each has its own vtable with its own first member at slot
        /// 6. Here each interface is its own pointer, QueryInterface answers by IID, and an IID the
        /// object does not implement is <c>E_NOINTERFACE</c> - which is what lets a game's
        /// <c>dynamic_cast&lt;StorageFolder^&gt;</c> on a file say no.</para>
        /// <para>Vtables are built once per class, not per object: a trap slot is a finite resource
        /// (16,384 to a page) and a game listing a folder makes hundreds of files.</para>
        /// <para>A member nobody implemented is answered from its metadata signature
        /// (<see cref="AnswerBySignature"/>) and recorded in <see cref="DefaultedSlots"/>, exactly as on
        /// a stand-in: the game keeps running.</para>
        /// </remarks>
        private sealed partial class SystemClasses
        {
            // HRESULTs, by their documented meaning.
            public const int S_OK = 0;
            public const int E_NOTIMPL = unchecked((int)0x80004001);
            public const int E_NOINTERFACE = unchecked((int)0x80004002);
            public const int E_POINTER = unchecked((int)0x80004003);
            public const int E_FAIL = unchecked((int)0x80004005);
            public const int E_INVALIDARG = unchecked((int)0x80070057);
            public const int E_ACCESSDENIED = unchecked((int)0x80070005);
            public const int E_BOUNDS = unchecked((int)0x8000000B);
            public const int E_ILLEGAL_METHOD_CALL = unchecked((int)0x8000000E);
            public const int E_ILLEGAL_DELEGATE_ASSIGNMENT = unchecked((int)0x80000018);
            public const int RO_E_CLOSED = unchecked((int)0x80000013);
            public const int FileNotFound = unchecked((int)0x80070002);     // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
            public const int PathNotFound = unchecked((int)0x80070003);     // HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)
            public const int AlreadyExists = unchecked((int)0x800700B7);    // HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)
            public const int DirNotEmpty = unchecked((int)0x80070091);      // HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY)
            public const int TypeMismatch = unchecked((int)0x80028CA0);     // TYPE_E_TYPEMISMATCH
            public const int Overflow = unchecked((int)0x8002000A);         // DISP_E_OVERFLOW
            public const int DataTypeMismatch = unchecked((int)0x8007065E); // HRESULT_FROM_WIN32(ERROR_DATATYPE_MISMATCH)

            private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
            private static readonly Guid IidInspectable = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");
            private static readonly Guid IidAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
            public static readonly Guid IidActivationFactory = new("00000035-0000-0000-c000-000000000046");

            private readonly WinRtRuntime _rt;
            private readonly ArmEmulator _e;

            public SystemClasses(WinRtRuntime runtime)
            {
                _rt = runtime;
                _e = runtime._emulator;
            }

            public List<string> Log { get; } = new();

            private readonly Dictionary<long, ComObject> _byPointer = new();
            private readonly Dictionary<string, ComClass> _classes = new(StringComparer.Ordinal);
            private readonly Dictionary<string, ComObject> _factoryObjects = new(StringComparer.Ordinal);

            /// <summary>Called once, from the runtime's constructor.</summary>
            public void Register()
            {
                RegisterFoundation();
                RegisterStorage();
                RegisterStreams();
                RegisterAppModel();
                RegisterSystem();
            }

            public long? FactoryInterface(string className, Guid iid)
                => _factoryObjects.TryGetValue(className, out ComObject? factory)
                    ? factory.Pointers.GetValueOrDefault(iid, factory.Primary)
                    : null;

            // -----------------------------------------------------------------------------------
            // Interface and class definitions
            // -----------------------------------------------------------------------------------

            /// <summary>One interface's shape: its name, IID, and methods in vtable order.</summary>
            public sealed record InterfaceDef(string Name, Guid Iid, IReadOnlyList<WinRtSystemInterfaces.Method> Methods, bool UnknownBased = false)
            {
                public int BaseSlots => UnknownBased ? 3 : WinRtSystemInterfaces.InspectableSlots;
            }

            /// <summary>The metadata's interface, optionally with the IID of one instantiation of a generic.</summary>
            private static InterfaceDef Meta(string fullName, Guid? iid = null)
            {
                WinRtSystemInterfaces.Interface found = WinRtSystemInterfaces.Find(fullName)
                    ?? throw new KeyNotFoundException($"no WinRT metadata for {fullName}");
                return new InterfaceDef(found.FullName, iid ?? found.Iid, found.Methods);
            }

            /// <summary>An interface the generated metadata does not cover (Windows.Phone.*, IUnknown-based natives), with signatures in the same "Name(A, B) : R" form.</summary>
            private static InterfaceDef Manual(string fullName, Guid iid, string[] methods, bool unknownBased = false)
            {
                int first = unknownBased ? 3 : WinRtSystemInterfaces.InspectableSlots;
                return new InterfaceDef(
                    fullName,
                    iid,
                    methods.Select((m, i) => new WinRtSystemInterfaces.Method(m[..m.IndexOf('(')], m, first + i)).ToArray(),
                    unknownBased);
            }

            public static readonly InterfaceDef ActivationFactory =
                Manual("IActivationFactory", IidActivationFactory, ["ActivateInstance() : Object"]);

            public static readonly InterfaceDef Closable = Meta("Windows.Foundation.IClosable");

            /// <summary>A member implementation. The call's arguments and result pointer are in <see cref="RtCall"/>.</summary>
            public delegate void Member(RtCall c);

            /// <summary>An interface of a class, and how its members are implemented: by name, the n-th overload as <c>Name/n</c>.</summary>
            public sealed record Iface(InterfaceDef Def, IReadOnlyDictionary<string, Member> Members);

            private static Iface I(InterfaceDef def, params (string Name, Member Handler)[] members)
                => new(def, members.ToDictionary(m => m.Name, m => m.Handler, StringComparer.Ordinal));

            /// <summary>A runtime class: its interfaces (the first is the default) with one shared vtable each.</summary>
            public sealed class ComClass
            {
                public required string Name { get; init; }

                public required IReadOnlyList<(InterfaceDef Def, long Vtable)> Interfaces { get; init; }

                /// <summary>Answer an IID nobody recognises with the default interface instead of E_NOINTERFACE: for classes whose IIDs are not in the metadata, so may be wrong here.</summary>
                public bool Lenient { get; init; }
            }

            /// <summary>One live object: one pointer per interface, all leading back here.</summary>
            public sealed class ComObject
            {
                public required ComClass Class { get; init; }

                public object? State { get; set; }

                public Dictionary<Guid, long> Pointers { get; } = new();

                public long Primary { get; set; }

                public int Refs { get; set; } = 1;

                public long WeakSource { get; set; }

                public T As<T>() where T : class => (T)State!;
            }

            /// <summary>
            /// Defines (or returns, by name) a class. Each interface's vtable is built here, once:
            /// IInspectable's six (or IUnknown's three), then every metadata member, implemented or
            /// answered by signature.
            /// </summary>
            private ComClass Class(string name, Iface[] interfaces, bool lenient = false)
            {
                if (_classes.TryGetValue(name, out ComClass? existing))
                {
                    return existing;
                }

                var built = new List<(InterfaceDef, long)>();
                foreach (Iface iface in interfaces)
                {
                    built.Add((iface.Def, BuildVtable(name, iface)));
                }

                ComClass cls = new() { Name = name, Interfaces = built, Lenient = lenient };
                _classes[name] = cls;
                return cls;
            }

            /// <summary>Unimplemented trailing slots beyond the interface, so a game calling one past the end gets a named, recorded answer rather than the next heap block.</summary>
            private const int VtablePadding = 4;

            private long BuildVtable(string className, Iface iface)
            {
                InterfaceDef def = iface.Def;
                int count = def.BaseSlots + def.Methods.Count + VtablePadding;
                long vtable = _e.AllocateHeap(count * 4);
                string label = $"{className}:{ShortName(def.Name)}";

                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                var used = new HashSet<string>(StringComparer.Ordinal);
                for (int slot = 0; slot < count; slot++)
                {
                    Action handler;
                    string name;
                    if (slot < def.BaseSlots)
                    {
                        (name, handler) = BaseMember(slot, def.UnknownBased);
                    }
                    else if (slot - def.BaseSlots < def.Methods.Count)
                    {
                        WinRtSystemInterfaces.Method method = def.Methods[slot - def.BaseSlots];
                        int nth = seen[method.Name] = seen.GetValueOrDefault(method.Name) + 1;
                        string key = nth == 1 ? method.Name : $"{method.Name}/{nth}";
                        name = key;
                        if (iface.Members.TryGetValue(key, out Member? member))
                        {
                            used.Add(key);
                            handler = Implemented(member, method);
                        }
                        else
                        {
                            handler = Defaulted(label, def, method, slot);
                        }
                    }
                    else
                    {
                        name = $"slot{slot}";
                        int captured = slot;
                        handler = () =>
                        {
                            _rt._unimplementedCalls.Add($"{label}::slot{captured} (past the end of the interface)");
                            _rt.Return(E_NOTIMPL);
                        };
                    }

                    long trap = _e.RegisterVtableMethod($"{label}::{name}", handler);
                    _e.WriteUInt32(vtable + (slot * 4), (uint)ArmEmulator.ThumbEntry(trap));
                }

                // A member implemented under a name the interface does not have is a typo that would
                // otherwise silently leave the real member defaulted. Reported as slot drift, which
                // the contract tests require to be empty.
                foreach (string key in iface.Members.Keys.Where(k => !used.Contains(k)))
                {
                    _rt._slotDrift.Add($"{label} implements {key}, which the interface does not have");
                }

                return vtable;
            }

            private static string ShortName(string fullName)
            {
                int angle = fullName.IndexOf('`');
                string head = angle > 0 ? fullName[..angle] : fullName;
                return head[(head.LastIndexOf('.') + 1)..];
            }

            private Action Implemented(Member member, WinRtSystemInterfaces.Method method)
            {
                WinRtSystemInterfaces.AbiLayout layout = WinRtSystemInterfaces.Layout(method);
                return () =>
                {
                    long self = _rt.Arg(0);
                    if (!_byPointer.TryGetValue(self, out ComObject? obj))
                    {
                        _rt.Return(E_POINTER);
                        return;
                    }

                    member(new RtCall(this, obj, layout));
                };
            }

            /// <summary>An unimplemented member: answered from its signature, recorded, and the game carries on.</summary>
            private Action Defaulted(string label, InterfaceDef def, WinRtSystemInterfaces.Method method, int slot)
            {
                string described = $"{label}::{method.Name}";
                return () =>
                {
                    _rt._unimplementedCalls.Add($"{described}  this=0x{_rt.Arg(0):X8} r1=0x{_rt.Arg(1):X8} r2=0x{_rt.Arg(2):X8}");
                    if (_rt.AnswerBySignature(method, def.Name, slot, described, failing: false))
                    {
                        return;
                    }

                    _rt._defaultedSlots[described] = _rt._defaultedSlots.GetValueOrDefault(described) + 1;
                    _rt.Return(E_NOTIMPL);
                };
            }

            private (string, Action) BaseMember(int slot, bool unknownBased) => slot switch
            {
                0 => ("QueryInterface", () => Query(_rt.Arg(0), _rt.Arg(1), _rt.Arg(2))),
                1 => ("AddRef", () => _rt.Return(Count(_rt.Arg(0), +1))),
                2 => ("Release", () => _rt.Return(Count(_rt.Arg(0), -1))),
                3 when !unknownBased => ("GetIids", () => Iids(_rt.Arg(0), _rt.Arg(1), _rt.Arg(2))),
                4 when !unknownBased => ("GetRuntimeClassName", () =>
                {
                    if (_byPointer.TryGetValue(_rt.Arg(0), out ComObject? o) && _rt.IsWritable(_rt.Arg(1), 4))
                    {
                        _e.WriteUInt32(_rt.Arg(1), (uint)_rt._strings.Create(o.Class.Name));
                    }

                    _rt.Return(S_OK);
                }),
                5 when !unknownBased => ("GetTrustLevel", () =>
                {
                    if (_rt.IsWritable(_rt.Arg(1), 4))
                    {
                        _e.WriteUInt32(_rt.Arg(1), 0); // BaseTrust
                    }

                    _rt.Return(S_OK);
                }),
                _ => throw new InvalidOperationException(),
            };

            private uint Count(long pointer, int delta)
            {
                if (!_byPointer.TryGetValue(pointer, out ComObject? obj))
                {
                    return 1;
                }

                // Nothing here is ever freed (the emulated heap does not know these blocks are
                // objects), so the count is only reported, never acted on.
                obj.Refs = Math.Max(0, obj.Refs + delta);
                return (uint)Math.Max(1, obj.Refs);
            }

            private void Query(long self, long iidPointer, long result)
            {
                if (result == 0 || iidPointer == 0)
                {
                    _rt.Return(E_POINTER);
                    return;
                }

                if (!_byPointer.TryGetValue(self, out ComObject? obj))
                {
                    _e.WriteUInt32(result, 0);
                    _rt.Return(E_NOINTERFACE);
                    return;
                }

                Guid iid = _e.ReadGuid(iidPointer);
                long answer = 0;
                if (iid == IidUnknown || iid == IidInspectable || iid == IidAgileObject)
                {
                    answer = obj.Primary;
                }
                else if (obj.Pointers.TryGetValue(iid, out long pointer))
                {
                    answer = pointer;
                }
                else if (iid == IidWeakReferenceSource)
                {
                    answer = obj.WeakSource != 0 ? obj.WeakSource : obj.WeakSource = WeakSourceFor(obj.Primary);
                }
                else if (obj.Class.Lenient)
                {
                    answer = obj.Primary;
                }

                if (answer == 0)
                {
                    string what = WinRtSystemInterfaces.Find(iid)?.FullName ?? iid.ToString();
                    Note($"{obj.Class.Name}: QueryInterface({what}) -> E_NOINTERFACE");
                    _e.WriteUInt32(result, 0);
                    _rt.Return(E_NOINTERFACE);
                    return;
                }

                obj.Refs++;
                _e.WriteUInt32(result, (uint)answer);
                _rt.Return(S_OK);
            }

            private long WeakSourceFor(long primary) => _rt.CreateUnknownObject(
                "IWeakReferenceSource",
                slotCount: 4,
                known: new Dictionary<int, (string, Action)>
                {
                    [3] = ("GetWeakReference", () =>
                    {
                        if (_rt.Arg(1) != 0)
                        {
                            _e.WriteUInt32(_rt.Arg(1), (uint)_rt.WeakReferenceTo(primary));
                        }

                        _rt.Return(S_OK);
                    }),
                });

            private void Iids(long self, long countOut, long arrayOut)
            {
                Guid[] iids = _byPointer.TryGetValue(self, out ComObject? obj)
                    ? obj.Pointers.Keys.Where(g => g != Guid.Empty).ToArray()
                    : [];
                long array = iids.Length == 0 ? 0 : _e.AllocateHeap(iids.Length * 16);
                for (int i = 0; i < iids.Length; i++)
                {
                    _e.WriteMemory(array + (i * 16), iids[i].ToByteArray());
                }

                if (_rt.IsWritable(countOut, 4))
                {
                    _e.WriteUInt32(countOut, (uint)iids.Length);
                }

                if (_rt.IsWritable(arrayOut, 4))
                {
                    _e.WriteUInt32(arrayOut, (uint)array);
                }

                _rt.Return(S_OK);
            }

            /// <summary>A new object of <paramref name="cls"/>: one 8-byte block per interface, [vtable][0].</summary>
            public ComObject New(ComClass cls, object? state)
            {
                ComObject obj = new() { Class = cls, State = state };
                foreach ((InterfaceDef def, long vtable) in cls.Interfaces)
                {
                    long pointer = _e.AllocateHeap(8);
                    _e.WriteUInt32(pointer, (uint)vtable);
                    _e.WriteUInt32(pointer + 4, 0);
                    _byPointer[pointer] = obj;
                    if (obj.Primary == 0)
                    {
                        obj.Primary = pointer;
                    }

                    obj.Pointers.TryAdd(def.Iid, pointer);
                }

                return obj;
            }

            /// <summary>The object an interface pointer belongs to, when it is one of these.</summary>
            public ComObject? Lookup(long pointer) => pointer != 0 && _byPointer.TryGetValue(pointer, out ComObject? obj) ? obj : null;

            /// <summary>The state of the object behind an interface pointer, when it is one of these and of that kind.</summary>
            public T? StateOf<T>(long pointer) where T : class => Lookup(pointer)?.State as T;

            /// <summary>The interface pointer of <paramref name="obj"/> for an interface by full name.</summary>
            public static long PointerFor(ComObject obj, string interfaceName)
            {
                foreach ((InterfaceDef def, long _) in obj.Class.Interfaces)
                {
                    if (def.Name == interfaceName && obj.Pointers.TryGetValue(def.Iid, out long pointer))
                    {
                        return pointer;
                    }
                }

                return obj.Primary;
            }

            /// <summary>Registers a factory object under a class name, for activation and for IID-routed activation.</summary>
            private ComObject Factory(string className, params Iface[] interfaces)
            {
                ComObject factory = New(Class(className + " (factory)", interfaces), null);
                _factoryObjects[className] = factory;
                _rt._factories[className] = factory.Primary;
                return factory;
            }

            public void Note(string line)
            {
                if (Log.Count < 2000)
                {
                    Log.Add(line);
                }
            }

            /// <summary>
            /// Invokes a list of delegates in turn (<c>Invoke</c> at slot 3, as every WinRT delegate)
            /// and then finishes the stub with <paramref name="result"/>. Returns false (and does
            /// nothing) when there is no delegate to call, so the caller returns normally.
            /// </summary>
            public bool InvokeThenReturn(IReadOnlyList<long> handlers, Func<long, long[]> arguments, int result, string why, Action? finished = null)
            {
                var live = handlers.Where(h => h != 0 && _rt.LooksLikeDelegate(h)).ToList();
                if (live.Count == 0)
                {
                    return false;
                }

                long callerReturn = _e.ReturnAddress;
                int next = 0;

                void InvokeNext()
                {
                    if (next >= live.Count)
                    {
                        _rt.Return(result);
                        _e.ContinueAt(callerReturn);
                        finished?.Invoke();
                        return;
                    }

                    long handler = live[next++];
                    long invoke = _e.ReadUInt32(_e.ReadUInt32(handler) + (SlotHandlerInvoke * 4));
                    _e.CallEmulated(why, invoke, arguments(handler), onReturn: InvokeNext, recycle: true);
                }

                InvokeNext();
                return true;
            }
        }

        /// <summary>
        /// One call to an implemented member: <c>this</c>'s object, the arguments by the ABI layout of
        /// the member's signature, and helpers for the result.
        /// </summary>
        private sealed class RtCall
        {
            private readonly SystemClasses _sys;
            private readonly WinRtSystemInterfaces.AbiLayout _layout;

            public RtCall(SystemClasses sys, SystemClasses.ComObject self, WinRtSystemInterfaces.AbiLayout layout)
            {
                _sys = sys;
                Self = self;
                _layout = layout;
            }

            public SystemClasses.ComObject Self { get; }

            public T State<T>() where T : class => Self.As<T>();

            private WinRtRuntime Rt => _sys.Runtime;

            /// <summary>The word of parameter <paramref name="index"/> (a 32-bit value or pointer).</summary>
            public long P(int index) => Rt.Arg(_layout.Parameters[index].Word);

            /// <summary>A 64-bit parameter (Int64, UInt64, DateTime, TimeSpan), from its even word pair.</summary>
            public long P64(int index)
            {
                int word = _layout.Parameters[index].Word;
                return (Rt.Arg(word) & 0xFFFFFFFFL) | (Rt.Arg(word + 1) << 32);
            }

            /// <summary>A parameter of <paramref name="words"/> words passed by value (a Guid is four).</summary>
            public byte[] PBytes(int index, int words)
            {
                int word = _layout.Parameters[index].Word;
                byte[] bytes = new byte[words * 4];
                for (int i = 0; i < words; i++)
                {
                    BitConverter.GetBytes((uint)Rt.Arg(word + i)).CopyTo(bytes, i * 4);
                }

                return bytes;
            }

            /// <summary>An HSTRING parameter's text; the null HSTRING is the empty string.</summary>
            public string Str(int index) => Rt._strings.ReadText(P(index));

            /// <summary>An array parameter: its (length, pointer) pair.</summary>
            public (uint Length, long Data) Array(int index)
            {
                int word = _layout.Parameters[index].Word;
                return ((uint)Rt.Arg(word), Rt.Arg(word + 1));
            }

            /// <summary>Where the result goes: the trailing out-pointer.</summary>
            public long ResultPointer => _layout.Result is { } r ? Rt.Arg(r.Word) : 0;

            public void Ok() => Rt.Return(SystemClasses.S_OK);

            public void Fail(int hresult) => Rt.Return(hresult);

            public void RetObj(long pointer)
            {
                if (Rt.IsWritable(ResultPointer, 4))
                {
                    Rt._emulator.WriteUInt32(ResultPointer, (uint)pointer);
                    if (_sys.Lookup(pointer) is { } obj)
                    {
                        obj.Refs++;
                    }
                }

                Ok();
            }

            public void RetStr(string text)
            {
                if (Rt.IsWritable(ResultPointer, 4))
                {
                    Rt._emulator.WriteUInt32(ResultPointer, text.Length == 0 ? 0 : (uint)Rt._strings.Create(text));
                }

                Ok();
            }

            public void RetU32(uint value) => RetBytes(BitConverter.GetBytes(value));

            public void RetI32(int value) => RetBytes(BitConverter.GetBytes(value));

            public void RetU64(ulong value) => RetBytes(BitConverter.GetBytes(value));

            public void RetI64(long value) => RetBytes(BitConverter.GetBytes(value));

            public void RetBool(bool value) => RetBytes([value ? (byte)1 : (byte)0]);

            public void RetSingle(float value) => RetBytes(BitConverter.GetBytes(value));

            public void RetDouble(double value) => RetBytes(BitConverter.GetBytes(value));

            public void RetBytes(byte[] value)
            {
                if (Rt.IsWritable(ResultPointer, value.Length))
                {
                    Rt._emulator.WriteMemory(ResultPointer, value);
                }

                Ok();
            }

            /// <summary>An out array (<c>T[]&amp;</c>): (UInt32* length, T** data) at the parameter's two words.</summary>
            public void OutArray(int index, uint length, long data)
            {
                int word = _layout.Parameters[index].Word;
                if (Rt.IsWritable(Rt.Arg(word), 4))
                {
                    Rt._emulator.WriteUInt32(Rt.Arg(word), length);
                }

                if (Rt.IsWritable(Rt.Arg(word + 1), 4))
                {
                    Rt._emulator.WriteUInt32(Rt.Arg(word + 1), (uint)data);
                }
            }

            /// <summary>A plain out-parameter (<c>T&amp;</c>) of a scalar type.</summary>
            public void OutBytes(int index, byte[] value)
            {
                long pointer = P(index);
                if (Rt.IsWritable(pointer, value.Length))
                {
                    Rt._emulator.WriteMemory(pointer, value);
                }
            }
        }

        private sealed partial class SystemClasses
        {
            public WinRtRuntime Runtime => _rt;
        }
    }
}
