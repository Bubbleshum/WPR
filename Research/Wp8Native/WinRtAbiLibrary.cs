using System.Globalization;
using System.Text;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The WinRT and COM ABI API sets: api-ms-win-core-winrt (activation), -winrt-error (restricted
    /// error info), -winrt-string (HSTRING), -winrt-robuffer, and the api-ms-win-core-com functions
    /// that are not apartment plumbing (streams, GUID strings, task memory).
    /// </summary>
    public sealed partial class HostStubs
    {
        private const long StgInvalidFunction = unchecked((int)0x80030001);   // STG_E_INVALIDFUNCTION
        private const long ClassStringInvalid = unchecked((int)0x800401F3);   // CO_E_CLASSSTRING

        internal static readonly Guid IidRestrictedErrorInfo = new("82ba7092-4c88-427d-a7bc-16dd93feb67e");
        internal static readonly Guid IidSequentialStream = new("0c733a30-2a1c-11ce-ade5-00aa0044773d");
        internal static readonly Guid IidStream = new("0000000c-0000-0000-c000-000000000046");

        private void RegisterWinRtAbi()
        {
            RegisterActivation();
            RegisterErrorInfo();
            RegisterStringFunctions();
            RegisterComFunctions();
        }

        // -------------------------------------------------------------------------
        // api-ms-win-core-winrt-l1-1-0
        // -------------------------------------------------------------------------

        private void RegisterActivation()
        {
            // HRESULT RoInitialize(RO_INIT_TYPE) / void RoUninitialize(): the same per-thread count as
            // CoInitializeEx - S_OK the first time, S_FALSE nested.
            Add("RoInitialize", () => Return(InitialiseApartment()));
            Add("RoUninitialize", () =>
            {
                UninitialiseApartment();
                Return(0);
            });

            // HRESULT RoGetActivationFactory(HSTRING class, REFIID, void** factory): the same factory
            // GetActivationFactoryByPCWSTR answers for the class.
            Add("RoGetActivationFactory", () =>
            {
                long outFactory = Arg(2);
                if (outFactory == 0)
                {
                    Return(EPointer);
                    return;
                }

                string className = _strings.ReadText(Arg(0));
                long? factory = className.Length == 0 ? null : _winRt.GetActivationFactory(className);
                if (factory is null)
                {
                    RequestedClasses.Add($"{className} -> REGDB_E_CLASSNOTREG (RoGetActivationFactory)");
                    WriteOut(outFactory, 0);
                    Return(WinRtRuntime.ClassNotRegistered);
                    return;
                }

                RequestedClasses.Add($"{className} -> factory at 0x{factory.Value:X8} (RoGetActivationFactory)");
                WriteOut(outFactory, factory.Value);
                Return(SOk);
            });

            // HRESULT RoActivateInstance(HSTRING class, IInspectable** instance): the class's factory,
            // then IActivationFactory::ActivateInstance (slot 6), tail-called so its HRESULT and its
            // out-parameter are the answer.
            Add("RoActivateInstance", () =>
            {
                long outInstance = Arg(1);
                if (outInstance == 0)
                {
                    Return(EPointer);
                    return;
                }

                string className = _strings.ReadText(Arg(0));
                long? factory = className.Length == 0 ? null : _winRt.GetActivationFactory(className);
                if (factory is null)
                {
                    RequestedClasses.Add($"{className} -> REGDB_E_CLASSNOTREG (RoActivateInstance)");
                    WriteOut(outInstance, 0);
                    Return(WinRtRuntime.ClassNotRegistered);
                    return;
                }

                RequestedClasses.Add($"{className} -> ActivateInstance on 0x{factory.Value:X8}");
                _emulator.WriteRegister(Arm.UC_ARM_REG_R0, factory.Value);
                _emulator.WriteRegister(Arm.UC_ARM_REG_R1, outInstance);
                _emulator.ContinueAt(_emulator.ReadUInt32(_emulator.ReadUInt32(factory.Value) + (6 * 4)));
            });
        }

        // -------------------------------------------------------------------------
        // api-ms-win-core-winrt-error
        // -------------------------------------------------------------------------

        private sealed record ErrorInfoState(long HResult, string? Description);

        /// <summary>Each thread's current restricted error info (an IRestrictedErrorInfo*).</summary>
        private readonly Dictionary<int, long> _restrictedErrors = new();

        private HostClass? _errorInfoClass;

        /// <summary>
        /// IRestrictedErrorInfo: GetErrorDetails(BSTR* description, HRESULT* error, BSTR* restricted,
        /// BSTR* capabilitySid) and GetReference(BSTR*). The description is the system text for the
        /// HRESULT, the restricted description the message the error was originated with.
        /// </summary>
        private HostClass ErrorInfoClass => _errorInfoClass ??= new HostClass(
            "RestrictedErrorInfo",
            [
                new HostInterface("IRestrictedErrorInfo", [IidRestrictedErrorInfo], false,
                    ("GetErrorDetails", o =>
                    {
                        var state = (ErrorInfoState)o.State!;
                        WriteOut(Arg(1), AllocateBstr(SystemMessages.GetValueOrDefault((uint)state.HResult)));
                        WriteOut(Arg(2), state.HResult);
                        WriteOut(Arg(3), AllocateBstr(state.Description));
                        WriteOut(Arg(4), 0);
                        Return(SOk);
                    }),
                    ("GetReference", _ =>
                    {
                        WriteOut(Arg(1), 0);
                        Return(SOk);
                    })),
            ]);

        /// <summary>Makes a new error info current on this thread (RoOriginateError's job).</summary>
        private void OriginateError(long hresult, string? message)
        {
            HostObject info = CreateHostObject(ErrorInfoClass, 4, new ErrorInfoState(hresult & 0xFFFFFFFFL, message));
            SetErrorInfo(info.Address);
            info.References--;   // the thread's reference is the only one
        }

        private void SetErrorInfo(long info)
        {
            int thread = _emulator.CurrentThreadId;
            if (_restrictedErrors.Remove(thread, out long previous) && HostObjectAt(previous) is { } old)
            {
                old.References--;
            }

            if (info != 0)
            {
                _restrictedErrors[thread] = info;
                if (HostObjectAt(info) is { } kept)
                {
                    kept.References++;
                }
            }
        }

        private void RegisterErrorInfo()
        {
            // HRESULT SetRestrictedErrorInfo(IRestrictedErrorInfo*): replaces this thread's; null clears.
            // The thread holds a reference (taken on host objects; a guest object's own AddRef is not
            // called, so a guest error info must outlive its use - none is known to be made).
            Add("SetRestrictedErrorInfo", () =>
            {
                SetErrorInfo(Arg(0));
                Return(SOk);
            });

            // HRESULT GetRestrictedErrorInfo(IRestrictedErrorInfo**): hands over this thread's and
            // clears it; S_FALSE with null when there is none.
            Add("GetRestrictedErrorInfo", () =>
            {
                if (Arg(0) == 0)
                {
                    Return(EPointer);
                    return;
                }

                bool had = _restrictedErrors.Remove(_emulator.CurrentThreadId, out long info);
                WriteOut(Arg(0), had ? info : 0);
                Return(had ? SOk : SFalse);
            });

            // BOOL RoOriginateError(HRESULT, HSTRING message) / RoOriginateErrorW(HRESULT, UINT cch,
            // PCWSTR): records the error with its message as this thread's; FALSE for a success code.
            Add("RoOriginateError", () => Return(Originate(Arg(0), Arg(1) == 0 ? null : _strings.ReadText(Arg(1)))));
            Add("RoOriginateErrorW", () => Return(Originate(Arg(0), ReadCountedWide(Arg(2), Arg(1)))));

            // BOOL RoTransformError(HRESULT old, HRESULT new, HSTRING message) / RoTransformErrorW(...,
            // UINT cch, PCWSTR): reports the new error, unless neither is a failure.
            Add("RoTransformError", () => Return(Transform(Arg(0), Arg(1), Arg(2) == 0 ? null : _strings.ReadText(Arg(2)))));
            Add("RoTransformErrorW", () => Return(Transform(Arg(0), Arg(1), ReadCountedWide(Arg(3), Arg(2)))));

            // HRESULT RoCaptureErrorContext(HRESULT): captures a stack for the current error info;
            // there is no debugger to give it to, so this succeeds having recorded nothing more.
            Add("RoCaptureErrorContext", () => Return(SOk));

            // void RoFailFastWithErrorContext(HRESULT): ends the process. It is the game failing.
            Add("RoFailFastWithErrorContext", () =>
            {
                Return(0);
                _emulator.Stop($"the image called RoFailFastWithErrorContext(0x{Arg(0) & 0xFFFFFFFFL:X8}) from 0x{_emulator.ReturnAddress & ~1L:X8}");
            });

            // HRESULT RoReportUnhandledError(IRestrictedErrorInfo*): raises CoreApplication's
            // UnhandledErrorDetected, which terminates the app unless a handler marks it handled -
            // and nothing here subscribes one. The game has failed.
            Add("RoReportUnhandledError", () =>
            {
                long hr = HostObjectAt(Arg(0))?.State is ErrorInfoState info ? info.HResult : 0;
                Return(SOk);
                _emulator.Stop($"the image reported an unhandled error (RoReportUnhandledError, hr=0x{hr:X8}) from 0x{_emulator.ReturnAddress & ~1L:X8}");
            });
        }

        private long Originate(long hresult, string? message)
        {
            if (!IsFailure(hresult))
            {
                return 0;
            }

            OriginateError(hresult, message);
            return 1;
        }

        private long Transform(long oldError, long newError, string? message)
        {
            if (!IsFailure(oldError) && !IsFailure(newError))
            {
                return 0;
            }

            OriginateError(IsFailure(newError) ? newError : oldError, message);
            return 1;
        }

        private string? ReadCountedWide(long text, long count)
        {
            if (text == 0)
            {
                return null;
            }

            string read = _emulator.ReadUtf16String(text, count == 0 ? 4096 : (int)Math.Min(count, 4096));
            return read;
        }

        // -------------------------------------------------------------------------
        // api-ms-win-core-winrt-string-l1-1-0
        // -------------------------------------------------------------------------

        /// <summary>Preallocated string buffers, by handle: the HSTRING each will be promoted to, and its length.</summary>
        private readonly Dictionary<long, int> _stringBuffers = new();

        private void RegisterStringFunctions()
        {
            // BOOL WindowsIsStringEmpty(HSTRING): null and zero-length are both empty.
            Add("WindowsIsStringEmpty", () => Return(_strings.LengthOf(Arg(0)) == 0 ? 1 : 0));

            // HRESULT WindowsStringHasEmbeddedNull(HSTRING, BOOL*).
            Add("WindowsStringHasEmbeddedNull", () =>
            {
                if (Arg(1) == 0)
                {
                    Return(EInvalidArg);
                    return;
                }

                WriteOut(Arg(1), _strings.ReadText(Arg(0)).Contains('\0', StringComparison.Ordinal) ? 1 : 0);
                Return(SOk);
            });

            // HRESULT WindowsSubstring(HSTRING, UINT32 start, HSTRING*) and
            // WindowsSubstringWithSpecifiedLength(HSTRING, UINT32 start, UINT32 length, HSTRING*):
            // E_BOUNDS past the end; a zero-length result is the null string.
            Add("WindowsSubstring", () =>
            {
                string text = _strings.ReadText(Arg(0));
                long start = Arg(1);
                StringResult(Arg(2), start > text.Length ? null : text[(int)start..]);
            });
            Add("WindowsSubstringWithSpecifiedLength", () =>
            {
                string text = _strings.ReadText(Arg(0));
                long start = Arg(1), length = Arg(2);
                StringResult(Arg(3), start + length > text.Length ? null : text.Substring((int)start, (int)length));
            });

            // HRESULT WindowsTrimStringStart/End(HSTRING, HSTRING trimChars, HSTRING*): strips any of
            // trimChars from that end; E_INVALIDARG for an empty trim set.
            Add("WindowsTrimStringStart", () => Trim(start: true));
            Add("WindowsTrimStringEnd", () => Trim(start: false));

            // HRESULT WindowsReplaceString(HSTRING, HSTRING find, HSTRING replace, HSTRING*): every
            // non-overlapping occurrence, left to right; E_INVALIDARG for an empty find.
            Add("WindowsReplaceString", () =>
            {
                string find = _strings.ReadText(Arg(1));
                if (Arg(3) == 0 || find.Length == 0)
                {
                    WriteOut(Arg(3), 0);
                    Return(EInvalidArg);
                    return;
                }

                string text = _strings.ReadText(Arg(0));
                StringResult(Arg(3), text.Replace(find, _strings.ReadText(Arg(2)), StringComparison.Ordinal));
            });

            // HRESULT WindowsPreallocateStringBuffer(UINT32 length, WCHAR** buffer, HSTRING_BUFFER*):
            // a writable buffer of length characters plus the terminator. The buffer handle is the
            // HSTRING it will become, so promoting it is a check, not a copy.
            Add("WindowsPreallocateStringBuffer", () =>
            {
                long length = Arg(0);
                if (Arg(1) == 0 || Arg(2) == 0)
                {
                    Return(EPointer);
                    return;
                }

                if (length > HStringHeap.SaneLengthLimit / 2)
                {
                    WriteOut(Arg(1), 0);
                    WriteOut(Arg(2), 0);
                    Return(EOutOfMemory);
                    return;
                }

                long handle = _strings.Create(new byte[length * 2]);
                _stringBuffers[handle] = (int)length;
                WriteOut(Arg(1), _strings.BufferOf(handle));
                WriteOut(Arg(2), handle);
                Return(SOk);
            });

            // HRESULT WindowsPromoteStringBuffer(HSTRING_BUFFER, HSTRING*): the buffer must still be
            // terminated where it was allocated to end.
            Add("WindowsPromoteStringBuffer", () =>
            {
                long handle = Arg(0);
                if (Arg(1) == 0)
                {
                    Return(EPointer);
                    return;
                }

                if (!_stringBuffers.TryGetValue(handle, out int length) ||
                    BitConverter.ToUInt16(_emulator.ReadMemory(_strings.BufferOf(handle) + (length * 2), 2)) != 0)
                {
                    WriteOut(Arg(1), 0);
                    Return(EInvalidArg);
                    return;
                }

                _stringBuffers.Remove(handle);
                WriteOut(Arg(1), length == 0 ? 0 : handle);
                Return(SOk);
            });

            // HRESULT WindowsDeleteStringBuffer(HSTRING_BUFFER): discards one never promoted.
            Add("WindowsDeleteStringBuffer", () => Return(Arg(0) == 0 || _stringBuffers.Remove(Arg(0)) ? SOk : EInvalidArg));
        }

        /// <summary>Writes a string result (a null text means E_BOUNDS), reporting E_INVALIDARG for a null out.</summary>
        private void StringResult(long destination, string? text)
        {
            if (destination == 0)
            {
                Return(EInvalidArg);
                return;
            }

            if (text is null)
            {
                WriteOut(destination, 0);
                Return(EBounds);
                return;
            }

            WriteOut(destination, text.Length == 0 ? 0 : _strings.Create(text));
            Return(SOk);
        }

        private void Trim(bool start)
        {
            string trim = _strings.ReadText(Arg(1));
            if (Arg(2) == 0 || trim.Length == 0)
            {
                WriteOut(Arg(2), 0);
                Return(EInvalidArg);
                return;
            }

            string text = _strings.ReadText(Arg(0));
            char[] set = trim.ToCharArray();
            StringResult(Arg(2), start ? text.TrimStart(set) : text.TrimEnd(set));
        }

        // -------------------------------------------------------------------------
        // api-ms-win-core-com and -winrt-robuffer
        // -------------------------------------------------------------------------

        private void RegisterComFunctions()
        {
            // void CoUninitialize(): undoes one CoInitializeEx on this thread.
            Add("CoUninitialize", () =>
            {
                UninitialiseApartment();
                Return(0);
            });

            // void* CoTaskMemRealloc(void*, SIZE_T): realloc's contract on the task allocator, which
            // here is the heap.
            Add("CoTaskMemRealloc", () => _handlers["realloc"]());

            // int StringFromGUID2(REFGUID, LPOLESTR, int cchMax): "{XXXXXXXX-...}" in upper case with
            // its terminator; the characters written including it, or 0 if they do not fit.
            Add("StringFromGUID2", () =>
            {
                if (Arg(0) == 0 || Arg(1) == 0)
                {
                    Return(0);
                    return;
                }

                string text = "{" + _emulator.ReadGuid(Arg(0)).ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}";
                if (_frame.SignedArg(2) < text.Length + 1)
                {
                    Return(0);
                    return;
                }

                _emulator.WriteMemory(Arg(1), Encoding.Unicode.GetBytes(text + "\0"));
                Return(text.Length + 1);
            });

            // HRESULT CLSIDFromString(LPCOLESTR, LPCLSID) / IIDFromString(LPCOLESTR, LPIID): the braced
            // form. CLSIDFromString takes a null string as CLSID_NULL; a ProgID would need a registry,
            // and a phone app has none, so anything not a GUID is CO_E_CLASSSTRING (IIDFromString:
            // E_INVALIDARG).
            Add("CLSIDFromString", () => ParseGuidString(ClassStringInvalid, nullIsEmpty: true));
            Add("IIDFromString", () => ParseGuidString(EInvalidArg, nullIsEmpty: true));

            // HRESULT CreateStreamOnHGlobal(HGLOBAL, BOOL deleteOnRelease, IStream**): see StreamClass.
            Add("CreateStreamOnHGlobal", () =>
            {
                if (Arg(2) == 0)
                {
                    Return(EInvalidArg);
                    return;
                }

                long memory = Arg(0);
                var block = new HGlobalBlock
                {
                    Address = memory,
                    Size = memory == 0 ? 0 : _emulator.AllocationSizeOf(memory),
                    Owned = Arg(1) != 0,
                };
                block.Capacity = block.Size;
                WriteOut(Arg(2), CreateStream(block, 0));
                Return(SOk);
            });

            // HRESULT CoUnmarshalInterface(IStream*, REFIID, void**): nothing here ever marshals an
            // interface into a stream - CoMarshalInterThreadInterfaceInStream refuses - so no stream
            // can hold one to read back. Refused the same way, with the out-parameter nulled.
            Add("CoUnmarshalInterface", () => FailWithNull(Arg(2), NotImplemented));

            // HRESULT RoGetBufferMarshaler(IMarshal**): the marshaler for IBuffer across apartments.
            // There is one apartment and no marshalling, so there is no marshaler to give.
            Add("RoGetBufferMarshaler", () => FailWithNull(Arg(0), NotImplemented));
        }

        private void ParseGuidString(long invalid, bool nullIsEmpty)
        {
            long text = Arg(0), destination = Arg(1);
            if (destination == 0)
            {
                Return(EInvalidArg);
                return;
            }

            if (text == 0 && nullIsEmpty)
            {
                WriteGuid(destination, Guid.Empty);
                Return(SOk);
                return;
            }

            string value = text == 0 ? string.Empty : _emulator.ReadUtf16String(text, 64);
            if (value.Length == 38 && value[0] == '{' && value[37] == '}' &&
                Guid.TryParseExact(value, "B", out Guid parsed))
            {
                WriteGuid(destination, parsed);
                Return(SOk);
                return;
            }

            Return(invalid);
        }

        // -------------------------------------------------------------------------
        // IStream over guest memory
        // -------------------------------------------------------------------------

        /// <summary>The memory an HGLOBAL stream reads and writes, shared by a stream and its clones.</summary>
        internal sealed class HGlobalBlock
        {
            public long Address { get; set; }

            /// <summary>The stream's logical size.</summary>
            public long Size { get; set; }

            /// <summary>Bytes allocated at <see cref="Address"/>.</summary>
            public long Capacity { get; set; }

            /// <summary>Freed when the last stream on it is released (CreateStreamOnHGlobal's fDeleteOnRelease).</summary>
            public bool Owned { get; init; }

            public int Streams { get; set; }
        }

        internal sealed class StreamState(HGlobalBlock block, long position)
        {
            public HGlobalBlock Block { get; } = block;

            public long Position { get; set; } = position;
        }

        private HostClass? _streamClass;

        private long CreateStream(HGlobalBlock block, long position)
        {
            block.Streams++;
            return CreateHostObject(StreamClass, 8, new StreamState(block, position)).Address;
        }

        /// <summary>
        /// The IStream CreateStreamOnHGlobal makes. A NULL HGLOBAL starts an empty block; a given one
        /// starts at the size of its allocation (GlobalSize). Writing past the capacity moves the data
        /// to a larger block, as GlobalReAlloc may; seeking past the end is allowed and the gap is
        /// zero when written over. No transactions (Commit and Revert succeed and do nothing) and no
        /// region locking (STG_E_INVALIDFUNCTION), as for the real HGLOBAL stream.
        /// </summary>
        private HostClass StreamClass => _streamClass ??= new HostClass(
            "HGlobalStream",
            [
                new HostInterface("IStream", [IidSequentialStream, IidStream], false,
                    ("Read", o => StreamRead((StreamState)o.State!, Arg(1), Arg(2), Arg(3))),
                    ("Write", o => StreamWrite((StreamState)o.State!, Arg(1), Arg(2), Arg(3))),
                    ("Seek", o => StreamSeek((StreamState)o.State!)),
                    ("SetSize", o =>
                    {
                        var state = (StreamState)o.State!;
                        long size = Arg(2) | (Arg(3) << 32);
                        if (size < 0 || size > CallFrame.SaneLengthLimit || !EnsureCapacity(state.Block, size))
                        {
                            Return(EOutOfMemory);
                            return;
                        }

                        if (size > state.Block.Size)
                        {
                            _emulator.WriteMemory(state.Block.Address + state.Block.Size, new byte[size - state.Block.Size]);
                        }

                        state.Block.Size = size;
                        Return(SOk);
                    }),
                    ("CopyTo", StreamCopyTo),
                    ("Commit", _ => Return(SOk)),
                    ("Revert", _ => Return(SOk)),
                    ("LockRegion", _ => Return(StgInvalidFunction)),
                    ("UnlockRegion", _ => Return(StgInvalidFunction)),
                    ("Stat", o =>
                    {
                        // STATSTG on ARM: pwcsName, type, cbSize (8-aligned, at 8), three FILETIMEs,
                        // grfMode at 40, grfLocksSupported, clsid at 48, grfStateBits, reserved: 72 bytes.
                        long stat = Arg(1);
                        if (stat == 0)
                        {
                            Return(EInvalidArg);
                            return;
                        }

                        _emulator.WriteMemory(stat, new byte[72]);
                        _emulator.WriteUInt32(stat + 4, 2);   // STGTY_STREAM
                        _emulator.WriteUInt64(stat + 8, (ulong)((StreamState)o.State!).Block.Size);
                        _emulator.WriteUInt32(stat + 40, 2);  // STGM_READWRITE
                        Return(SOk);
                    }),
                    ("Clone", o =>
                    {
                        if (Arg(1) == 0)
                        {
                            Return(EInvalidArg);
                            return;
                        }

                        var state = (StreamState)o.State!;
                        WriteOut(Arg(1), CreateStream(state.Block, state.Position));
                        Return(SOk);
                    })),
            ])
        {
            OnFinalRelease = o =>
            {
                HGlobalBlock block = ((StreamState)o.State!).Block;
                if (--block.Streams == 0 && block.Owned && block.Address != 0)
                {
                    _emulator.FreeHeap(block.Address);
                    block.Address = 0;
                }
            },
        };

        private bool EnsureCapacity(HGlobalBlock block, long needed)
        {
            if (needed <= block.Capacity && block.Address != 0)
            {
                return true;
            }

            long capacity = Math.Max(needed, Math.Max(block.Capacity * 2, 256));
            long moved = _emulator.AllocateHeap(capacity);
            if (moved == 0)
            {
                return false;
            }

            _emulator.WriteMemory(moved, new byte[capacity]);
            if (block.Address != 0 && block.Size > 0)
            {
                _emulator.WriteMemory(moved, _emulator.ReadMemory(block.Address, (int)block.Size));
            }

            if (block.Address != 0 && block.Owned)
            {
                _emulator.FreeHeap(block.Address);
            }

            block.Address = moved;
            block.Capacity = capacity;
            return true;
        }

        /// <summary>HRESULT Read(void* pv, ULONG cb, ULONG* pcbRead): as much as is left; S_OK even if short.</summary>
        private void StreamRead(StreamState state, long buffer, long count, long outRead)
        {
            long available = Math.Max(0, state.Block.Size - state.Position);
            long n = Math.Min(count, available);
            if (n > 0)
            {
                if (buffer == 0)
                {
                    Return(EInvalidArg);
                    return;
                }

                _emulator.WriteMemory(buffer, _emulator.ReadMemory(state.Block.Address + state.Position, (int)n));
                state.Position += n;
            }

            WriteOut(outRead, n);
            Return(SOk);
        }

        /// <summary>HRESULT Write(const void* pv, ULONG cb, ULONG* pcbWritten): grows the stream as needed.</summary>
        private void StreamWrite(StreamState state, long buffer, long count, long outWritten)
        {
            if (count > 0)
            {
                long end = state.Position + count;
                if (buffer == 0)
                {
                    Return(EInvalidArg);
                    return;
                }

                if (end > CallFrame.SaneLengthLimit || !EnsureCapacity(state.Block, end))
                {
                    WriteOut(outWritten, 0);
                    Return(EOutOfMemory);
                    return;
                }

                if (state.Position > state.Block.Size)
                {
                    _emulator.WriteMemory(state.Block.Address + state.Block.Size, new byte[state.Position - state.Block.Size]);
                }

                _emulator.WriteMemory(state.Block.Address + state.Position, _emulator.ReadMemory(buffer, (int)count));
                state.Position = end;
                state.Block.Size = Math.Max(state.Block.Size, end);
            }

            WriteOut(outWritten, count);
            Return(SOk);
        }

        /// <summary>
        /// HRESULT Seek(LARGE_INTEGER move, DWORD origin, ULARGE_INTEGER* newPosition): the 64-bit move
        /// is in r2:r3 (an even pair after this), origin and the out-pointer on the stack.
        /// </summary>
        private void StreamSeek(StreamState state)
        {
            long move = Arg(2) | (Arg(3) << 32);
            long origin = Arg(4);
            long outPosition = Arg(5);
            long basis = origin switch
            {
                0 => 0,
                1 => state.Position,
                2 => state.Block.Size,
                _ => -1,
            };

            long target = basis + move;
            if (basis < 0 || target < 0)
            {
                Return(StgInvalidFunction);
                return;
            }

            state.Position = target;
            if (outPosition != 0)
            {
                _emulator.WriteUInt64(outPosition, (ulong)target);
            }

            Return(SOk);
        }

        /// <summary>
        /// HRESULT CopyTo(IStream* dest, ULARGE_INTEGER cb, ULARGE_INTEGER* read, ULARGE_INTEGER* written):
        /// cb in r2:r3, the two out-pointers on the stack. A host destination is written directly; any
        /// other through its own Write.
        /// </summary>
        private void StreamCopyTo(HostObject source)
        {
            var state = (StreamState)source.State!;
            long destination = Arg(1);
            long wanted = Arg(2) | (Arg(3) << 32);
            long outRead = Arg(4), outWritten = Arg(5);
            long callerReturn = _emulator.ReturnAddress;
            if (destination == 0)
            {
                Return(EInvalidArg);
                return;
            }

            long n = Math.Clamp(Math.Min(wanted, state.Block.Size - state.Position), 0, CallFrame.SaneLengthLimit);
            long from = state.Block.Address + state.Position;
            state.Position += n;
            if (outRead != 0)
            {
                _emulator.WriteUInt64(outRead, (ulong)n);
            }

            if (HostObjectFor(destination)?.State is StreamState target)
            {
                long copy = n == 0 ? 0 : _emulator.AllocateHeap(n);
                if (n > 0)
                {
                    _emulator.WriteMemory(copy, _emulator.ReadMemory(from, (int)n));
                }

                StreamWrite(target, copy, n, 0);
                if (copy != 0)
                {
                    _emulator.FreeHeap(copy);
                }

                if (outWritten != 0)
                {
                    _emulator.WriteUInt64(outWritten, IsFailure(Arg(0)) ? 0 : (ulong)n);
                }

                return;
            }

            long staged = _emulator.AllocateHeap(Math.Max(n, 1) + 4);
            if (n > 0)
            {
                _emulator.WriteMemory(staged, _emulator.ReadMemory(from, (int)n));
            }

            _emulator.WriteUInt32(staged + n, 0);
            CallMethod("ISequentialStream::Write", destination, 4, [staged, n, staged + n], () =>
            {
                long hr = Arg(0);
                if (outWritten != 0)
                {
                    _emulator.WriteUInt64(outWritten, _emulator.ReadUInt32(staged + n));
                }

                _emulator.FreeHeap(staged);
                FinishImport(callerReturn, hr);
            });
        }
    }
}
