using System.Reflection;
using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// vccorlib110 and the WinRT/COM ABI API sets: Platform::Exception/Type/boxing, the object context,
/// weak references, restricted error info, the HSTRING functions, IStream over guest memory.
/// </summary>
/// <remarks>
/// Several of these call back into COM methods. Nothing guest-side runs here, so every object
/// involved is made of host traps, and <see cref="Pump"/> plays the CPU: it enters whatever trap
/// r12 names until control comes back to the caller's return address.
/// </remarks>
public sealed class VccorlibSuite : IContractSuite
{
    private const int R0 = Harness.R0, R1 = Harness.R1, R2 = Harness.R2, R3 = Harness.R3, SP = Harness.SP, LR = 10, R12 = 78;
    private const long Caller = 0x00401001;

    private Harness _h = null!;

    public string Name => "vccorlib + WinRT ABI";

    private ArmEmulator E => _h.E;

    private static readonly MethodInfo EnterTrap =
        typeof(ArmEmulator).GetMethod("EnterTrap", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Runs host traps until control returns to <see cref="Caller"/>.</summary>
    private void Pump()
    {
        for (int i = 0; i < 200; i++)
        {
            long next = E.ReadRegister(R12) & 0xFFFFFFFEL;
            if (next == (Caller & ~1L))
            {
                return;
            }

            if (E.TrapNameAt(next) is null)
            {
                throw new InvalidOperationException($"control went to 0x{next:X8}, which is not a host trap");
            }

            EnterTrap.Invoke(E, [next]);
        }

        throw new InvalidOperationException("no return after 200 traps");
    }

    private void SetArguments(long[] args)
    {
        int[] registers = [R0, R1, R2, R3];
        for (int i = 0; i < args.Length && i < 4; i++) E.WriteRegister(registers[i], args[i]);
        for (int i = 4; i < args.Length; i++) E.WriteUInt32(_h.Stack + ((i - 4) * 4), (uint)args[i]);
        E.WriteRegister(SP, _h.Stack);
        E.WriteRegister(LR, Caller);
        E.WriteRegister(R12, Caller);
    }

    /// <summary>An import, letting any calls it makes into COM methods run.</summary>
    private long Import(string function, params long[] args)
    {
        SetArguments(args);
        _h.S.Dispatch("vccorlib110.DLL!" + function);
        Pump();
        return E.ReadRegister(R0) & 0xFFFFFFFFL;
    }

    /// <summary>Method <paramref name="slot"/> of the interface at <paramref name="self"/>.</summary>
    private long Method(long self, int slot, params long[] args)
    {
        SetArguments([self, .. args]);
        long function = E.ReadUInt32(E.ReadUInt32(self) + (slot * 4));
        EnterTrap.Invoke(E, [function & ~1L]);
        Pump();
        return E.ReadRegister(R0) & 0xFFFFFFFFL;
    }

    private long Iid(Guid iid)
    {
        long p = E.AllocateHeap(16);
        E.WriteMemory(p, iid.ToByteArray());
        return p;
    }

    private long Out() => _h.Buffer(16);

    private string Text(long hstring) =>
        hstring == 0 ? string.Empty : Encoding.Unicode.GetString(E.ReadMemory(hstring + 4, (int)E.ReadUInt32(hstring) * 2));

    private long HString(string text)
    {
        long pointer = _h.Wide(text), result = Out();
        _h.Call("WindowsCreateString", pointer, text.Length, result);
        return E.ReadUInt32(result);
    }

    /// <summary>A trap-backed vtable of host methods, for objects the "guest" owns.</summary>
    private long Vtable(string name, params Action[] slots)
    {
        long vtable = E.AllocateHeap(slots.Length * 4);
        for (int i = 0; i < slots.Length; i++)
        {
            E.WriteUInt32(vtable + (i * 4), (uint)ArmEmulator.ThumbEntry(E.RegisterVtableMethod($"{name}::{i}", slots[i])));
        }

        return vtable;
    }

    private long Reg(int register) => E.ReadRegister(register) & 0xFFFFFFFFL;

    private void Ret(long value) => E.WriteRegister(R0, value);

    public void Run(Harness h)
    {
        _h = h;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        HostStubs s = h.S;
        const string Pl = "@Platform@@";
        const uint EBounds = 0x8000000B, EInvalidArg = 0x80070057, ENoInterface = 0x80004002, ENotImpl = 0x80004001, TypeMismatch = 0x80028CA0, Overflow = 0x8002000A;

        // ---- __abi_FailFast ends the guest (first: it is the one check that reads StopReason) ----
        Import("?__abi_FailFast@@YAXXZ");
        Check("__abi_FailFast stops the guest, by name", E.StopReason?.Contains("__abi_FailFast") == true, E.StopReason ?? "not stopped");

        // ---- parameterised IIDs ----
        Check("IReference<Int32> IID", HostStubsProbe.ReferenceIid("i4") == new Guid("548cefbd-bc8a-5fa0-8df2-957440fc8bf4"), HostStubsProbe.ReferenceIid("i4").ToString());
        Check("IReference<String> IID", HostStubsProbe.ReferenceIid("string") == new Guid("fd416dfb-2a07-52eb-aae3-dfce14116c05"), HostStubsProbe.ReferenceIid("string").ToString());
        Check("IReference<Boolean> IID", HostStubsProbe.ReferenceIid("b1") == new Guid("3c00fd60-2950-5939-a21a-2d12c5a01b8a"), HostStubsProbe.ReferenceIid("b1").ToString());

        // ---- GetIidsFn ----
        Guid[] two = [Guid.NewGuid(), Guid.NewGuid()];
        long iids = E.AllocateHeap(32);
        E.WriteMemory(iids, [.. two[0].ToByteArray(), .. two[1].ToByteArray()]);
        long count = Out(), copy = Out();
        long r = Import("?GetIidsFn@@YAJHPAKPBU__s_GUID@@PAPAVGuid@Platform@@@Z", 2, count, iids, copy);
        long copied = E.ReadUInt32(copy);
        Check("GetIidsFn: S_OK, count, a separate copy of the IIDs",
            r == 0 && E.ReadUInt32(count) == 2 && copied != 0 && copied != iids && E.ReadGuid(copied + 16) == two[1]);

        // ---- base constructors ----
        long obj = h.Buffer(16);
        Check("Object::Object returns this", Import($"??0Object{Pl}Q$AAA@XZ", obj) == obj);
        long name = Out();
        Check("and installs an IInspectable vtable naming Platform.Object",
            Method(obj, 4, name) == 0 && Text(E.ReadUInt32(name)) == "Platform.Object", Text(E.ReadUInt32(name)));
        Import($"??0Delegate{Pl}Q$AAA@XZ", obj);
        Method(obj, 4, name);
        Check("Delegate::Delegate names Platform.Delegate", Text(E.ReadUInt32(name)) == "Platform.Delegate");

        // ---- exceptions ----
        long ex = h.Buffer(0x44);
        Check("COMException(hr) returns this", Import($"??0COMException{Pl}Q$AAA@H@Z", ex, 0x80070002) == ex);
        Check("it keeps the HRESULT where vccorlib does (+0x20)", E.ReadUInt32(ex + 0x20) == 0x80070002);
        Check("ReCreateFromException returns its HRESULT", Import($"?ReCreateFromException@Details{Pl}YAJP$AAVException@2@@Z", ex) == 0x80070002);
        Check("Message is the system text for it",
            Text(Import($"?get@Message@Exception{Pl}Q$AAAP$AAVString@3@XZ", ex)) == "The system cannot find the file specified.\r\n");
        Method(ex, 4, name);
        Check("GetRuntimeClassName is Platform.COMException", Text(E.ReadUInt32(name)) == "Platform.COMException");
        long printable = Out();
        Check("QI IPrintable gives the interface at +4", Method(ex, 0, Iid(new Guid("de0cbaeb-8065-4a45-96b1-c9d443f9bab3")), printable) == 0 && E.ReadUInt32(printable) == ex + 4);
        long text = Out();
        Check("IPrintable::__abi_ToString is the message", Method(ex + 4, 6, text) == 0 && Text(E.ReadUInt32(text)).StartsWith("The system cannot find the file"));
        Check("QI for an unknown IID: E_NOINTERFACE, null", Method(ex, 0, Iid(Guid.NewGuid()), printable) == ENoInterface && E.ReadUInt32(printable) == 0);
        Check("AddRef/Release count", Method(ex, 1) == 3 && Method(ex, 2) == 2);   // 1 + the QI above

        long oom = h.Buffer(0x44);
        Import($"??0OutOfMemoryException{Pl}Q$AAA@XZ", oom);
        Check("a named exception's constructor knows its own HRESULT", E.ReadUInt32(oom + 0x20) == 0x8007000E);
        long msgEx = h.Buffer(0x44);
        Import($"??0InvalidArgumentException{Pl}Q$AAA@P$AAVString@1@@Z", msgEx, HString("bad"));
        Check("and its (String^) overload too", E.ReadUInt32(msgEx + 0x20) == 0x80070057);

        long created = Import($"?CreateException@Exception{Pl}SAP$AAV12@H@Z", 0x80004001);
        Method(created, 4, name);
        Check("CreateException(E_NOTIMPL) is a NotImplementedException",
            Text(E.ReadUInt32(name)) == "Platform.NotImplementedException" && E.ReadUInt32(created + 0x20) == 0x80004001);
        created = Import($"?CreateException@Exception{Pl}SAP$AAV12@H@Z", 0x88990011);
        Method(created, 4, name);
        Check("CreateException(any other failure) is a COMException", Text(E.ReadUInt32(name)) == "Platform.COMException");
        created = Import($"?ReCreateException@Exception{Pl}SAP$AAV12@H@Z", 0x80000013);
        Method(created, 4, name);
        Check("ReCreateException(RO_E_CLOSED) is an ObjectDisposedException", Text(E.ReadUInt32(name)) == "Platform.ObjectDisposedException");
        int raisedBefore = s.RaiseLog.Count;
        E.WriteRegister(LR, Caller);
        SetArguments([0]);
        s.Dispatch($"vccorlib110.DLL!?CreateException@Exception{Pl}SAP$AAV12@H@Z");
        Check("CreateException(S_OK) raises InvalidArgumentException",
            s.RaiseLog.Count > raisedBefore && s.RaiseLog[^1].StartsWith("InvalidArgumentException"), s.RaiseLog.Count > 0 ? s.RaiseLog[^1] : "nothing raised");
        Check("the raise path builds vccorlib's layout", s.ConstructedExceptions[^1].Contains("raised"));

        // ---- Platform::Type ----
        long typeName = h.Wide("Int32");
        long descriptor = E.AllocateHeap(8);
        E.WriteUInt32(descriptor, (uint)typeName);
        E.WriteUInt32(descriptor + 4, 9);
        long type = Import("?__abi_make_type_id@@YAP$AAVType@Platform@@ABU__abi_type_descriptor@@@Z", descriptor);
        Check("__abi_make_type_id: FullName", Text(Import($"?get@FullName@Type{Pl}Q$AAAP$AAVString@3@XZ", type)) == "Int32");
        Check("and GetTypeCode", Import($"?GetTypeCode@Type{Pl}SA?AW4TypeCode@2@P$AAV12@@Z", type) == 9);
        Check("and the descriptor at +0x10, as vccorlib keeps it", E.ReadUInt32(type + 0x10) == descriptor);

        // ---- boxing ----
        long value = E.AllocateHeap(4);
        E.WriteUInt32(value, 300);
        long boxed = Import($"?CreateValue@Details{Pl}YAP$AAVObject@2@P$AAVType@2@PBX@Z", type, value);
        Check("CreateValue(Int32) boxes", boxed != 0);
        long pv = Out();
        Check("QI IPropertyValue", Method(boxed, 0, Iid(new Guid("4bd682dd-7554-40e9-9a9b-82654ede7e62")), pv) == 0 && E.ReadUInt32(pv) == boxed);
        long o = Out();
        Check("get_Type is Int32 (4)", Method(boxed, 6, o) == 0 && E.ReadUInt32(o) == 4);
        Check("IsNumericScalar", Method(boxed, 7, o) == 0 && E.ReadMemory(o, 1)[0] == 1);
        Check("GetInt32", Method(boxed, 11, o) == 0 && E.ReadUInt32(o) == 300);
        Check("GetInt16 converts", Method(boxed, 9, o) == 0 && BitConverter.ToInt16(E.ReadMemory(o, 2)) == 300);
        Check("GetUInt8 of 300 is DISP_E_OVERFLOW", Method(boxed, 8, o) == Overflow);
        Check("GetDouble converts", Method(boxed, 16, o) == 0 && BitConverter.ToDouble(E.ReadMemory(o, 8)) == 300.0);
        Check("GetString is TYPE_E_TYPEMISMATCH", Method(boxed, 19, o) == TypeMismatch);
        long reference = Out();
        Check("QI IReference<Int32> then get_Value",
            Method(boxed, 0, Iid(new Guid("548cefbd-bc8a-5fa0-8df2-957440fc8bf4")), reference) == 0 &&
            Method(E.ReadUInt32(reference), 6, o) == 0 && E.ReadUInt32(o) == 300);
        const string ObjectToString = "?__abi_ObjectToString@__abi_details@@YAP$AAVString@Platform@@P$AAVObject@3@_N@Z";
        Check("__abi_ObjectToString of a boxed int", Text(Import(ObjectToString, boxed, 1)) == "300");
        long boxedType = Import($"?GetType@Object{Pl}Q$AAAP$AAVType@2@XZ", boxed);
        Check("Object::GetType of a boxed int is Int32",
            Text(Import($"?get@FullName@Type{Pl}Q$AAAP$AAVString@3@XZ", boxedType)) == "Int32" &&
            Import($"?GetTypeCode@Type{Pl}SA?AW4TypeCode@2@P$AAV12@@Z", boxedType) == 9);

        long doubleName = h.Wide("Double");
        E.WriteUInt32(descriptor, (uint)doubleName);
        E.WriteUInt32(descriptor + 4, 14);
        long doubleType = Import("?__abi_make_type_id@@YAP$AAVType@Platform@@ABU__abi_type_descriptor@@@Z", descriptor);
        long dv = E.AllocateHeap(8);
        foreach ((double d, string expected) in new[] { (1.5, "1.5"), (1e6, "1E+006"), (0.0001, "0.0001"), (123456789.0, "1.23457E+008"), (-0.25, "-0.25") })
        {
            E.WriteMemory(dv, BitConverter.GetBytes(d));
            string got = Text(Import(ObjectToString, Import($"?CreateValue@Details{Pl}YAP$AAVObject@2@P$AAVType@2@PBX@Z", doubleType, dv), 0));
            Check($"a boxed {d} prints as %G: {expected}", got == expected, got);
        }

        long hello = HString("hello");
        long boxedString = Import("?__abi_cast_String_to_Object@__abi_details@@YAP$AAVObject@Platform@@P$AAVString@3@@Z", hello);
        const string ObjectToStringCast = "?__abi_cast_Object_to_String@__abi_details@@YAP$AAVString@Platform@@_NP$AAVObject@3@@Z";
        Check("String^ -> Object^ -> String^", Text(Import(ObjectToStringCast, 1, boxedString)) == "hello");
        Check("dynamic_cast<String^> of a boxed int is null", Import(ObjectToStringCast, 1, boxed) == 0);
        Check("a null String^ boxes to null", Import("?__abi_cast_String_to_Object@__abi_details@@YAP$AAVObject@Platform@@P$AAVString@3@@Z", 0) == 0);

        // A "guest" object (not host-made) that is printable: ObjectToString goes through QI and the vtable.
        long guest = E.AllocateHeap(8);
        long printVtable = 0;
        long guestVtable = Vtable("guest",
            () =>
            {
                Guid asked = E.ReadGuid(Reg(R1));
                bool printable = asked == new Guid("de0cbaeb-8065-4a45-96b1-c9d443f9bab3");
                E.WriteUInt32(Reg(R2), printable ? (uint)(guest + 4) : 0);
                Ret(printable ? 0 : ENoInterface);
            },
            () => Ret(2), () => Ret(1));
        printVtable = Vtable("guest IPrintable",
            () => Ret(ENoInterface), () => Ret(2), () => Ret(1), () => Ret(0), () => Ret(0), () => Ret(0),
            () =>
            {
                E.WriteUInt32(Reg(R1), (uint)HString("from the guest"));
                Ret(0);
            });
        E.WriteUInt32(guest, (uint)guestVtable);
        E.WriteUInt32(guest + 4, (uint)printVtable);
        Check("__abi_ObjectToString calls a guest IPrintable", Text(Import(ObjectToString, guest, 1)) == "from the guest");

        // ---- value ToStrings ----
        long word = E.AllocateHeap(16);
        E.WriteUInt32(word, unchecked((uint)-5));
        Check("int32::ToString", Text(Import("?ToString@int32@default@@QAAP$AAVString@Platform@@XZ", word)) == "-5");
        E.WriteMemory(word, BitConverter.GetBytes(ulong.MaxValue));
        Check("uint64::ToString", Text(Import("?ToString@uint64@default@@QAAP$AAVString@Platform@@XZ", word)) == "18446744073709551615");
        E.WriteMemory(word, BitConverter.GetBytes(2.5f));
        Check("float32::ToString", Text(Import("?ToString@float32@default@@QAAP$AAVString@Platform@@XZ", word)) == "2.5");
        Guid g = new("0123abcd-4567-89ab-cdef-0123456789ab");
        E.WriteMemory(word, g.ToByteArray());
        Check("Guid::ToString is lower case in braces", Text(Import($"?ToString@Guid{Pl}QAAP$AAVString@2@XZ", word)) == "{0123abcd-4567-89ab-cdef-0123456789ab}");

        // ---- IBox / IBoxArray vtables ----
        long primaryThis = 0;
        long primary = Vtable("box primary",
            () => Ret(0), () => { primaryThis = Reg(R0); Ret(77); }, () => Ret(1),
            () =>
            {
                long list = E.AllocateHeap(16);
                E.WriteMemory(list, HostStubsProbe.ReferenceArrayIid("i4").ToByteArray());
                E.WriteUInt32(Reg(R1), 1);
                E.WriteUInt32(Reg(R2), (uint)list);
                Ret(0);
            });
        long boxObject = E.AllocateHeap(8);
        E.WriteUInt32(boxObject, (uint)primary);
        E.WriteUInt32(boxObject + 4, (uint)Import($"?GetIBoxVtable@Details{Pl}YAPAXPAX@Z", 0));
        Check("IBox: IUnknown delegates to the primary at this-4", Method(boxObject + 4, 1) == 77 && primaryThis == boxObject);
        Check("IBox: get_Type is OtherType", Method(boxObject + 4, 6, o) == 0 && E.ReadUInt32(o) == 20);
        Check("IBox: GetInt32 is E_NOTIMPL", Method(boxObject + 4, 11, o) == ENotImpl);
        E.WriteUInt32(o + 4, 0xDEAD);
        Check("IBox: an array getter nulls its value", Method(boxObject + 4, 26, o, o + 4) == ENotImpl && E.ReadUInt32(o + 4) == 0);
        E.WriteUInt32(boxObject + 4, (uint)Import($"?GetIBoxArrayVtable@Details{Pl}YAPAXPAX@Z", 0));
        Check("IBoxArray: get_Type reads the element type from GetIids (Int32Array = 1028)", Method(boxObject + 4, 6, o) == 0 && E.ReadUInt32(o) == 1028, $"{E.ReadUInt32(o)}");
        Check("IBoxArray: a getter for another element type is TYPE_E_TYPEMISMATCH", Method(boxObject + 4, 26, o, o + 4) == TypeMismatch);

        // ---- weak references ----
        long target = E.AllocateHeap(4);
        long weakObject = E.AllocateHeap(4);
        long sourceObject = E.AllocateHeap(4);
        E.WriteUInt32(target, (uint)Vtable("weak target",
            () => { E.WriteUInt32(Reg(R2), (uint)sourceObject); Ret(0); }, () => Ret(2), () => Ret(1)));
        E.WriteUInt32(sourceObject, (uint)Vtable("weak source",
            () => Ret(ENoInterface), () => Ret(2), () => Ret(1),
            () => { E.WriteUInt32(Reg(R1), (uint)weakObject); Ret(0); }));
        E.WriteUInt32(weakObject, (uint)Vtable("weak reference",
            () => Ret(ENoInterface), () => Ret(2), () => Ret(1),
            () => { E.WriteUInt32(Reg(R2), (uint)target); Ret(0); }));
        long weak = Import($"?GetWeakReference@Details{Pl}YAPAU__abi_IUnknown@@Q$ADVObject@2@@Z", target);
        Check("GetWeakReference: IWeakReferenceSource::GetWeakReference", weak == weakObject);
        long holder = E.AllocateHeap(4);
        E.WriteUInt32(holder, (uint)weak);
        Check("ResolveWeakReference: IWeakReference::Resolve",
            Import($"?ResolveWeakReference@Details{Pl}YAP$AAVObject@2@ABU_GUID@@PAPAU__abi_IUnknown@@@Z", Iid(Guid.NewGuid()), holder) == target);
        E.WriteUInt32(holder, 0);
        Check("ResolveWeakReference of null is null",
            Import($"?ResolveWeakReference@Details{Pl}YAP$AAVObject@2@ABU_GUID@@PAPAU__abi_IUnknown@@@Z", Iid(Guid.NewGuid()), holder) == 0);

        // ---- object context ----
        long context = Import($"?GetObjectContext@Details{Pl}YAPAUIUnknown@@XZ");
        long callback = Out();
        Check("GetObjectContext: an IContextCallback",
            context != 0 && Method(context, 0, Iid(new Guid("000001da-0000-0000-c000-000000000046")), callback) == 0 && E.ReadUInt32(callback) == context);
        long seen = 0;
        long fn = ArmEmulator.ThumbEntry(E.RegisterVtableMethod("context callback", () => { seen = Reg(R0); Ret(0x1234); }));
        Check("ContextCallback runs the callback here with its data", Method(context, 3, fn, 0xDA7A, 0, 0, 0) == 0x1234 && seen == 0xDA7A);
        int released = 0;
        long releasable = E.AllocateHeap(4);
        E.WriteUInt32(releasable, (uint)Vtable("releasable",
            () => { E.WriteUInt32(Reg(R2), (uint)Reg(R0)); Ret(0); }, () => Ret(2), () => { released++; Ret(0); }));
        Check("ReleaseInContextImpl releases and reports S_OK",
            Import($"?ReleaseInContextImpl@Details{Pl}YAJPAUIUnknown@@0@Z", releasable, context) == 0 && released == 1);
        long proxy = Out();
        Check("GetProxyImpl is QueryInterface",
            Import($"?GetProxyImpl@Details{Pl}YAJPAUIUnknown@@ABU_GUID@@0PAPAU3@@Z", releasable, Iid(Guid.NewGuid()), context, proxy) == 0 &&
            E.ReadUInt32(proxy) == releasable);

        // ---- restricted error info ----
        Check("RoOriginateError of a success code is FALSE", Import("RoOriginateError", 0, 0) == 0);
        Check("RoOriginateError of a failure is TRUE", Import("RoOriginateError", 0x80004005, HString("boom")) == 1);
        long info = Out();
        Check("GetRestrictedErrorInfo hands it over", Import("GetRestrictedErrorInfo", info) == 0 && E.ReadUInt32(info) != 0);
        long details = _h.Buffer(16);
        long errorInfo = E.ReadUInt32(info);
        Method(errorInfo, 3, details, details + 4, details + 8, details + 12);
        Check("GetErrorDetails: HRESULT and the restricted description",
            E.ReadUInt32(details + 4) == 0x80004005 && _h.ReadWide(E.ReadUInt32(details + 8)) == "boom");
        Check("and the thread no longer has one (S_FALSE, null)", Import("GetRestrictedErrorInfo", info) == 1 && E.ReadUInt32(info) == 0);
        Import("SetRestrictedErrorInfo", errorInfo);
        Check("SetRestrictedErrorInfo then Get gives it back", Import("GetRestrictedErrorInfo", info) == 0 && E.ReadUInt32(info) == errorInfo);
        Check("RoTransformError(S_OK, S_OK) is FALSE", Import("RoTransformError", 0, 0, 0) == 0);
        Check("RoTransformError to a failure is TRUE", Import("RoTransformError", 0x80004005, 0x8000000B, 0) == 1);
        Import("GetRestrictedErrorInfo", info);
        Method(E.ReadUInt32(info), 3, details, details + 4, details + 8, details + 12);
        Check("and records the new error", E.ReadUInt32(details + 4) == 0x8000000B);

        // ---- strings ----
        Check("WindowsIsStringEmpty(null)", Import("WindowsIsStringEmpty", 0) == 1);
        Check("WindowsIsStringEmpty(\"hello\")", Import("WindowsIsStringEmpty", hello) == 0);
        long flag = Out();
        Check("WindowsStringHasEmbeddedNull", Import("WindowsStringHasEmbeddedNull", HString("a\0b"), flag) == 0 && E.ReadUInt32(flag) == 1 &&
            Import("WindowsStringHasEmbeddedNull", hello, flag) == 0 && E.ReadUInt32(flag) == 0);
        long sub = Out();
        Check("WindowsSubstring", Import("WindowsSubstring", hello, 2, sub) == 0 && Text(E.ReadUInt32(sub)) == "llo");
        Check("WindowsSubstring at the end is the null string", Import("WindowsSubstring", hello, 5, sub) == 0 && E.ReadUInt32(sub) == 0);
        Check("WindowsSubstring past the end is E_BOUNDS", Import("WindowsSubstring", hello, 6, sub) == EBounds);
        Check("WindowsSubstringWithSpecifiedLength", Import("WindowsSubstringWithSpecifiedLength", hello, 1, 3, sub) == 0 && Text(E.ReadUInt32(sub)) == "ell");
        Check("WindowsSubstringWithSpecifiedLength past the end is E_BOUNDS", Import("WindowsSubstringWithSpecifiedLength", hello, 3, 3, sub) == EBounds);
        Check("WindowsTrimStringStart", Import("WindowsTrimStringStart", HString("  xx hi "), HString(" x"), sub) == 0 && Text(E.ReadUInt32(sub)) == "hi ");
        Check("WindowsTrimStringEnd", Import("WindowsTrimStringEnd", HString("hi..."), HString("."), sub) == 0 && Text(E.ReadUInt32(sub)) == "hi");
        Check("WindowsTrimStringStart with nothing to trim with is E_INVALIDARG", Import("WindowsTrimStringStart", hello, 0, sub) == EInvalidArg);
        Check("WindowsReplaceString", Import("WindowsReplaceString", HString("a-b-c"), HString("-"), HString("+="), sub) == 0 && Text(E.ReadUInt32(sub)) == "a+=b+=c");
        long buffer = Out(), bufferHandle = Out();
        Check("WindowsPreallocateStringBuffer", Import("WindowsPreallocateStringBuffer", 3, buffer, bufferHandle) == 0 && E.ReadUInt32(buffer) != 0);
        E.WriteMemory(E.ReadUInt32(buffer), Encoding.Unicode.GetBytes("abc"));
        Check("WindowsPromoteStringBuffer", Import("WindowsPromoteStringBuffer", E.ReadUInt32(bufferHandle), sub) == 0 && Text(E.ReadUInt32(sub)) == "abc");
        Import("WindowsPreallocateStringBuffer", 2, buffer, bufferHandle);
        E.WriteMemory(E.ReadUInt32(buffer), Encoding.Unicode.GetBytes("xyz"));   // overruns the terminator
        Check("promoting an unterminated buffer is E_INVALIDARG", Import("WindowsPromoteStringBuffer", E.ReadUInt32(bufferHandle), sub) == EInvalidArg);
        Check("WindowsDeleteStringBuffer", Import("WindowsDeleteStringBuffer", E.ReadUInt32(bufferHandle)) == 0);

        // ---- COM ----
        long guid = Iid(g);
        long wide = _h.Buffer(100);
        Check("StringFromGUID2", Import("StringFromGUID2", guid, wide, 39) == 39 && _h.ReadWide(wide) == "{0123ABCD-4567-89AB-CDEF-0123456789AB}");
        Check("StringFromGUID2 into too small a buffer is 0", Import("StringFromGUID2", guid, wide, 38) == 0);
        long parsed = Out();
        Check("CLSIDFromString round-trips", Import("CLSIDFromString", wide, parsed) == 0 && E.ReadGuid(parsed) == g);
        Check("CLSIDFromString of a ProgID is CO_E_CLASSSTRING", Import("CLSIDFromString", _h.Wide("Some.ProgId"), parsed) == 0x800401F3);
        Check("IIDFromString of garbage is E_INVALIDARG", Import("IIDFromString", _h.Wide("{nope}"), parsed) == EInvalidArg);
        Check("CoInitializeEx nests, CoUninitialize unwinds",
            Import("CoInitializeEx", 0, 0) == 0 && Import("RoInitialize", 1) == 1 && Import("CoUninitialize") == 0 &&
            Import("CoInitializeEx", 0, 0) == 1);
        Import("CoUninitialize");
        Import("CoUninitialize");
        long grown = Import("CoTaskMemRealloc", Import("CoTaskMemAlloc", 4), 64);
        Check("CoTaskMemRealloc", grown != 0);

        long streamOut = Out();
        Check("CreateStreamOnHGlobal(NULL)", Import("CreateStreamOnHGlobal", 0, 1, streamOut) == 0 && E.ReadUInt32(streamOut) != 0);
        long stream = E.ReadUInt32(streamOut);
        long data = _h.Narrow("hello world"), done = Out();
        Check("IStream::Write", Method(stream, 4, data, 11, done) == 0 && E.ReadUInt32(done) == 11);
        long position = Out();
        Check("IStream::Seek(0, SET)", Method(stream, 5, 0, 0, 0, 0, position) == 0 && E.ReadUInt32(position) == 0);
        long read = _h.Buffer(32);
        Check("IStream::Read", Method(stream, 3, read, 5, done) == 0 && E.ReadUInt32(done) == 5 && _h.ReadNarrow(read) == "hello");
        long stat = _h.Buffer(72);
        Check("IStream::Stat: a stream of 11 bytes", Method(stream, 12, stat, 1) == 0 && E.ReadUInt32(stat + 4) == 2 && E.ReadUInt32(stat + 8) == 11);
        long cloneOut = Out();
        Method(stream, 13, cloneOut);
        long clone = E.ReadUInt32(cloneOut);
        long read2 = _h.Buffer(32);
        Check("IStream::Clone shares the data and the position", Method(clone, 3, read2, 32, done) == 0 && _h.ReadNarrow(read2) == " world");
        Method(stream, 6, 0, 4, 0);   // SetSize(4)
        Method(stream, 5, 0, 0, 0, 0, 0);
        long read3 = _h.Buffer(32);
        Check("IStream::SetSize truncates", Method(stream, 3, read3, 32, done) == 0 && E.ReadUInt32(done) == 4 && _h.ReadNarrow(read3) == "hell");
        Check("IStream::Seek before the start fails", Method(stream, 5, 0, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0) == 0x80030001);
        Import("CreateStreamOnHGlobal", 0, 1, streamOut);
        long copyTarget = E.ReadUInt32(streamOut);
        Method(stream, 5, 0, 0, 0, 0, 0);
        long counts = _h.Buffer(16);
        Check("IStream::CopyTo another stream", Method(stream, 7, copyTarget, 100, 0, counts, counts + 8) == 0 &&
            E.ReadUInt32(counts) == 4 && E.ReadUInt32(counts + 8) == 4);
        Method(copyTarget, 5, 0, 0, 0, 0, 0);
        long read4 = _h.Buffer(32);
        Method(copyTarget, 3, read4, 32, done);
        Check("and the target holds the bytes", _h.ReadNarrow(read4) == "hell");
        Check("QI IStream", Method(stream, 0, Iid(new Guid("0000000c-0000-0000-c000-000000000046")), position) == 0 && E.ReadUInt32(position) == stream);

        long factory = Out();
        Check("RoGetActivationFactory", Import("RoGetActivationFactory", HString("Windows.Phone.UI.Input.HardwareButtons"), Iid(Guid.NewGuid()), factory) == 0 && E.ReadUInt32(factory) != 0);

        // ---- Rect / GridLength ----
        long rect = E.AllocateHeap(16);
        E.WriteMemory(rect, [.. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(2f), .. BitConverter.GetBytes(10f), .. BitConverter.GetBytes(-1f)]);
        Import("?get@Right@Rect@Foundation@Windows@@QAAMXZ", rect);
        Check("Rect::Right is X + Width", h.S32(0) == 11f);
        Import("?get@Bottom@Rect@Foundation@Windows@@QAAMXZ", rect);
        Check("Rect::Bottom of a negative height is -infinity", float.IsNegativeInfinity(h.S32(0)));
        long grid = E.AllocateHeap(16);
        h.SetD64(0, 2.5);
        Check("GridLength(2.5, Star)", Import("??0GridLength@Xaml@UI@Windows@@QAA@NW4GridUnitType@123@@Z", grid, 2) == grid &&
            BitConverter.ToDouble(E.ReadMemory(grid, 8)) == 2.5 && E.ReadUInt32(grid + 8) == 2);
        h.SetD64(0, 7);
        Import("??0GridLength@Xaml@UI@Windows@@QAA@NW4GridUnitType@123@@Z", grid, 0);
        Check("GridLength(x, Auto) stores 1.0", BitConverter.ToDouble(E.ReadMemory(grid, 8)) == 1.0);

        // ---- the census names this change implements are no longer defaults ----
        string[] implemented =
        [
            "?__abi_FailFast@@YAXXZ", "?GetIidsFn@@YAJHPAKPBU__s_GUID@@PAPAVGuid@Platform@@@Z", $"??0Object{Pl}Q$AAA@XZ",
            $"?ReCreateFromException@Details{Pl}YAJP$AAVException@2@@Z", $"?UninitializeData@Details{Pl}YAXH@Z",
            $"??0Delegate{Pl}Q$AAA@XZ", $"?CreateException@Exception{Pl}SAP$AAV12@H@Z", $"?ReCreateException@Exception{Pl}SAP$AAV12@H@Z",
            "?__abi_ObjectToString@__abi_details@@YAP$AAVString@Platform@@P$AAVObject@3@_N@Z",
            "?__abi_cast_String_to_Object@__abi_details@@YAP$AAVObject@Platform@@P$AAVString@3@@Z",
            "?__abi_make_type_id@@YAP$AAVType@Platform@@ABU__abi_type_descriptor@@@Z", $"?CreateValue@Details{Pl}YAP$AAVObject@2@P$AAVType@2@PBX@Z",
            $"?GetIBoxVtable@Details{Pl}YAPAXPAX@Z", $"?ReleaseInContextImpl@Details{Pl}YAJPAUIUnknown@@0@Z",
            $"?get@Message@Exception{Pl}Q$AAAP$AAVString@3@XZ", $"?GetObjectContext@Details{Pl}YAPAUIUnknown@@XZ",
            $"?GetProxyImpl@Details{Pl}YAJPAUIUnknown@@ABU_GUID@@0PAPAU3@@Z", "CreateStreamOnHGlobal", "WindowsIsStringEmpty",
            "WindowsStringHasEmbeddedNull", "CoUnmarshalInterface", "StringFromGUID2", "CLSIDFromString", "SetRestrictedErrorInfo",
            "GetRestrictedErrorInfo", "RoTransformError", "RoActivateInstance", "RoGetActivationFactory", "CoUninitialize",
            "RoFailFastWithErrorContext", "RoOriginateError", "RoReportUnhandledError", "RoGetBufferMarshaler",
            $"?ToString@Guid{Pl}QAAP$AAVString@2@XZ", "?ToString@int32@default@@QAAP$AAVString@Platform@@XZ",
            $"?CreateException@Exception{Pl}SAP$AAV12@HP$AAVString@2@@Z", $"?GetWeakReference@Details{Pl}YAPAU__abi_IUnknown@@Q$ADVObject@2@@Z",
            $"?ResolveWeakReference@Details{Pl}YAP$AAVObject@2@ABU_GUID@@PAPAU__abi_IUnknown@@@Z",
        ];
        string[] missing = implemented.Where(n => !s.IsImplemented(n)).ToArray();
        Check($"{implemented.Length} census imports report as implemented", missing.Length == 0, string.Join(", ", missing));
    }
}

/// <summary>Reaches the host's internal IID computation for the checks above.</summary>
internal static class HostStubsProbe
{
    private static readonly MethodInfo Parameterized =
        typeof(HostStubs).GetMethod("ParameterizedIid", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static Guid ReferenceIid(string signature) =>
        (Guid)Parameterized.Invoke(null, [new Guid("61c17706-2d65-11e0-9ae8-d48564015472"), signature])!;

    public static Guid ReferenceArrayIid(string signature) =>
        (Guid)Parameterized.Invoke(null, [new Guid("61c17707-2d65-11e0-9ae8-d48564015472"), signature])!;
}
