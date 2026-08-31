using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Host-side implementations of the functions the emulated image imports.
    /// </summary>
    /// <remarks>
    /// This is the seam that decides how far the image gets. Every trapped import lands
    /// in <see cref="Dispatch"/>; anything without a handler simply returns zero, which
    /// is enough to keep the CPU moving but not enough to be correct. The handlers below
    /// cover exactly the calls the WP8 CRT startup path makes before it asks for its first
    /// WinRT activation factory - the point at which a real backend would take over.
    ///
    /// The ARM procedure call standard puts the first four arguments in r0-r3 and the
    /// return value in r0, with 64-bit values returned in the r0:r1 pair.
    /// </remarks>
    public sealed class HostStubs
    {
        private readonly ArmEmulator _emulator;
        private readonly WinRtRuntime _winRt;
        private readonly HStringHeap _strings;
        private readonly CallFrame _frame;
        private readonly CrtLibrary _crt;
        private readonly Dictionary<string, Action> _handlers;

        /// <summary>Stands in for a monotonic clock. Advanced by every timing call.</summary>
        private long _tick = 0x1234;

        /// <summary>Ticks per second reported to the image: one microsecond of resolution.</summary>
        private const long PerformanceFrequency = 1_000_000;

        public HostStubs(ArmEmulator emulator, WinRtRuntime winRt, HStringHeap strings, string imageDirectory)
        {
            _emulator = emulator;
            _winRt = winRt;
            _strings = strings;
            _frame = new CallFrame(emulator);
            Direct3D = new Direct3DRuntime(emulator, _frame);
            XAudio2 = new XAudio2Runtime(emulator, _frame);

            // Assets are read from the unpacked package; anything written goes to a
            // sandbox standing in for the app's local folder.
            Files = new FileLibrary(
                emulator,
                _frame,
                readRoot: imageDirectory,
                writeRoot: Path.Combine(Path.GetTempPath(), "wpr-wp8-sandbox"));

            _handlers = new Dictionary<string, Action>(StringComparer.Ordinal)
            {
                // --- timing: __security_init_cookie mixes these into the stack guard ---
                ["GetSystemTimeAsFileTime"] = () =>
                {
                    // Writes a FILETIME through its only argument; the value is never checked.
                    WriteInt64(Arg(0), 130000000000000000L + _tick);
                    _tick += 1000;
                    Return(0);
                },
                ["QueryPerformanceCounter"] = QueryPerformance,

                // A frequency, not a counter - these were sharing a handler, so the image
                // was being told its clock ticked at whatever the current time was.
                ["QueryPerformanceFrequency"] = () =>
                {
                    WriteInt64(Arg(0), PerformanceFrequency);
                    Return(1);
                },
                ["GetTickCount64"] = () => Return64(_tick++),
                ["GetTickCount"] = () => Return(_tick++),

                // --- identity ---
                ["GetCurrentThreadId"] = () => Return(0x1000),
                ["GetCurrentProcessId"] = () => Return(0x2000),

                // --- allocation ---
                ["malloc"] = Allocate,
                // calloc is the one member of this family that does NOT take a single size.
                // Aliasing it to Allocate read nmemb as the size, so calloc(1, 0x724) - a
                // 1,828-byte object - came back as the allocator's sixteen-byte minimum, and
                // every field past the first four words of that object lived in whatever was
                // allocated next. The image read its texture decoder's plane pointers from
                // 1,700 bytes into a sixteen-byte block.
                ["calloc"] = AllocateZeroed,
                ["_calloc_crt"] = AllocateZeroed,
                ["??2@YAPAXI@Z"] = Allocate,                                // operator new(unsigned int)
                ["??_U@YAPAXI@Z"] = Allocate,                               // operator new[](unsigned int)
                ["?Allocate@Heap@Details@Platform@@SAPAXI@Z"] = Allocate,   // Platform::Details::Heap::Allocate
                ["?AllocateException@Heap@Details@Platform@@SAPAXI@Z"] = Allocate,

                // --- CRT initialisation ---
                ["_initterm"] = () => RunStaticInitialisers(stopOnError: false),
                ["_initterm_e"] = () => RunStaticInitialisers(stopOnError: true),
                ["__crtGetShowWindowMode"] = () => Return(0),
                ["__set_app_type"] = () => Return(0),

                // A throw must never return to its caller. Without SEH unwinding there is
                // nowhere correct to go, and letting it return runs the code after the
                // throw - which the compiler generated on the assumption it is
                // unreachable. Every instruction after that is noise, and the run
                // eventually dies somewhere unrelated with no way to trace it back here.
                // Stopping is the honest answer until unwinding exists.
                ["_CxxThrowException"] = ThrowCxxException,

                // EncodePointer/DecodePointer obfuscate a pointer against overwrite
                // attacks; the CRT stores its function pointers encoded and decodes them
                // before calling. Identity is a perfectly valid implementation of the pair
                // - but a stub returning 0 is not, because the CRT then stores a null,
                // decodes a null, and calls it.
                ["EncodePointer"] = () => Return(Arg(0)),
                ["DecodePointer"] = () => Return(Arg(0)),
                ["EncodeSystemPointer"] = () => Return(Arg(0)),
                ["DecodeSystemPointer"] = () => Return(Arg(0)),

                // The _crt allocator family, which the CRT uses for its own bookkeeping.
                // void *realloc(void *ptr, size_t size). Not having this was fatal in a way
                // that took a whole session to trace back: lua_newstate's first act is
                // realloc(NULL, 0x14C), it got the default stub's zero, returned NULL, and
                // the game threw "Failed to initialized Lua interpreter" - four frames and
                // one C++/CX FailFast away from anything that mentioned memory.
                ["realloc"] = () =>
                {
                    long pointer = Arg(0);
                    long size = Arg(1);

                    if (size == 0)
                    {
                        // realloc(p, 0) frees and returns null.
                        _emulator.FreeHeap(pointer);
                        Return(0);
                        return;
                    }

                    if (pointer == 0)
                    {
                        Return(_emulator.AllocateHeap(size));
                        return;
                    }

                    // Always moves, because a bump allocator cannot grow a block in place.
                    // Copying the smaller of the two sizes is what makes that invisible to
                    // the caller; the old block is simply abandoned.
                    long moved = _emulator.AllocateHeap(size);
                    if (moved != 0)
                    {
                        long available = _emulator.AllocationSizeOf(pointer);
                        long copy = Math.Min(size, available == 0 ? size : available);
                        _emulator.WriteMemory(moved, _emulator.ReadMemory(pointer, (int)copy));

                        // The old block is dead the moment its contents have been carried
                        // across, and a game that reallocs a growing buffer would otherwise
                        // leave every previous size of it behind.
                        _emulator.FreeHeap(pointer);
                    }

                    Return(moved);
                },

                ["_malloc_crt"] = Allocate,
                ["_realloc_crt"] = Allocate,
                ["_free_crt"] = () => Return(0),

                // --- WinRT activation ---
                // HRESULT GetActivationFactoryByPCWSTR(void* className, Guid& iid, void** factory)
                ["?GetActivationFactoryByPCWSTR@@YAJPAXAAVGuid@Platform@@PAPAX@Z"] = ActivateFactory,

                // --- memory and string ---
                // These have to be real. Returning 0 from memcpy or strlen does not fail
                // loudly, it quietly corrupts whatever the image was building, and the
                // damage only shows up later as a jump to a garbage address.
                ["memcpy"] = MemoryCopy,
                ["memmove"] = MemoryCopy,
                ["memset"] = MemorySet,
                ["memcmp"] = MemoryCompare,
                ["strlen"] = () => Return(MeasureNarrowString(Arg(0))),
                ["wcslen"] = () => Return(MeasureWideString(Arg(0))),

                // Nothing is ever freed - the bump allocator has no free list - so these
                // succeed and do nothing.
                // These really do free now. They used to be no-ops, which cost nothing while
                // a run ended at the first fault and became the reason a run ended once the
                // image reached its main loop and started turning over memory per frame.
                ["free"] = Release,
                ["??3@YAXPAX@Z"] = Release,                                  // operator delete
                ["??_V@YAXPAX@Z"] = Release,                                 // operator delete[]
                ["?Free@Heap@Details@Platform@@SAXPAX@Z"] = Release,
                ["?FreeException@Heap@Details@Platform@@SAXPAX@Z"] = Release,
                ["?FreeException@Heap@Details@Platform@@SAXPAX@Z"] = () => Return(0),

                // --- HSTRING (see HStringHeap for the representation) ---
                ["WindowsCreateString"] = () => CreateHString(Arg(0), (int)Arg(1), Arg(2)),
                ["WindowsCreateStringReference"] = () => CreateHString(Arg(0), (int)Arg(1), Arg(3)),
                ["WindowsDeleteString"] = () => Return(0),   // immutable and never freed

                // Sharing the original is safe for the same reason.
                ["WindowsDuplicateString"] = () => WriteOutPointer(Arg(1), Arg(0)),

                ["WindowsGetStringRawBuffer"] = () =>
                {
                    if (Arg(1) != 0)
                    {
                        _emulator.WriteUInt32(Arg(1), _strings.LengthOf(Arg(0)));
                    }

                    Return(_strings.BufferOf(Arg(0)));
                },

                ["WindowsConcatString"] = () =>
                {
                    byte[] combined = [.. _strings.Read(Arg(0)), .. _strings.Read(Arg(1))];
                    WriteOutPointer(Arg(2), combined.Length == 0 ? 0 : _strings.Create(combined));
                },

                ["WindowsCompareStringOrdinal"] = () =>
                {
                    int ordering = Math.Sign(
                        string.CompareOrdinal(_strings.ReadText(Arg(0)), _strings.ReadText(Arg(1))));

                    if (Arg(2) != 0)
                    {
                        _emulator.WriteUInt32(Arg(2), unchecked((uint)ordering));
                    }

                    Return(0);
                },
            };

            // The C string and character functions live on their own; there are a lot of
            // them and none of them are specific to this image.
            _crt = new CrtLibrary(emulator, _frame);
            _crt.RegisterInto(_handlers);
            Files.RegisterInto(_handlers);
        }

        /// <summary>
        /// Invoked with the fully qualified <c>dll!function</c> name of a trapped import,
        /// while the CPU sits on that import's trap slot with the return address in lr.
        /// </summary>
        private readonly List<string> _undeliveredThrows = new();

        /// <summary>
        /// Points where the image asked the runtime to throw and the throw was not
        /// delivered, so the caller carried on with a value it should never have seen.
        /// </summary>
        public IReadOnlyList<string> UndeliveredThrows => _undeliveredThrows;

        public Direct3DRuntime Direct3D { get; }

        public XAudio2Runtime XAudio2 { get; }

        private const long CoNotInitialized = unchecked((int)0x800401F0);
        private const long ClassNotRegistered = unchecked((int)0x80040154);
        private const long NoInterface = unchecked((int)0x80004002);
        private const long NotImplemented = unchecked((int)0x80004001);

        /// <summary>
        /// The ten COM entry points the image imports.
        /// </summary>
        /// <remarks>
        /// Every one of these has an out-parameter, and the default stub answered all of them
        /// with S_OK and wrote none of them. That is the worst possible answer: the caller is
        /// told it succeeded and reads back whatever was already in its own memory.
        ///
        /// It is not a hypothetical. PPL captures the COM apartment for a continuation with
        /// <c>_ContextCallback::_Capture</c>, which is <c>CoGetObjectContext</c> straight into
        /// a member, with the member set to null only if the call FAILS. Succeeding without
        /// writing left that member holding a stale heap address; the matching
        /// <c>_ContextCallback::_Reset</c> then saw a pointer that was neither null nor the
        /// deferred-capture sentinel of 1, called <c>Release</c> on it, and jumped through
        /// slot 2 of a vtable that was never there. That was the null call at 0x004A47F1 that
        /// killed the loader thread and, through it, left the main loop spin-waiting forever
        /// on a flag the loader was supposed to set.
        ///
        /// The rule this encodes: a stub for a function with an out-parameter either writes
        /// the out-parameter or returns a failure the caller must handle. It may never do
        /// neither.
        /// </remarks>
        private Dictionary<string, Action> ComStubs => _comStubs ??= new(StringComparer.Ordinal)
        {
            // HRESULT CoGetObjectContext(REFIID riid, void **ppv). There is no apartment
            // here, so failing is the truthful answer and the one PPL is written to handle.
            ["CoGetObjectContext"] = () => FailWithNull(Arg(1), CoNotInitialized),

            // HRESULT CoGetContextToken(ULONG_PTR *pToken).
            ["CoGetContextToken"] = () => FailWithNull(Arg(0), CoNotInitialized),

            // HRESULT CoCreateFreeThreadedMarshaler(IUnknown *pUnkOuter, IUnknown **ppunkMarshal).
            ["CoCreateFreeThreadedMarshaler"] = () => FailWithNull(Arg(1), NotImplemented),

            // HRESULT CoMarshalInterThreadInterfaceInStream(REFIID, IUnknown*, IStream**).
            ["CoMarshalInterThreadInterfaceInStream"] = () => FailWithNull(Arg(2), NotImplemented),

            // HRESULT CoGetInterfaceAndReleaseStream(IStream*, REFIID, void**).
            ["CoGetInterfaceAndReleaseStream"] = () => FailWithNull(Arg(2), NoInterface),

            // HRESULT CoCreateInstanceFromApp(rclsid, punkOuter, dwClsCtx, reserved, count,
            // MULTI_QI*) - six arguments, and nothing here registers a CLSID.
            ["CoCreateInstanceFromApp"] = () => Return(ClassNotRegistered),

            // HRESULT CoGetApartmentType(APTTYPE *pAptType, APTTYPEQUALIFIER *pAptQualifier).
            // One thread, and it behaves like the multithreaded apartment.
            ["CoGetApartmentType"] = () =>
            {
                WriteIfPointer(Arg(0), 1); // APTTYPE_MTA
                WriteIfPointer(Arg(1), 0); // APTTYPEQUALIFIER_NONE
                Return(0);
            },

            // HRESULT CoCreateGuid(GUID *pguid). Must produce distinct values: a caller that
            // asks for two and gets the same one has two objects it believes are one.
            ["CoCreateGuid"] = () =>
            {
                if (Arg(0) != 0)
                {
                    // Counted rather than random, so a run is reproducible and two runs of the
                    // same image can be diffed against each other.
                    byte[] guid = new byte[16];
                    BitConverter.GetBytes(0x57505200 + _guidsIssued++).CopyTo(guid, 0);
                    guid[8] = 0x80;
                    _emulator.WriteMemory(Arg(0), guid);
                }

                Return(0);
            },

            // void *CoTaskMemAlloc(SIZE_T cb) - returns the block, not an HRESULT.
            ["CoTaskMemAlloc"] = () => Return(Arg(0) <= 0 ? 0 : _emulator.AllocateHeap(Arg(0))),

            // void CoTaskMemFree(void*). The allocator never frees, so this is genuinely a
            // no-op rather than an unimplemented one.
            ["CoTaskMemFree"] = Release,
        };

        private Dictionary<string, Action>? _comStubs;

        private int _guidsIssued;

        /// <summary>Blanks an out-parameter and returns a failure, which is the honest pair.</summary>
        private void FailWithNull(long outPointer, long hresult)
        {
            WriteIfPointer(outPointer, 0);
            Return(hresult);
        }

        private void WriteIfPointer(long address, uint value)
        {
            if (address != 0)
            {
                _emulator.WriteUInt32(address, value);
            }
        }

        public void Dispatch(string fullName)
        {
            int split = fullName.IndexOf('!');
            string function = split >= 0 ? fullName[(split + 1)..] : fullName;

            if (_handlers.TryGetValue(function, out Action? handler))
            {
                handler();
                return;
            }

            if (function == "D3D11CreateDevice")
            {
                Direct3D.CreateDevice();
                return;
            }

            // XAudio2Create is exported by ordinal and by no name, so the import table gives
            // it no better identity than "#1".
            if (function is "XAudio2Create" or "#1")
            {
                XAudio2.CreateEngine();
                return;
            }

            if (ComStubs.TryGetValue(function, out Action? com))
            {
                com();
                return;
            }

            // An MSVC C++ constructor returns `this` in r0, and the caller relies on it -
            // `Foo* f = new Foo()` uses the constructor's return value, not the operator
            // new result. Returning 0 from an unimplemented constructor therefore hands
            // back a null object, and the caller dereferences it a few instructions later
            // with nothing in the trace to explain why. Preserving r0 costs nothing and
            // makes an unimplemented constructor merely uninitialised rather than fatal.
            if (function.StartsWith("??0", StringComparison.Ordinal))
            {
                ConstructShapedObject(function);
                return;
            }

            // A function whose whole job is to raise never returns on a real device: it
            // throws a Platform::Exception, which the image is often ready to catch. This
            // one returns, which means the caller carries on with a value it was promised
            // it would never see. Sometimes that is survivable and sometimes it is not -
            // ClassNotRegistered was not, and execution fell out of the image into zeroed
            // heap, where 30 MB of "executed code" in the report was a Thumb no-op slide.
            //
            // Recording them is the honest half of what can be done without wiring
            // vccorlib into the C++ exception machinery: the report then names every point
            // where the image gave up, whether or not it survived doing so.
            if (function.Contains("raise", StringComparison.OrdinalIgnoreCase))
            {
                _undeliveredThrows.Add(function);
            }

            // Everything else returns zero. For a function returning void or a handle
            // nobody checks that is harmless; for anything else it is a lie the caller
            // will eventually notice, which is exactly what this probe measures.
            Return(0);
        }

        /// <summary>
        /// HRESULT GetActivationFactoryByPCWSTR(void* className, Guid&amp; iid, void** factory).
        /// Hands back a synthetic factory object when the runtime implements the class, and
        /// REGDB_E_CLASSNOTREG when it does not - which vccorlib turns into a
        /// ClassNotRegisteredException, exactly as it would on a real device.
        /// </summary>
        private void ActivateFactory()
        {
            string className = _emulator.ReadUtf16String(Arg(0));
            long outFactory = Arg(2);

            long? factory = _winRt.GetActivationFactory(className);
            if (factory is null)
            {
                RequestedClasses.Add($"{className} -> REGDB_E_CLASSNOTREG");
                Return(WinRtRuntime.ClassNotRegistered);
                return;
            }

            if (outFactory != 0)
            {
                _emulator.WriteUInt32(outFactory, (uint)factory.Value);
            }

            RequestedClasses.Add($"{className} -> factory at 0x{factory.Value:X8}");
            Return(0); // S_OK
        }

        /// <summary>Runtime classes the image tried to activate, in order.</summary>
        public List<string> RequestedClasses { get; } = new();

        private readonly Dictionary<string, long> _constructorVtables = new(StringComparer.Ordinal);

        /// <summary>
        /// Stands in for an unimplemented C++ constructor.
        /// </summary>
        /// <remarks>
        /// Returning <c>this</c> is necessary but not sufficient. The object is exactly as
        /// the allocator handed it over - all zeros - so the first virtual call on it goes
        /// through a null vtable, a long way from here and with nothing to explain it.
        /// Giving it a shaped vtable turns that into a named call instead.
        ///
        /// Only a zeroed first word is replaced: an object whose vtable is already set was
        /// constructed by real code, and overwriting it would be the bug rather than the fix.
        /// </remarks>
        private void ConstructShapedObject(string constructor)
        {
            long instance = Arg(0);
            Return(instance);

            if (instance == 0 || _emulator.ReadUInt32(instance) != 0)
            {
                return;
            }

            if (!_constructorVtables.TryGetValue(constructor, out long vtable))
            {
                vtable = _emulator.CreateShapedVtable(constructor);
                _constructorVtables[constructor] = vtable;
            }

            _emulator.WriteUInt32(instance, (uint)vtable);
        }

        /// <summary>File I/O, backed by real files.</summary>
        public FileLibrary Files { get; }

        /// <summary>How many C++ static initialisers actually ran.</summary>
        public int StaticInitialisersRun { get; private set; }

        /// <summary>The stack at the point the image threw, outermost frame last.</summary>
        public IReadOnlyList<UnwoundFrame> ThrowStack { get; private set; } = [];

        /// <summary>
        /// <c>void _CxxThrowException(void* object, _ThrowInfo* info)</c>.
        /// </summary>
        /// <remarks>
        /// A throw must never return to its caller, so this cannot be stubbed: letting it
        /// return runs the code the compiler emitted on the assumption it is unreachable.
        /// What it can do is unwind - walk the frames the way the real handler search would
        /// - and report where the throw came from and which frames along the way carry a
        /// handler. Transferring control into one of those handlers is the part still
        /// missing, so the run stops here rather than continuing incorrectly.
        /// </remarks>
        private void ThrowCxxException()
        {
            // The whole live register file: unwinding needs more than sp and lr, because
            // a frame-pointer restore reads the stack pointer straight out of a register.
            int[] coreRegisters =
            [
                Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
                Arm.UC_ARM_REG_R4, Arm.UC_ARM_REG_R5, Arm.UC_ARM_REG_R6, Arm.UC_ARM_REG_R7,
                Arm.UC_ARM_REG_R8, Arm.UC_ARM_REG_R9, Arm.UC_ARM_REG_R10, Arm.UC_ARM_REG_R11,
                Arm.UC_ARM_REG_R12, Arm.UC_ARM_REG_SP, Arm.UC_ARM_REG_LR, Arm.UC_ARM_REG_PC,
            ];

            // Everything below this is still live for the duration of the throw: the
            // exception object itself is a local of whichever frame threw, and the objects
            // the cleanup funclets are about to destroy are locals of the frames between
            // here and the catch. Funclets therefore have to run on a stack deeper than
            // this, never on the frame they are unwinding to.
            _funcletStack = _emulator.ReadRegister(Arm.UC_ARM_REG_SP) - FuncletStackMargin;

            ThrowStack = _emulator.Unwinder.Walk(
                pc: _emulator.ReturnAddress & ~1L,
                liveRegisters: coreRegisters.Select(_emulator.ReadRegister).ToArray());

            ThrownText = ReadableStringsIn(Arg(0), 0x100);

            // r0 is the exception object, r1 its ThrowInfo.
            CxxExceptionModel model = _emulator.ExceptionModel;
            Thrown = model.ReadThrow(Arg(0), Arg(1));
            CatchCandidates = model.FindHandlers(ThrowStack, Thrown);

            if (CatchCandidates.Count == 0)
            {
                _emulator.Stop(
                    $"the image threw {Thrown.TypeName}; unwound {ThrowStack.Count} frames, " +
                    "no matching catch found");
                return;
            }

            TransferToHandler(CatchCandidates[0], Thrown);
        }

        /// <summary>
        /// Enters a catch handler: the last step of a throw.
        /// </summary>
        /// <remarks>
        /// A catch clause is compiled as a **funclet** - a separate function sharing the
        /// establisher frame's locals. Entering one means putting the stack pointer back
        /// where that frame had it, copying the caught object into the frame slot the
        /// funclet expects it in, and calling the funclet. It returns the address to
        /// resume at, which is the instruction after the whole try/catch.
        ///
        /// Cleanup funclets for the frames between the throw and the catch are **not** run,
        /// so destructors are skipped. That leaks - but the allocator here never frees
        /// anything anyway, so it leaks nothing that was not already lost.
        /// </remarks>
        private void TransferToHandler(CatchCandidate candidate, ThrownException thrown)
        {
            long frame = candidate.Frame.FramePointer + candidate.FrameOffset;
            long funclet = _emulator.ImageBase + candidate.FuncletRva;

            if (candidate.FuncletRva == 0)
            {
                _emulator.Stop(
                    $"the image threw {thrown.TypeName}; the matching catch has no funclet address");
                return;
            }

            // The funclet reads the caught object out of its own frame, not a register.
            if (candidate.CatchObjectOffset != 0)
            {
                _emulator.WriteUInt32(frame + candidate.CatchObjectOffset, (uint)thrown.Object);
            }

            // Destructors first, innermost outwards, then the catch itself.
            List<CxxExceptionModel.CleanupAction> cleanups =
                _emulator.ExceptionModel.CollectCleanups(ThrowStack, candidate);

            TransferLog.Add(
                $"entering catch({candidate.CaughtType}) funclet 0x{candidate.FuncletRva:X8} " +
                $"with frame 0x{frame:X8}, after {cleanups.Count} cleanup funclet(s)");

            RunCleanups(cleanups, 0, () => EnterCatchFunclet(candidate, frame, funclet));
        }

        /// <summary>
        /// Runs the cleanup funclets one at a time, then continues with
        /// <paramref name="onFinished"/>. Each is emulated code, so this is another
        /// continuation chain.
        /// </summary>
        private void RunCleanups(
            List<CxxExceptionModel.CleanupAction> cleanups, int index, Action onFinished)
        {
            if (index >= cleanups.Count)
            {
                onFinished();
                return;
            }

            CxxExceptionModel.CleanupAction action = cleanups[index];
            TransferLog.Add($"   {action.Description} -> funclet 0x{action.FuncletRva:X8}");

            EnterFunclet(
                (_emulator.ImageBase + action.FuncletRva) | 1,
                action.Frame,
                "cleanup funclet",
                onReturn: () => RunCleanups(cleanups, index + 1, onFinished));
        }

        /// <summary>
        /// How far below the throw point a funclet's own stack starts.
        /// </summary>
        /// <remarks>
        /// Only has to clear the frame of whatever called _CxxThrowException. Anything at
        /// all would do; a page is simply comfortable.
        /// </remarks>
        private const long FuncletStackMargin = 0x1000;

        private long _funcletStack;

        /// <summary>
        /// Calls an EH funclet the way the real runtime does on ARM.
        /// </summary>
        /// <remarks>
        /// The establisher frame goes in **r11**, not in sp. MSVC's <c>_CallSettingFrame</c>
        /// is three instructions - <c>mov r11, r1</c>, <c>blx r0</c> - and the funclet reads
        /// its parent's locals through r11 while running on whatever stack the handler is
        /// already on, which is deep, below the throw.
        ///
        /// Putting sp at the establisher frame instead, as this used to, is wrong in a way
        /// that takes a while to show: the funclet itself runs fine, but every call it makes
        /// allocates its frame *below* sp - which is exactly where the exception object and
        /// the not-yet-destroyed locals of the unwound frames still live. The thrown object
        /// gets overwritten mid-unwind, and a later destructor walks whatever replaced it.
        /// Here that was a std::vector destructor walking a std::string, in a loop that
        /// destroyed twelve megabytes of heap before the runaway detector caught it.
        /// </remarks>
        private void EnterFunclet(long funclet, UnwoundFrame frame, string name, Action onReturn)
        {
            RestoreCalleeSaved(frame);
            _emulator.WriteRegister(Arm.UC_ARM_REG_SP, _funcletStack);
            _emulator.CallEmulated(name, funclet, [0, frame.FramePointer], onReturn);
        }

        /// <summary>
        /// Puts a frame's callee-saved registers back, which is how a funclet finds its
        /// parent's locals.
        /// </summary>
        /// <remarks>
        /// Not r11, and not the frame pointer passed in r1. This image's funclets are
        /// **r7-relative** - every one of them is two instructions, <c>adds r0, r7, #N</c>
        /// followed by a tail call to a destructor - because r7 is what its prologues use as
        /// the frame base. Which register a funclet reaches through is the compiler's
        /// choice, so the honest thing is to restore the whole callee-saved set and let the
        /// funclet use whichever it was built to use.
        ///
        /// Getting this wrong is silent and destructive rather than an error: the funclet
        /// runs, computes <c>this</c> from a stale register, and calls a real destructor on
        /// whatever is there. That is how a std::vector destructor came to be walking a
        /// std::string, destroying twelve megabytes of heap in a loop that could not
        /// terminate because the two pointers it compares had never belonged to each other.
        /// </remarks>
        private void RestoreCalleeSaved(UnwoundFrame frame)
        {
            int[] calleeSaved =
            [
                Arm.UC_ARM_REG_R4, Arm.UC_ARM_REG_R5, Arm.UC_ARM_REG_R6, Arm.UC_ARM_REG_R7,
                Arm.UC_ARM_REG_R8, Arm.UC_ARM_REG_R9, Arm.UC_ARM_REG_R10, Arm.UC_ARM_REG_R11,
            ];

            IReadOnlyList<long> saved = frame.Registers;
            if (saved.Count < 12)
            {
                return;
            }

            for (int i = 0; i < calleeSaved.Length; i++)
            {
                _emulator.WriteRegister(calleeSaved[i], saved[i + 4]);
            }
        }

        private void EnterCatchFunclet(CatchCandidate candidate, long frame, long funclet)
        {
            // The code after the catch belongs to the establisher frame and expects its own
            // r4-r11, not whatever the last cleanup funclet left in them.
            EnterFunclet(funclet | 1, candidate.Frame, "catch funclet",
                onReturn: () =>
                {
                    long continuation = Arg(0);
                    long stack = _emulator.ReadRegister(Arm.UC_ARM_REG_SP);

                    // The continuation eventually runs the function's own epilogue, which
                    // pops its return address off the stack. If the stack pointer handed
                    // back is wrong, that pop reads a zero and the function "returns" to
                    // address 0 - so it is worth seeing what is actually there.
                    TransferLog.Add(
                        $"   funclet returned continuation 0x{continuation:X8}, sp=0x{stack:X8}");
                    TransferLog.Add(
                        "   stack above sp: " + string.Join(" ", Enumerable.Range(0, 8)
                            .Select(i => $"+0x{i * 4 + 0x74:X2}=0x{_emulator.ReadUInt32(stack + 0x74 + (i * 4)):X8}")));

                    if ((continuation & ~1L) < _emulator.ImageBase)
                    {
                        _emulator.Stop(
                            $"catch funclet returned 0x{continuation:X8}, which is not a code address");
                        return;
                    }

                    _emulator.WriteRegister(Arm.UC_ARM_REG_SP, frame);
                    _emulator.ContinueAt(continuation);
                });
        }

        /// <summary>What the image threw, once it has thrown.</summary>
        public ThrownException? Thrown { get; private set; }

        /// <summary>Any readable text inside the thrown object - usually its message.</summary>
        public IReadOnlyList<string> ThrownText { get; private set; } = [];

        /// <summary>
        /// Pulls the strings out of an object without knowing its layout.
        /// </summary>
        /// <remarks>
        /// An exception carries the one piece of information worth more than everything the
        /// type name and the stack say together: what actually went wrong, in words the
        /// image's own author wrote. Getting at it properly would mean knowing where the
        /// std::string members are, which varies per class.
        ///
        /// So this does not try. It reads the object as words, follows any that point at
        /// readable memory, and keeps whatever comes back as printable text - which catches
        /// a heap-allocated std::string. It also reads the object's own bytes directly,
        /// which catches a short one stored inline. Both are guesses; both are checked
        /// against being printable before they are believed.
        /// </remarks>
        private List<string> ReadableStringsIn(long address, int size)
        {
            var found = new List<string>();
            if (address == 0)
            {
                return found;
            }

            void Consider(long from)
            {
                string text = _frame.ReadNarrowString(from, 512);
                if (text.Length >= 4 && text.All(c => c is >= ' ' and < (char)127))
                {
                    found.Add(text);
                }
            }

            for (int offset = 0; offset < size; offset += 4)
            {
                try
                {
                    Consider(address + offset);

                    uint pointer = _emulator.ReadUInt32(address + offset);
                    if (pointer > 0x10000)
                    {
                        Consider(pointer);
                    }
                }
                catch (Exception)
                {
                    // An unreadable word is just a word that is not a string.
                }
            }

            return found.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>Catch clauses that accept the thrown object, innermost first.</summary>
        public IReadOnlyList<CatchCandidate> CatchCandidates { get; private set; } = [];

        /// <summary>Each handler entered, and where it resumed.</summary>
        public List<string> TransferLog { get; } = new();

        /// <summary>Each initialiser table walked, and the entries found in it.</summary>
        public List<string> InitialiserLog { get; } = new();

        /// <summary>Strings the image has formatted through the printf family.</summary>
        public IReadOnlyList<string> FormattedStrings => _crt.FormattedStrings;

        /// <summary>What the image asked the PPL task machinery to do.</summary>
        public IReadOnlyList<string> TaskCollectionLog => _crt.TaskCollectionLog;

        /// <summary>
        /// <c>void _initterm(void (**first)(), void (**last)())</c> - walks a table of
        /// function pointers and calls each one. <c>_initterm_e</c> is the same, except each
        /// returns an int and a non-zero result aborts the rest.
        /// </summary>
        /// <remarks>
        /// Both used to return 0 and do nothing, which claims every initialiser succeeded
        /// without running any of them. That is survivable right up until the image touches
        /// a global that a constructor was supposed to fill in - and then it is a null
        /// pointer with no explanation, a long way from here.
        ///
        /// Running them for real is only possible because a host trap can call back into
        /// emulated code. Each initialiser is a separate call that has to complete before
        /// the next begins, so this is another continuation chain.
        /// </remarks>
        private void RunStaticInitialisers(bool stopOnError)
        {
            long cursor = Arg(0);
            long end = Arg(1);
            long callerReturn = _emulator.ReturnAddress;

            // A garbage table would otherwise walk the address space forever.
            const long tableLimit = 64 * 1024;
            if (cursor == 0 || end <= cursor || end - cursor > tableLimit)
            {
                InitialiserLog.Add($"table 0x{cursor:X8}..0x{end:X8} REJECTED as implausible");
                Return(0);
                return;
            }

            InitialiserLog.Add($"table 0x{cursor:X8}..0x{end:X8} ({(end - cursor) / 4} entries, stopOnError={stopOnError})");

            void RunNext()
            {
                while (cursor < end)
                {
                    long initialiser = _emulator.ReadUInt32(cursor);
                    cursor += 4;

                    if (initialiser == 0)
                    {
                        continue;   // padding in the table, which is normal
                    }

                    StaticInitialisersRun++;
                    InitialiserLog.Add($"  init 0x{initialiser:X8}");
                    _emulator.CallEmulated("static initialiser", initialiser, [], onReturn: () =>
                    {
                        if (stopOnError && Arg(0) != 0)
                        {
                            Return(Arg(0));
                            _emulator.ContinueAt(callerReturn);
                            return;
                        }

                        RunNext();
                    });

                    return;
                }

                Return(0);
                _emulator.ContinueAt(callerReturn);
            }

            RunNext();
        }

        // -------------------------------------------------------------------------
        // Memory and string
        // -------------------------------------------------------------------------

        /// <summary>Anything larger than this is treated as a garbage length, not a real one.</summary>
        private const int SaneLengthLimit = 64 * 1024 * 1024;

        private void MemoryCopy()
        {
            long destination = Arg(0);
            long source = Arg(1);
            long count = Arg(2);

            if (count is > 0 and < SaneLengthLimit && destination != 0 && source != 0)
            {
                _emulator.WriteMemory(destination, _emulator.ReadMemory(source, (int)count));
            }

            Return(destination);
        }

        private void MemorySet()
        {
            long destination = Arg(0);
            long count = Arg(2);

            if (count is > 0 and < SaneLengthLimit && destination != 0)
            {
                byte[] fill = new byte[count];
                Array.Fill(fill, (byte)(Arg(1) & 0xFF));
                _emulator.WriteMemory(destination, fill);
            }

            Return(destination);
        }

        private void MemoryCompare()
        {
            long count = Arg(2);
            if (count is <= 0 or >= SaneLengthLimit || Arg(0) == 0 || Arg(1) == 0)
            {
                Return(0);
                return;
            }

            byte[] left = _emulator.ReadMemory(Arg(0), (int)count);
            byte[] right = _emulator.ReadMemory(Arg(1), (int)count);

            for (int i = 0; i < count; i++)
            {
                if (left[i] != right[i])
                {
                    Return(left[i] < right[i] ? -1 : 1);
                    return;
                }
            }

            Return(0);
        }

        private long MeasureNarrowString(long address)
        {
            if (address == 0)
            {
                return 0;
            }

            const int chunk = 256;
            for (long offset = 0; offset < SaneLengthLimit; offset += chunk)
            {
                byte[] bytes = _emulator.ReadMemory(address + offset, chunk);
                int terminator = Array.IndexOf(bytes, (byte)0);
                if (terminator >= 0)
                {
                    return offset + terminator;
                }
            }

            return 0;
        }

        private long MeasureWideString(long address)
        {
            if (address == 0)
            {
                return 0;
            }

            for (long index = 0; index < SaneLengthLimit / 2; index++)
            {
                if (BitConverter.ToUInt16(_emulator.ReadMemory(address + (index * 2), 2)) == 0)
                {
                    return index;
                }
            }

            return 0;
        }

        /// <summary>
        /// HRESULT WindowsCreateString(PCWSTR source, UINT32 length, HSTRING* result).
        /// A zero length is the empty string, which WinRT represents as a null handle.
        /// </summary>
        private void CreateHString(long source, int lengthInChars, long result)
        {
            long handle = lengthInChars <= 0 || source == 0
                ? 0
                : _strings.Create(_emulator.ReadMemory(source, lengthInChars * 2));

            WriteOutPointer(result, handle);
        }

        /// <summary>Writes a pointer through an out-parameter and reports S_OK.</summary>
        private void WriteOutPointer(long destination, long value)
        {
            if (destination != 0)
            {
                _emulator.WriteUInt32(destination, (uint)value);
            }

            Return(0);
        }

        private void QueryPerformance()
        {
            WriteInt64(Arg(0), _tick * 1000);
            _tick++;
            Return(1); // BOOL TRUE
        }

        private void Release()
        {
            _emulator.FreeHeap(Arg(0));
            Return(0);
        }

        private void Allocate()
        {
            long size = Arg(0);
            Return(_emulator.AllocateHeap(size));
        }

        /// <summary>
        /// void *calloc(size_t nmemb, size_t size) - two arguments, multiplied, and zeroed.
        /// </summary>
        /// <remarks>
        /// The zeroing is belt and braces today, because a bump allocator that never reuses
        /// only ever hands out pages Unicorn mapped as zero. It is written anyway: the
        /// moment this allocator learns to recycle, a calloc that quietly stopped zeroing
        /// would be the hardest bug in the whole probe to find.
        /// </remarks>
        private void AllocateZeroed()
        {
            long total = Arg(0) * Arg(1);
            if (total <= 0 || total > CallFrame.SaneLengthLimit)
            {
                Return(0);
                return;
            }

            long block = _emulator.AllocateHeap(total);
            if (block != 0)
            {
                _emulator.WriteMemory(block, new byte[total]);
            }

            Return(block);
        }

        private long Arg(int index) => _frame.Arg(index);

        private void Return(long value) => _frame.Return(value);

        private void Return64(long value) => _frame.Return64(value);

        private void WriteInt64(long address, long value) => _frame.WriteInt64(address, value);
    }
}
