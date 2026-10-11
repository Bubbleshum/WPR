using System.Globalization;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// vccorlib110.dll - the C++/CX runtime every WP8 native title links - to the contracts of the
    /// real DLL. Where a behaviour is not obvious from the C++/CX headers it was read from vccorlib110
    /// itself (the x64 build of the same source); each such case says so.
    /// </summary>
    /// <remarks>
    /// Platform::Exception and Platform::Type are host objects laid out as vccorlib lays them out on
    /// ARM: four vptrs (Object/IInspectable, IPrintable, IEquatable, IDisposable) at 0..0xC, and the
    /// state after them - the HRESULT at 0x20 of a 0x44-byte exception (the size the images pass to
    /// Heap::AllocateException), the type descriptor at 0x10 of a Type. The images never read those
    /// fields themselves (they go through ReCreateFromException, get_Message and the vtables), so the
    /// offsets matter only for agreeing with the real DLL.
    /// </remarks>
    public sealed partial class HostStubs
    {
        private const string Pl = "@Platform@@";

        /// <summary>Size of a Platform::Exception on ARM, as the images allocate it.</summary>
        internal const int ExceptionSize = 0x44;

        /// <summary>Where vccorlib keeps an exception's HRESULT (0x40 on x64, after four vptrs and four pointers).</summary>
        internal const int ExceptionHResultOffset = 0x20;

        /// <summary>Size of a Platform::Type on ARM, and where it keeps its __abi_type_descriptor*.</summary>
        internal const int TypeSize = 0x24, TypeDescriptorOffset = 0x10;

        internal sealed class ExceptionState(string typeName, long hresult, long message)
        {
            public string TypeName { get; } = typeName;

            public long HResult { get; } = hresult;

            /// <summary>The String^ it was constructed with, if any (Message does not report it: see get_Message).</summary>
            public long Message { get; } = message;
        }

        internal sealed record TypeState(string FullName, int TypeCode);

        /// <summary>Registers everything in this file and in WinRtAbiLibrary.cs; called once from the constructor.</summary>
        private void RegisterVccorlibAndAbi()
        {
            RegisterVccorlib();
            RegisterWinRtAbi();
        }

        /// <summary>Adds a handler unless something earlier already implements the name.</summary>
        private void Add(string name, Action handler) => _handlers.TryAdd(name, handler);

        private void RegisterVccorlib()
        {
            // void __abi_FailFast(): C++/CX's answer to an exception that is not a Platform::Exception
            // reaching an ABI boundary (every generated wrapper's catch(...)). vccorlib calls
            // RoFailFastWithErrorContext: the process ends. It is the game failing, so the guest
            // stops here, by name, rather than running the code after a call that never returns.
            Add("?__abi_FailFast@@YAXXZ", () =>
            {
                Return(0);
                _emulator.Stop($"the image called __abi_FailFast() from 0x{_emulator.ReturnAddress & ~1L:X8}: " +
                    "a non-Platform exception reached a WinRT boundary, which ends the process on a phone");
            });

            // HRESULT GetIidsFn(int count, ULONG* outCount, const GUID* iids, Guid** outIids): the
            // body of every ref class's GetIids. A CoTaskMemAlloc'd copy (vccorlib110: E_OUTOFMEMORY
            // and *outCount = 0 if the allocation fails).
            Add("?GetIidsFn@@YAJHPAKPBU__s_GUID@@PAPAVGuid@Platform@@@Z", () =>
            {
                int count = Math.Max(_frame.SignedArg(0), 0);
                long copy = _emulator.AllocateHeap(Math.Max(count * 16, 1));
                WriteOut(Arg(3), copy);
                if (copy == 0)
                {
                    WriteOut(Arg(1), 0);
                    Return(EOutOfMemory);
                    return;
                }

                if (count > 0 && Arg(2) != 0)
                {
                    _emulator.WriteMemory(copy, _emulator.ReadMemory(Arg(2), count * 16));
                }

                WriteOut(Arg(1), count);
                Return(SOk);
            });

            // The four base constructors: vccorlib's each install their own vtable at this+0 and
            // return this; the derived class's constructor replaces it straight after.
            foreach ((string ctor, string runtimeClass) in new[]
            {
                ($"??0Object{Pl}Q$AAA@XZ", "Platform.Object"),
                ($"??0Delegate{Pl}Q$AAA@XZ", "Platform.Delegate"),
                ($"??0Enum{Pl}Q$AAA@XZ", "Platform.Enum"),
                ($"??0ValueType{Pl}Q$AAA@XZ", "Platform.ValueType"),
            })
            {
                Add(ctor, () =>
                {
                    if (Arg(0) != 0)
                    {
                        _emulator.WriteUInt32(Arg(0), (uint)BaseInspectableVtable(runtimeClass));
                    }

                    Return(Arg(0));
                });
            }

            // void UninitializeData(int threadingModel): the pair of InitializeData. vccorlib's ends
            // the RoInitialize it made; there is no apartment here to end.
            Add($"?UninitializeData@Details{Pl}YAXH@Z", () => Return(0));

            // HRESULT ReCreateFromException(Exception^): what a generated ABI wrapper returns from its
            // catch(Platform::Exception^). Re-establishes the exception's error info on the thread and
            // returns its HRESULT (vccorlib110: SetRestrictedErrorInfo, then the HRESULT field).
            Add($"?ReCreateFromException@Details{Pl}YAJP$AAVException@2@@Z", () =>
            {
                long exception = Arg(0);
                long hr = ExceptionHResult(exception);
                if (HostObjectAt(exception)?.State is ExceptionState state)
                {
                    OriginateError(hr, state.Message == 0 ? null : _strings.ReadText(state.Message));
                }

                Return(hr);
            });

            // static Exception^ Exception::CreateException(int hr[, String^ message]) and
            // ReCreateException(int hr): the exception class that HRESULT maps to (vccorlib110 switches
            // over exactly the HRESULTs PlatformRaise knows), COMException for any other failure. A
            // success code is not an error: Exception's constructor raises InvalidArgumentException.
            Add($"?CreateException@Exception{Pl}SAP$AAV12@H@Z", () => CreateExceptionFor(_frame.SignedArg(0), 0));
            Add($"?CreateException@Exception{Pl}SAP$AAV12@HP$AAVString@2@@Z", () => CreateExceptionFor(_frame.SignedArg(0), Arg(1)));
            Add($"?ReCreateException@Exception{Pl}SAP$AAV12@H@Z", () =>
            {
                long hr = _frame.SignedArg(0);
                CreateExceptionFor(hr, 0);
                if (IsFailure(hr) && _restrictedErrors.TryGetValue(_emulator.CurrentThreadId, out long info) && info != 0)
                {
                    // ReCreateException adopts the thread's current error info (GetRestrictedErrorInfo),
                    // which takes it off the thread.
                    _restrictedErrors.Remove(_emulator.CurrentThreadId);
                }
            });

            // String^ Exception::Message::get(): vccorlib110 formats the HRESULT with
            // FormatMessageW(FROM_SYSTEM) - the system text, not a message given to the constructor -
            // and returns null when the system has none.
            Add($"?get@Message@Exception{Pl}Q$AAAP$AAVString@3@XZ", () => Return(SystemMessageString(ExceptionHResult(Arg(0)))));

            // --- Platform::Type ---
            // Type^ __abi_make_type_id(const __abi_type_descriptor&): { const wchar_t* name; int typeCode; }.
            Add("?__abi_make_type_id@@YAP$AAVType@Platform@@ABU__abi_type_descriptor@@@Z", () =>
            {
                long descriptor = Arg(0);
                string name = descriptor == 0 ? string.Empty : ReadWide(_emulator.ReadUInt32(descriptor));
                int code = descriptor == 0 ? 0 : (int)_emulator.ReadUInt32(descriptor + 4);
                Return(CreateType(name, code, descriptor));
            });

            Add($"?get@FullName@Type{Pl}Q$AAAP$AAVString@3@XZ", () =>
                Return(TypeOf(Arg(0)) is { } type ? NewHString(type.FullName) : 0));

            Add($"?GetTypeCode@Type{Pl}SA?AW4TypeCode@2@P$AAV12@@Z", () => Return(TypeOf(Arg(0))?.TypeCode ?? 0));

            // Type^ Object::GetType(): vccorlib110's Type(Object^) - a boxed value is its value type
            // (by its PropertyType), anything else its runtime class name with TypeCode Object.
            Add($"?GetType@Object{Pl}Q$AAAP$AAVType@2@XZ", GetObjectType);

            // static bool Object::ReferenceEquals(Object^, Object^): identity.
            Add($"?ReferenceEquals@Object{Pl}SA_NP$AAV12@0@Z", () => Return(Arg(0) == Arg(1) ? 1 : 0));

            // --- boxing ---
            // Object^ CreateValue(Type^, const void* value): boxes by the type's TypeCode, through
            // PropertyValue (vccorlib110 calls IPropertyValueStatics::CreateXxx). An unknown code
            // boxes nothing.
            Add($"?CreateValue@Details{Pl}YAP$AAVObject@2@P$AAVType@2@PBX@Z", () =>
            {
                PropertyType? type = TypeOf(Arg(0)) is { } t ? PropertyTypeForTypeCode(t.TypeCode) : null;
                if (type is null || Arg(1) == 0)
                {
                    Return(0);
                    return;
                }

                byte[] raw = _emulator.ReadMemory(Arg(1), BoxableTypes[type.Value].Size);
                Return(Box(type.Value, raw));
            });

            // Object^ __abi_cast_String_to_Object(String^): PropertyValue::CreateString; null stays null.
            Add("?__abi_cast_String_to_Object@__abi_details@@YAP$AAVObject@Platform@@P$AAVString@3@@Z", () =>
                Return(Arg(0) == 0 ? 0 : Box(PropertyType.String, BitConverter.GetBytes((uint)Arg(0)))));

            // String^ __abi_cast_Object_to_String(bool dynamic, Object^): IReference<String>::get_Value.
            // An object that is not a boxed string is null under dynamic_cast and an
            // InvalidCastException under safe_cast.
            Add("?__abi_cast_Object_to_String@__abi_details@@YAP$AAVString@Platform@@_NP$AAVObject@3@@Z", CastObjectToString);

            // String^ __abi_ObjectToString(Object^, bool printable): what ToString() on an Object^ does.
            Add("?__abi_ObjectToString@__abi_details@@YAP$AAVString@Platform@@P$AAVObject@3@_N@Z", () =>
                ObjectToString(Arg(0), Arg(1) != 0, _emulator.ReturnAddress));

            // String^ Enum::ToString(): vccorlib110 is __abi_ObjectToString(this, true).
            Add($"?ToString@Enum{Pl}Q$AAAP$AAVString@2@XZ", () => ObjectToString(Arg(0), true, _emulator.ReturnAddress));

            // void* GetIBoxVtable(void*) / GetIBoxArrayVtable(void*): see BoxVtable.
            Add($"?GetIBoxVtable@Details{Pl}YAPAXPAX@Z", () => Return(BoxVtable(array: false)));
            Add($"?GetIBoxArrayVtable@Details{Pl}YAPAXPAX@Z", () => Return(BoxVtable(array: true)));

            // --- the object context: one apartment, so a context is just "here" ---
            // IUnknown* GetObjectContext(): CoGetObjectContext(IID_IUnknown). Every thread here is in
            // the same (only) apartment, so it is one context object, AddRef'd for each caller.
            Add($"?GetObjectContext@Details{Pl}YAPAUIUnknown@@XZ", () =>
            {
                HostObject context = ObjectContext();
                context.References++;
                Return(context.Address);
            });

            // HRESULT ReleaseInContextImpl(IUnknown* object, IUnknown* context): Release on the
            // object's context, which is this one.
            Add($"?ReleaseInContextImpl@Details{Pl}YAJPAUIUnknown@@0@Z", () =>
            {
                long callerReturn = _emulator.ReturnAddress;
                if (Arg(0) == 0)
                {
                    Return(SOk);
                    return;
                }

                CallMethod("ReleaseInContext", Arg(0), 2, [], () => FinishImport(callerReturn, SOk));
            });

            // HRESULT GetProxyImpl(IUnknown* object, REFIID, IUnknown* context, IUnknown** proxy): a
            // pointer usable from the caller's context - in a single apartment, QueryInterface itself.
            // Tail-called, so the object's own HRESULT and out-parameter are the answer.
            Add($"?GetProxyImpl@Details{Pl}YAJPAUIUnknown@@ABU_GUID@@0PAPAU3@@Z", () =>
            {
                long self = Arg(0), proxy = Arg(3);
                if (self == 0)
                {
                    WriteOut(proxy, 0);
                    Return(EPointer);
                    return;
                }

                _emulator.WriteRegister(Arm.UC_ARM_REG_R2, proxy);
                _emulator.ContinueAt(_emulator.ReadUInt32(_emulator.ReadUInt32(self)));
            });

            // --- weak references ---
            // __abi_IUnknown* GetWeakReference(Object^): IWeakReferenceSource::GetWeakReference.
            Add($"?GetWeakReference@Details{Pl}YAPAU__abi_IUnknown@@Q$ADVObject@2@@Z", GetWeakReference);

            // Object^ ResolveWeakReference(REFGUID, __abi_IUnknown** weak): IWeakReference::Resolve; null
            // for a null reference or one whose object has gone.
            Add($"?ResolveWeakReference@Details{Pl}YAP$AAVObject@2@ABU_GUID@@PAPAU__abi_IUnknown@@@Z", () =>
            {
                long callerReturn = _emulator.ReturnAddress;
                long weak = Arg(1) == 0 ? 0 : _emulator.ReadUInt32(Arg(1));
                if (weak == 0)
                {
                    Return(0);
                    return;
                }

                long resolved = Scratch(4);
                CallMethod("IWeakReference::Resolve", weak, 3, [Arg(0), resolved], () =>
                {
                    long hr = Arg(0);
                    long result = _emulator.ReadUInt32(resolved);
                    _emulator.FreeHeap(resolved);
                    if (IsFailure(hr))
                    {
                        RaiseForHResult(hr, callerReturn, "ResolveWeakReference");
                        return;
                    }

                    FinishImport(callerReturn, result);
                });
            });

            // --- value types' ToString: `this` is the value; formats are vccorlib110's own ---
            void ValueToString(string type, Func<long, string> format) =>
                Add($"?ToString@{type}@default@@QAAP$AAVString@Platform@@XZ", () =>
                    Return(Arg(0) == 0 ? 0 : NewHString(format(Arg(0)))));

            ValueToString("int8", p => ((sbyte)_emulator.ReadMemory(p, 1)[0]).ToString(CultureInfo.InvariantCulture));            // %hhd
            ValueToString("uint8", p => _emulator.ReadMemory(p, 1)[0].ToString(CultureInfo.InvariantCulture));                     // %hhu
            ValueToString("int16", p => BitConverter.ToInt16(_emulator.ReadMemory(p, 2)).ToString(CultureInfo.InvariantCulture));  // %hd
            ValueToString("uint16", p => BitConverter.ToUInt16(_emulator.ReadMemory(p, 2)).ToString(CultureInfo.InvariantCulture)); // %hu
            ValueToString("int32", p => ((int)_emulator.ReadUInt32(p)).ToString(CultureInfo.InvariantCulture));                     // %I32d
            ValueToString("uint32", p => _emulator.ReadUInt32(p).ToString(CultureInfo.InvariantCulture));                           // %I32u
            ValueToString("int64", p => BitConverter.ToInt64(_emulator.ReadMemory(p, 8)).ToString(CultureInfo.InvariantCulture));   // %I64d
            ValueToString("uint64", p => BitConverter.ToUInt64(_emulator.ReadMemory(p, 8)).ToString(CultureInfo.InvariantCulture)); // %I64u
            ValueToString("float32", p => FormatG(BitConverter.ToSingle(_emulator.ReadMemory(p, 4))));                              // %G
            ValueToString("float64", p => FormatG(BitConverter.ToDouble(_emulator.ReadMemory(p, 8))));                              // %G
            ValueToString("char16", p => ((char)BitConverter.ToUInt16(_emulator.ReadMemory(p, 2))).ToString());

            // String^ Guid::ToString(): "{%08x-%04hx-%04hx-%02hhx%02hhx-...}", lower case.
            Add($"?ToString@Guid{Pl}QAAP$AAVString@2@XZ", () => Return(Arg(0) == 0 ? 0 : NewHString(FormatGuid(_emulator.ReadGuid(Arg(0))))));

            // void String::<Dispose>(): vccorlib110 is WindowsDeleteString(this); strings here are
            // immutable and never freed.
            Add($"?<Dispose>@String{Pl}U$AAAXXZ", () => Return(0));

            // void* Heap::AlignedAllocate(size_t, size_t alignment) / AlignedFree(void*): the aligned
            // allocator (an alignment of 0 is 1), whichever implementation _aligned_malloc has.
            Add($"?AlignedAllocate@Heap@Details{Pl}SAPAXII@Z", () =>
            {
                if (Arg(1) == 0)
                {
                    _emulator.WriteRegister(Arm.UC_ARM_REG_R1, 1);
                }

                _handlers["_aligned_malloc"]();
            });
            Add($"?AlignedFree@Heap@Details{Pl}SAXPAX@Z", () => _handlers["_aligned_free"]());

            // --- Windows::Foundation / Xaml value types that vccorlib carries ---
            // float Rect::Right::get() / Bottom::get(): X + Width / Y + Height, or -infinity for an
            // empty rect (a negative size), as vccorlib110. `this` in r0, the float in s0.
            Add("?get@Right@Rect@Foundation@Windows@@QAAMXZ", () =>
            {
                float width = FloatAt(Arg(0) + 8);
                ReturnFloatResult(width < 0 ? float.NegativeInfinity : FloatAt(Arg(0)) + width);
            });
            Add("?get@Bottom@Rect@Foundation@Windows@@QAAMXZ", () =>
            {
                float height = FloatAt(Arg(0) + 12);
                ReturnFloatResult(height < 0 ? float.NegativeInfinity : FloatAt(Arg(0) + 4) + height);
            });

            // static Rect Rect::Empty::get(): { +inf, +inf, -inf, -inf }. A Rect is returned through
            // a hidden pointer in r0 (a class with constructors); s0-s3 are filled as well, which
            // costs nothing should a caller have been compiled to read it as a float aggregate.
            Add("?get@Empty@Rect@Foundation@Windows@@SA?AV234@XZ", () =>
            {
                float[] empty = [float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity];
                if (Arg(0) != 0)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _emulator.WriteSingle(Arg(0) + (i * 4), empty[i]);
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    _emulator.Cpu.VfpWrite(i, BitConverter.SingleToUInt32Bits(empty[i]));
                }

                Return(Arg(0));
            });

            // GridLength::GridLength(double value, GridUnitType type): `this` r0, value d0, type r1.
            // vccorlib110: NaN, +infinity, a negative value or an unknown unit raises
            // InvalidArgumentException; Auto stores the value 1.0. Layout { double Value; int Type }.
            Add("??0GridLength@Xaml@UI@Windows@@QAA@NW4GridUnitType@123@@Z", () =>
            {
                long self = Arg(0);
                double value = _frame.DoubleArg(0);
                long unit = Arg(1);
                if (double.IsNaN(value) || double.IsPositiveInfinity(value) || value < 0 || unit > 2)
                {
                    RaisePlatformException(new PlatformRaise("InvalidArgumentException", EInvalidArg & 0xFFFFFFFFL), "GridLength::GridLength");
                    return;
                }

                if (self != 0)
                {
                    _emulator.WriteMemory(self, BitConverter.GetBytes(unit == 0 ? 1.0 : value));
                    _emulator.WriteUInt32(self + 8, (uint)unit);
                }

                Return(self);
            });

            // Left defaulted on purpose (answered 0 and recorded), each for a reason:
            //  - TerminateModule(ModuleBase*): only DllCanUnloadNow calls it, the host never unloads a
            //    module, and the honest answer is the WRL module's own object count - guest state.
            //  - Rect(Point, Size) and Rect::IntersectsWith(Rect): how MSVC ARM passes a by-value
            //    Windows::Foundation struct with constructors (core registers or VFP) is not measured.
            //  - Object::GetHashCode, ValueType::Equals: one title each; they need the IEquatable /
            //    IPropertyValue protocol run against guest objects, not written yet.
            //  - Platform::String's members (Data, Length, Concat...) are not vccorlib110 exports at
            //    all: vccorlib.h inlines them over the WindowsXxx string functions (WinRtAbiLibrary.cs).
        }

        // -------------------------------------------------------------------------
        // Exceptions
        // -------------------------------------------------------------------------

        private readonly Dictionary<string, HostClass> _exceptionClasses = new(StringComparer.Ordinal);

        private HostClass ExceptionClass(string typeName)
        {
            if (_exceptionClasses.TryGetValue(typeName, out HostClass? known))
            {
                return known;
            }

            ExceptionState StateOf(HostObject o) => (ExceptionState)o.State!;
            bool SameError(HostObject o, long other) =>
                HostObjectFor(other)?.State is ExceptionState theirs && theirs.HResult == StateOf(o).HResult;

            var created = new HostClass(
                $"Platform.{typeName}",
                [
                    new HostInterface("Platform::Object", [], true),
                    new HostInterface("IPrintable", [IidPrintable], true,
                        ("__abi_ToString", o => { WriteOut(Arg(1), SystemMessageString(StateOf(o).HResult)); Return(SOk); }),
                        ("ToString", o => Return(SystemMessageString(StateOf(o).HResult)))),
                    new HostInterface("IEquatable", [IidEquatable], true,
                        ("__abi_Equals", o => { WriteBool(Arg(2), SameError(o, Arg(1))); Return(SOk); }),
                        ("__abi_GetHashCode", o => { WriteOut(Arg(1), StateOf(o).HResult); Return(SOk); }),
                        ("Equals", o => Return(SameError(o, Arg(1)) ? 1 : 0)),
                        ("GetHashCode", o => Return(StateOf(o).HResult))),
                    new HostInterface("IDisposable", [], true,
                        ("__abi_Dispose", _ => Return(SOk)),
                        ("Dispose", _ => Return(0))),
                ]);

            _exceptionClasses[typeName] = created;
            return created;
        }

        /// <summary>Builds a Platform exception of <paramref name="typeName"/> in fresh memory.</summary>
        internal long CreatePlatformException(string typeName, long hresult, long message = 0)
        {
            HostObject created = CreateHostObject(ExceptionClass(typeName), ExceptionSize, new ExceptionState(typeName, hresult & 0xFFFFFFFFL, message));
            _emulator.WriteUInt32(created.Address + ExceptionHResultOffset, (uint)hresult);
            return created.Address;
        }

        /// <summary>Makes image-allocated memory a Platform exception (what its constructor does).</summary>
        private void InitialisePlatformException(long self, string typeName, long hresult, long message)
        {
            AttachHostObject(self, ExceptionClass(typeName), new ExceptionState(typeName, hresult & 0xFFFFFFFFL, message));
            _emulator.WriteUInt32(self + ExceptionHResultOffset, (uint)hresult);
        }

        /// <summary>An exception's HRESULT: the host's record, or vccorlib's field for anything else.</summary>
        internal long ExceptionHResult(long exception) =>
            exception == 0 ? 0
            : HostObjectAt(exception)?.State is ExceptionState state ? state.HResult
            : _emulator.ReadUInt32(exception + ExceptionHResultOffset);

        /// <summary>
        /// <c>??0&lt;Name&gt;Exception@Platform@@...</c>: the image built the object (Heap::AllocateException)
        /// and asks vccorlib to construct it. Exception(int) and COMException(int) carry the HRESULT
        /// in r1; the named ones know their own, and their String^ overload takes the message in r1.
        /// Exception's constructor refuses a success code with InvalidArgumentException.
        /// </summary>
        private void ConstructPlatformException(string function)
        {
            long self = Arg(0);
            int nameEnd = function.IndexOf(Pl, StringComparison.Ordinal);
            string typeName = function[3..nameEnd];
            string parameters = function[(nameEnd + Pl.Length)..];
            bool takesHResult = parameters.StartsWith("Q$AAA@H", StringComparison.Ordinal);
            bool takesMessage = parameters.Contains("String", StringComparison.Ordinal);

            long hr;
            long message;
            if (takesHResult)
            {
                hr = Arg(1) & 0xFFFFFFFFL;
                message = takesMessage ? Arg(2) : 0;
            }
            else
            {
                hr = PlatformRaise.HResultOf(typeName) ?? 0x80004005;
                message = takesMessage ? Arg(1) : 0;
            }

            if (!IsFailure(hr))
            {
                RaisePlatformException(new PlatformRaise("InvalidArgumentException", 0x80070057), function);
                return;
            }

            if (self != 0)
            {
                InitialisePlatformException(self, typeName, hr, message);
            }

            if (_constructedExceptions.Count < 24)
            {
                _constructedExceptions.Add($"{function} hr=0x{hr:X8} at 0x{_emulator.ReturnAddress:X8}");
            }

            Return(self);
        }

        /// <summary>Exception::CreateException: the class for the HRESULT, or a raise for a success code.</summary>
        private void CreateExceptionFor(long hresult, long message)
        {
            long hr = hresult & 0xFFFFFFFFL;
            if (!IsFailure(hr))
            {
                RaisePlatformException(new PlatformRaise("InvalidArgumentException", 0x80070057), "Exception::CreateException");
                return;
            }

            string typeName = PlatformRaise.TypeNameOf(hr) ?? "COMException";
            Return(CreatePlatformException(typeName, hr, message));
        }

        /// <summary>
        /// Raises the Platform exception an HRESULT maps to from inside a chained call, as
        /// __abi_ThrowIfFailed does: the unwind starts at the import's caller, so lr is put back first.
        /// </summary>
        private void RaiseForHResult(long hresult, long callerReturn, string origin)
        {
            long hr = hresult & 0xFFFFFFFFL;
            _emulator.WriteRegister(Arm.UC_ARM_REG_LR, callerReturn);
            RaisePlatformException(new PlatformRaise(PlatformRaise.TypeNameOf(hr) ?? "COMException", hr), origin);
        }

        /// <summary>
        /// The system message FormatMessageW gives an HRESULT, as an HSTRING; 0 when it has none.
        /// The texts are the Windows message table's, CRLF included.
        /// </summary>
        private long SystemMessageString(long hresult) =>
            SystemMessages.TryGetValue((uint)hresult, out string? text) ? NewHString(text) : 0;

        internal static readonly IReadOnlyDictionary<uint, string> SystemMessages = new Dictionary<uint, string>
        {
            [0x80004001] = "Not implemented\r\n",
            [0x80004002] = "No such interface supported\r\n",
            [0x80004003] = "Invalid pointer\r\n",
            [0x80004004] = "Operation aborted\r\n",
            [0x80004005] = "Unspecified error\r\n",
            [0x8000FFFF] = "Catastrophic failure\r\n",
            [0x8000000B] = "The operation attempted to access data outside the valid range\r\n",
            [0x8000000C] = "A concurrent or interleaved operation changed the state of the object, invalidating this operation.\r\n",
            [0x80000013] = "The object has been closed.\r\n",
            [0x80010108] = "The object invoked has disconnected from its clients.\r\n",
            [0x8001010E] = "The application called an interface that was marshalled for a different thread.\r\n",
            [0x80040154] = "Class not registered\r\n",
            [0x80070002] = "The system cannot find the file specified.\r\n",
            [0x80070003] = "The system cannot find the path specified.\r\n",
            [0x80070005] = "Access is denied.\r\n",
            [0x80070006] = "The handle is invalid.\r\n",
            [0x8007000E] = "Not enough storage is available to complete this operation.\r\n",
            [0x80070057] = "The parameter is incorrect.\r\n",
        };

        // -------------------------------------------------------------------------
        // Types
        // -------------------------------------------------------------------------

        private HostClass? _typeClass;

        private HostClass TypeClass => _typeClass ??= new HostClass(
            "Platform.Type",
            [
                new HostInterface("Platform::Object", [], true),
                new HostInterface("IPrintable", [IidPrintable], true,
                    ("__abi_ToString", o => { WriteOut(Arg(1), NewHString(((TypeState)o.State!).FullName)); Return(SOk); }),
                    ("ToString", o => Return(NewHString(((TypeState)o.State!).FullName)))),
                new HostInterface("IEquatable", [IidEquatable], true,
                    ("__abi_Equals", o => { WriteBool(Arg(2), SameType(o, Arg(1))); Return(SOk); }),
                    ("__abi_GetHashCode", o => { WriteOut(Arg(1), TypeHash(o)); Return(SOk); }),
                    ("Equals", o => Return(SameType(o, Arg(1)) ? 1 : 0)),
                    ("GetHashCode", o => Return(TypeHash(o)))),
                new HostInterface("IDisposable", [], true,
                    ("__abi_Dispose", _ => Return(SOk)),
                    ("Dispose", _ => Return(0))),
            ]);

        private bool SameType(HostObject o, long other) =>
            HostObjectFor(other)?.State is TypeState theirs && theirs.FullName == ((TypeState)o.State!).FullName;

        private static long TypeHash(HostObject o) =>
            string.GetHashCode(((TypeState)o.State!).FullName, StringComparison.Ordinal) & 0xFFFFFFFFL;

        internal long CreateType(string fullName, int typeCode, long descriptor = 0)
        {
            HostObject created = CreateHostObject(TypeClass, TypeSize, new TypeState(fullName, typeCode));
            _emulator.WriteUInt32(created.Address + TypeDescriptorOffset, (uint)descriptor);
            return created.Address;
        }

        internal TypeState? TypeOf(long type) => HostObjectFor(type)?.State as TypeState;

        private string ReadWide(long address) => address == 0 ? string.Empty : _emulator.ReadUtf16String(address, 1024);

        /// <summary>Object::GetType: QI IPropertyValue -> get_Type -> the value type, else GetRuntimeClassName.</summary>
        private void GetObjectType()
        {
            long self = Arg(0);
            long callerReturn = _emulator.ReturnAddress;
            if (self == 0)
            {
                Return(0);
                return;
            }

            if (Unbox(self) is { } boxed)
            {
                (string _, int _, string name, int code) = BoxableTypes[boxed.Type];
                Return(CreateType(name, code));
                return;
            }

            long scratch = Scratch(12);
            long iid = Scratch(16);
            WriteGuid(iid, IidPropertyValue);
            CallMethod("QueryInterface(IPropertyValue)", self, 0, [iid, scratch], () =>
            {
                long value = _emulator.ReadUInt32(scratch);
                if (!IsFailure(Arg(0)) && value != 0)
                {
                    CallMethod("IPropertyValue::get_Type", value, 6, [scratch + 4], () =>
                    {
                        var type = (PropertyType)_emulator.ReadUInt32(scratch + 4);
                        CallMethod("Release", value, 2, [], () =>
                        {
                            (string Signature, int Size, string Name, int TypeCode) entry =
                                BoxableTypes.TryGetValue(type, out var e) ? e : ("", 0, "Platform.Object", 1);
                            FinishImport(callerReturn, CreateType(entry.Name, entry.TypeCode));
                        });
                    });
                    return;
                }

                CallMethod("GetRuntimeClassName", self, 4, [scratch + 8], () =>
                {
                    string name = _strings.ReadText(_emulator.ReadUInt32(scratch + 8));
                    FinishImport(callerReturn, CreateType(name, 1));
                });
            });
        }

        // -------------------------------------------------------------------------
        // Object^ to String^
        // -------------------------------------------------------------------------

        /// <summary>
        /// __abi_ObjectToString, as vccorlib110: QI IPrintable and call its ToString (when asked
        /// to); else QI IPropertyValue and format the value; else the runtime class name; else null.
        /// </summary>
        private void ObjectToString(long self, bool tryPrintable, long callerReturn)
        {
            if (self == 0)
            {
                Return(0);
                return;
            }

            if (Unbox(self) is { } boxed)
            {
                string? text = FormatBoxed(boxed);
                Return(text is null ? NewHString(HostObjectFor(self)!.Class.RuntimeClassName) : BoxedToHString(boxed));
                return;
            }

            long iid = Scratch(16);
            long result = Scratch(8);

            void Done(long value)
            {
                _emulator.FreeHeap(iid);
                _emulator.FreeHeap(result);
                FinishImport(callerReturn, value);
            }

            void ByPropertyValue()
            {
                WriteGuid(iid, IidPropertyValue);
                CallMethod("QueryInterface(IPropertyValue)", self, 0, [iid, result], () =>
                {
                    long value = _emulator.ReadUInt32(result);
                    if (IsFailure(Arg(0)) || value == 0)
                    {
                        ByClassName();
                        return;
                    }

                    CallMethod("IPropertyValue::get_Type", value, 6, [result + 4], () =>
                    {
                        var type = (PropertyType)_emulator.ReadUInt32(result + 4);
                        bool formattable = BoxableTypes.ContainsKey(type) && type is <= PropertyType.Boolean or PropertyType.String or PropertyType.Guid;
                        if (!formattable)
                        {
                            CallMethod("Release", value, 2, [], ByClassName);
                            return;
                        }

                        // GetUInt8 is slot 8, in PropertyType order up to GetString (19); GetGuid is 20.
                        int getter = type == PropertyType.Guid ? 20 : 7 + (int)type;
                        long raw = Scratch(16);
                        CallMethod($"IPropertyValue::Get{type}", value, getter, [raw], () =>
                        {
                            long hr = Arg(0);
                            byte[] bytes = _emulator.ReadMemory(raw, BoxableTypes[type].Size);
                            _emulator.FreeHeap(raw);
                            CallMethod("Release", value, 2, [], () =>
                                Done(IsFailure(hr) ? 0 : BoxedToHString(new BoxedValue(type, bytes))));
                        });
                    });
                });
            }

            void ByClassName()
            {
                WriteGuid(iid, IidInspectable);
                CallMethod("QueryInterface(IInspectable)", self, 0, [iid, result], () =>
                {
                    long inspectable = _emulator.ReadUInt32(result);
                    if (IsFailure(Arg(0)) || inspectable == 0)
                    {
                        Done(0);
                        return;
                    }

                    CallMethod("GetRuntimeClassName", inspectable, 4, [result + 4], () =>
                    {
                        long name = IsFailure(Arg(0)) ? 0 : _emulator.ReadUInt32(result + 4);
                        CallMethod("Release", inspectable, 2, [], () => Done(name));
                    });
                });
            }

            if (!tryPrintable)
            {
                ByPropertyValue();
                return;
            }

            WriteGuid(iid, IidPrintable);
            CallMethod("QueryInterface(IPrintable)", self, 0, [iid, result], () =>
            {
                long printable = _emulator.ReadUInt32(result);
                if (IsFailure(Arg(0)) || printable == 0)
                {
                    ByPropertyValue();
                    return;
                }

                CallMethod("IPrintable::__abi_ToString", printable, 6, [result + 4], () =>
                {
                    long hr = Arg(0);
                    long text = _emulator.ReadUInt32(result + 4);
                    CallMethod("Release", printable, 2, [], () =>
                    {
                        if (IsFailure(hr))
                        {
                            RaiseForHResult(hr, callerReturn, "__abi_ObjectToString");
                            return;
                        }

                        Done(text);
                    });
                });
            });
        }

        private void CastObjectToString()
        {
            bool dynamic = (Arg(0) & 0xFF) != 0;
            long self = Arg(1);
            long callerReturn = _emulator.ReturnAddress;
            if (self == 0)
            {
                Return(0);
                return;
            }

            if (HostObjectFor(self) is { } host)
            {
                if (host.State is BoxedValue { Type: PropertyType.String } boxedString)
                {
                    Return(BitConverter.ToUInt32(boxedString.Raw));
                }
                else if (dynamic)
                {
                    Return(0);
                }
                else
                {
                    RaisePlatformException(new PlatformRaise("InvalidCastException", 0x80004002), "__abi_cast_Object_to_String");
                }

                return;
            }

            long iid = Scratch(16);
            long result = Scratch(8);
            WriteGuid(iid, ReferenceIid(PropertyType.String));
            CallMethod("QueryInterface(IReference<String>)", self, 0, [iid, result], () =>
            {
                long reference = _emulator.ReadUInt32(result);
                if (IsFailure(Arg(0)) || reference == 0)
                {
                    if (dynamic)
                    {
                        FinishImport(callerReturn, 0);
                    }
                    else
                    {
                        RaiseForHResult(0x80004002, callerReturn, "__abi_cast_Object_to_String");
                    }

                    return;
                }

                CallMethod("IReference<String>::get_Value", reference, 6, [result + 4], () =>
                {
                    long hr = Arg(0);
                    long text = _emulator.ReadUInt32(result + 4);
                    CallMethod("Release", reference, 2, [], () =>
                    {
                        if (IsFailure(hr))
                        {
                            RaiseForHResult(hr, callerReturn, "__abi_cast_Object_to_String");
                            return;
                        }

                        FinishImport(callerReturn, text);
                    });
                });
            });
        }

        // -------------------------------------------------------------------------
        // Weak references
        // -------------------------------------------------------------------------

        private void GetWeakReference()
        {
            long self = Arg(0);
            long callerReturn = _emulator.ReturnAddress;
            if (self == 0)
            {
                Return(0);
                return;
            }

            long iid = Scratch(16);
            long result = Scratch(8);
            WriteGuid(iid, IidWeakReferenceSource);
            CallMethod("QueryInterface(IWeakReferenceSource)", self, 0, [iid, result], () =>
            {
                long hr = Arg(0);
                long source = _emulator.ReadUInt32(result);
                if (IsFailure(hr) || source == 0)
                {
                    RaiseForHResult(IsFailure(hr) ? hr : 0x80004002, callerReturn, "GetWeakReference");
                    return;
                }

                CallMethod("IWeakReferenceSource::GetWeakReference", source, 3, [result + 4], () =>
                {
                    long got = Arg(0);
                    long weak = _emulator.ReadUInt32(result + 4);
                    CallMethod("Release", source, 2, [], () =>
                    {
                        if (IsFailure(got))
                        {
                            RaiseForHResult(got, callerReturn, "GetWeakReference");
                            return;
                        }

                        FinishImport(callerReturn, weak);
                    });
                });
            });
        }

        // -------------------------------------------------------------------------
        // The IBox / IBoxArray vtables
        // -------------------------------------------------------------------------

        private long _boxVtable;
        private long _boxArrayVtable;

        /// <summary>
        /// vccorlib's IPropertyValue vtable for a compiler-generated box (IBox&lt;T&gt;/IBoxArray&lt;T&gt;),
        /// as vccorlib110: the box class puts it second, so IUnknown/IInspectable delegate to the
        /// object's primary vtable at this-4. A box of a value type is PropertyType OtherType, not
        /// numeric, and every GetXxx is E_NOTIMPL (the array getters also null their value
        /// out-parameter). For an array box, get_Type and the GetXxxArray getters inspect the
        /// primary's GetIids for an IReferenceArray&lt;T&gt; of a known element type.
        /// </summary>
        private long BoxVtable(bool array)
        {
            ref long cached = ref array ? ref _boxArrayVtable : ref _boxVtable;
            if (cached != 0)
            {
                return cached;
            }

            string kind = array ? "Platform::Array (IBoxArray)" : "Platform IBox";
            var slots = new List<(string Name, Action Handler)>();
            string[] inspectable = ["QueryInterface", "AddRef", "Release", "GetIids", "GetRuntimeClassName", "GetTrustLevel"];
            for (int i = 0; i < inspectable.Length; i++)
            {
                int slot = i;
                slots.Add((inspectable[i], () =>
                {
                    long primary = Arg(0) - 4;
                    _emulator.WriteRegister(Arm.UC_ARM_REG_R0, primary);
                    _emulator.ContinueAt(_emulator.ReadUInt32(_emulator.ReadUInt32(primary) + (slot * 4)));
                }));
            }

            slots.Add(("get_Type", array
                ? () => BoxArrayType(Arg(0) - 4, Arg(1), _emulator.ReturnAddress)
                : () => { WriteOut(Arg(1), (long)PropertyType.OtherType); Return(SOk); }));
            slots.Add(("get_IsNumericScalar", () => { WriteBool(Arg(1), false); Return(SOk); }));

            string[] scalars =
            [
                "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Char16",
                "Boolean", "String", "Guid", "DateTime", "TimeSpan", "Point", "Size", "Rect",
            ];
            foreach (string scalar in scalars)
            {
                slots.Add(($"Get{scalar}", () => Return(0x80004001)));
            }

            PropertyType[] arrays =
            [
                PropertyType.UInt8, PropertyType.Int16, PropertyType.UInt16, PropertyType.Int32, PropertyType.UInt32,
                PropertyType.Int64, PropertyType.UInt64, PropertyType.Single, PropertyType.Double, PropertyType.Char16,
                PropertyType.Boolean, PropertyType.String, PropertyType.Inspectable, PropertyType.Guid, PropertyType.DateTime,
                PropertyType.TimeSpan, PropertyType.Point, PropertyType.Size, PropertyType.Rect,
            ];
            foreach (PropertyType element in arrays)
            {
                slots.Add(($"Get{element}Array", array
                    ? () => BoxArrayGet(Arg(0) - 4, element, Arg(1), Arg(2), _emulator.ReturnAddress)
                    : () => { WriteOut(Arg(2), 0); Return(0x80004001); }));
            }

            long vtable = _emulator.AllocateHeap(slots.Count * 4);
            for (int i = 0; i < slots.Count; i++)
            {
                long trap = _emulator.RegisterVtableMethod($"{kind}::{slots[i].Name}", slots[i].Handler);
                _emulator.WriteUInt32(vtable + (i * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            cached = vtable;
            return vtable;
        }

        /// <summary>Element types an IBoxArray can name in its GetIids, with their IReferenceArray IIDs.</summary>
        private Dictionary<Guid, PropertyType> ReferenceArrayIids => _referenceArrayIids ??=
            BoxableTypes.Keys.ToDictionary(ReferenceArrayIid, type => type);

        private Dictionary<Guid, PropertyType>? _referenceArrayIids;

        /// <summary>Calls the primary's GetIids and hands back the first known IReferenceArray element type.</summary>
        private void FindArrayElementType(long primary, Action<PropertyType?> then)
        {
            long scratch = Scratch(8);
            CallMethod("IBoxArray primary::GetIids", primary, 3, [scratch, scratch + 4], () =>
            {
                PropertyType? found = null;
                long count = _emulator.ReadUInt32(scratch);
                long iids = _emulator.ReadUInt32(scratch + 4);
                if (!IsFailure(Arg(0)) && iids != 0)
                {
                    for (long i = 0; i < Math.Min(count, 256) && found is null; i++)
                    {
                        if (ReferenceArrayIids.TryGetValue(_emulator.ReadGuid(iids + (i * 16)), out PropertyType element))
                        {
                            found = element;
                        }
                    }
                }

                _emulator.FreeHeap(scratch);
                then(found);
            });
        }

        /// <summary>
        /// IBoxArray get_Type: the array PropertyType of the element the box names (1024 + element),
        /// or OtherTypeArray - vccorlib110's answer when no IReferenceArray&lt;T&gt; it knows is there.
        /// </summary>
        private void BoxArrayType(long primary, long outType, long callerReturn) =>
            FindArrayElementType(primary, element =>
            {
                WriteOut(outType, element is { } known ? 1024 + (long)known : (long)PropertyType.OtherTypeArray);
                FinishImport(callerReturn, SOk);
            });

        /// <summary>
        /// IBoxArray GetXxxArray: IReferenceArray&lt;T&gt;::get_Value of the primary when the box holds
        /// that element type; E_NOTIMPL with the outs nulled when it holds none vccorlib knows;
        /// TYPE_E_TYPEMISMATCH for a different one (arrays do not convert).
        /// </summary>
        private void BoxArrayGet(long primary, PropertyType wanted, long outLength, long outValue, long callerReturn) =>
            FindArrayElementType(primary, element =>
            {
                if (element != wanted)
                {
                    WriteOut(outLength, 0);
                    WriteOut(outValue, 0);
                    FinishImport(callerReturn, element is null ? 0x80004001 : TypeMismatch);
                    return;
                }

                long scratch = Scratch(24);
                WriteGuid(scratch + 8, ReferenceArrayIid(wanted));
                CallMethod("QueryInterface(IReferenceArray)", primary, 0, [scratch + 8, scratch], () =>
                {
                    long reference = _emulator.ReadUInt32(scratch);
                    if (IsFailure(Arg(0)) || reference == 0)
                    {
                        _emulator.FreeHeap(scratch);
                        WriteOut(outLength, 0);
                        WriteOut(outValue, 0);
                        FinishImport(callerReturn, 0x80004001);
                        return;
                    }

                    CallMethod("IReferenceArray::get_Value", reference, 6, [outLength, outValue], () =>
                    {
                        long hr = Arg(0);
                        CallMethod("Release", reference, 2, [], () =>
                        {
                            _emulator.FreeHeap(scratch);
                            FinishImport(callerReturn, hr);
                        });
                    });
                });
            });

        // -------------------------------------------------------------------------
        // The object context
        // -------------------------------------------------------------------------

        internal static readonly Guid IidContextCallback = new("000001da-0000-0000-c000-000000000046");

        private HostObject? _objectContext;

        /// <summary>
        /// The one object context: IContextCallback::ContextCallback(fn, data, riid, method, unk)
        /// runs fn(data) right here and returns its HRESULT (a tail call).
        /// </summary>
        private HostObject ObjectContext() => _objectContext ??= CreateHostObject(new HostClass(
            "ObjectContext",
            [
                new HostInterface("IContextCallback", [IidContextCallback], false,
                    ("ContextCallback", _ =>
                    {
                        long function = Arg(1);
                        if (function == 0)
                        {
                            Return(EInvalidArg);
                            return;
                        }

                        _emulator.WriteRegister(Arm.UC_ARM_REG_R0, Arg(2));
                        _emulator.ContinueAt(function);
                    })),
            ]));
    }
}
