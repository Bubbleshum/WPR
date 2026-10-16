using System.Security.Cryptography;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// MSVCP110 apart from iostreams and locale: the <c>_X*</c> throw helpers, the error-code maps,
    /// <c>_Lockit</c>, the debug-iterator container base, <c>_Yarn</c>, <c>exception_ptr</c> and the
    /// small fry around them, each to VC11's behaviour.
    /// </summary>
    /// <remarks>
    /// <para><b>Thrown objects are real.</b> Every <c>_X*</c> helper builds the std exception it
    /// names in guest memory - <c>{vfptr, _Mywhat, _Mydofree}</c> as VC11 lays out
    /// <c>std::exception</c>, plus the derived class's own members - and throws it through the
    /// image's own handler search with the catchable type names VC11's ThrowInfo would carry
    /// (most derived first, e.g. <c>.?AVlength_error@std@@</c>, <c>.?AVlogic_error@std@@</c>,
    /// <c>.?AVexception@std@@</c>). A game catches <c>std::exception&amp;</c> and calls
    /// <c>e.what()</c> through the vtable, so the vtable is real: slot 0 is the scalar deleting
    /// destructor, slot 1 <c>what()</c>, both host methods, and the word before it points at a
    /// CompleteObjectLocator so <c>dynamic_cast</c> and <c>typeid</c> work on the object too.</para>
    /// <para>The strings and tables are VC11's (syserror.cpp, future.cpp, xthread, regex), from
    /// the shipped headers and sources; where VC11's exact text could not be confirmed it is said
    /// so at the table.</para>
    /// </remarks>
    public sealed class MsvcpCore
    {
        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Action<ThrownException> _throw;
        private readonly Func<ThrownException?> _handled;

        public MsvcpCore(ArmEmulator emulator, CallFrame frame, Action<ThrownException> throwIntoImage, Func<ThrownException?> currentlyHandled)
        {
            _emulator = emulator;
            _frame = frame;
            _throw = throwIntoImage;
            _handled = currentlyHandled;

            // A thread that dies holding a _Lockit would otherwise hold that lock for ever.
            emulator.GuestThreadAbandoned += id =>
            {
                foreach (int kind in _lockits.Where(l => l.Value.Owner == id && l.Value.Count > 0).Select(l => l.Key).ToList())
                {
                    _lockits[kind] = (0, 0);
                }
            };
        }

        /// <summary>
        /// MSVCP110's exported variables. Their IAT slots must hold the address of real storage
        /// (see <c>ArmEmulator.DataImports</c>), so they are given cells rather than traps.
        /// </summary>
        /// <remarks>
        /// <c>_Inf</c>, <c>_Nan</c>, <c>_FNan</c> are <c>_Dconst</c> unions (16 bytes; the
        /// <c>double</c> or <c>float</c> member sits at offset 0) that numeric_limits reads.
        /// <c>_BADOFF</c> is the 64-bit <c>streamoff</c> -1. The placeholders are empty objects:
        /// their address is all <c>std::bind</c> uses.
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, byte[]> DataConstants = BuildDataConstants();

        private static Dictionary<string, byte[]> BuildDataConstants()
        {
            static byte[] Dconst(byte[] value) => [.. value, .. new byte[16 - value.Length]];

            var constants = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["?_BADOFF@std@@3_JB"] = BitConverter.GetBytes(-1L),
                ["_Inf"] = Dconst(BitConverter.GetBytes(double.PositiveInfinity)),
                ["_Nan"] = Dconst(BitConverter.GetBytes(0x7FF8000000000000L)),
                ["_FNan"] = Dconst(BitConverter.GetBytes(0x7FC00000)),
                ["_Snan"] = Dconst(BitConverter.GetBytes(0x7FF4000000000000L)),
                ["_FSnan"] = Dconst(BitConverter.GetBytes(0x7FA00000)),
            };

            // std::placeholders::_1.._10: _Ph<N> mangles N as $0 followed by the digit N-1.
            for (int n = 1; n <= 10; n++)
            {
                constants[$"?_{n}@placeholders@std@@3V?$_Ph@$0{n - 1}@2@A"] = new byte[8];
            }

            return constants;
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            RegisterThrowers(handlers);
            RegisterErrorMaps(handlers);
            RegisterLocks(handlers);
            RegisterContainers(handlers);
            RegisterExceptionPointers(handlers);
            RegisterMisc(handlers);
        }

        // ------------------------------------------------------------------------------------
        // Thrown std exceptions
        // ------------------------------------------------------------------------------------

        /// <summary>A std exception class the host can build: its RTTI, its vtable, its <c>what()</c>.</summary>
        public sealed record StdClass(string Name, IReadOnlyList<string> Catchable, long Vtable, int Size);

        private readonly Dictionary<string, StdClass> _classes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _typeDescriptors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _staticStrings = new(StringComparer.Ordinal);

        /// <summary>Objects the host built, so the deleting destructor knows its own.</summary>
        private readonly HashSet<long> _hostObjects = new();

        private const string UnknownExceptionText = "Unknown exception";

        private long Read(long address) => _emulator.ReadUInt32(address, 0);

        private void Write(long address, long value) => _emulator.WriteUInt32(address, (uint)value);

        /// <summary>A NUL-terminated string that lives for the rest of the run, one per distinct text.</summary>
        public long StaticString(string text)
        {
            if (!_staticStrings.TryGetValue(text, out long address))
            {
                byte[] bytes = Encoding.Latin1.GetBytes(text);
                address = _emulator.AllocateHeap(bytes.Length + 1);
                _emulator.WriteMemory(address, [.. bytes, 0]);
                _staticStrings[text] = address;
            }

            return address;
        }

        private long HeapString(string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            long address = _emulator.AllocateHeap(bytes.Length + 1);
            _emulator.WriteMemory(address, [.. bytes, 0]);
            return address;
        }

        /// <summary>The TypeDescriptor for a decorated class name: <c>{pVFTable, spare, name}</c>.</summary>
        private long TypeDescriptor(string decorated)
        {
            if (!_typeDescriptors.TryGetValue(decorated, out long descriptor))
            {
                byte[] name = Encoding.ASCII.GetBytes(decorated);
                descriptor = _emulator.AllocateHeap(8 + name.Length + 1);
                _emulator.WriteMemory(descriptor, [.. new byte[8], .. name, 0]);
                _typeDescriptors[decorated] = descriptor;
            }

            return descriptor;
        }

        /// <summary>
        /// The 32-bit RTTI for a single-inheritance chain, most derived first: a
        /// CompleteObjectLocator over a ClassHierarchyDescriptor over one BaseClassDescriptor per
        /// class (entry 0 the class itself), the layout <see cref="RttiLibrary"/> reads.
        /// </summary>
        private long CompleteObjectLocator(IReadOnlyList<string> chain)
        {
            long baseArray = _emulator.AllocateHeap(chain.Count * 4);
            for (int i = 0; i < chain.Count; i++)
            {
                // {pTypeDescriptor, numContainedBases, PMD {mdisp 0, pdisp -1, vdisp 0}, attributes, pClassDescriptor}
                long descriptor = _emulator.AllocateHeap(28);
                _emulator.WriteMemory(descriptor, new byte[28]);
                Write(descriptor, TypeDescriptor(chain[i]));
                Write(descriptor + 4, chain.Count - 1 - i);
                Write(descriptor + 12, 0xFFFFFFFF);
                Write(baseArray + (i * 4), descriptor);
            }

            long hierarchy = _emulator.AllocateHeap(16);
            Write(hierarchy, 0);
            Write(hierarchy + 4, 0);
            Write(hierarchy + 8, chain.Count);
            Write(hierarchy + 12, baseArray);

            long locator = _emulator.AllocateHeap(20);
            _emulator.WriteMemory(locator, new byte[20]);
            Write(locator + 12, TypeDescriptor(chain[0]));
            Write(locator + 16, hierarchy);
            return locator;
        }

        /// <summary>
        /// Builds a vtable whose word -1 is the locator and whose slots are host methods.
        /// </summary>
        private long Vtable(string name, IReadOnlyList<string> chain, params (string Slot, Action Body)[] slots)
        {
            long block = _emulator.AllocateHeap(4 + (slots.Length * 4));
            Write(block, CompleteObjectLocator(chain));
            for (int i = 0; i < slots.Length; i++)
            {
                long trap = _emulator.RegisterVtableMethod($"{name}::{slots[i].Slot}", slots[i].Body);
                Write(block + 4 + (i * 4), ArmEmulator.ThumbEntry(trap));
            }

            return block + 4;
        }

        /// <summary>
        /// One std exception class: <paramref name="chain"/> is <c>Name@namespace</c> per class,
        /// most derived first. <paramref name="what"/> overrides <c>what()</c>; by default it is
        /// <c>std::exception</c>'s: <c>_Mywhat</c>, or "Unknown exception" when that is null.
        /// </summary>
        private StdClass Class(string[] chain, int size = 12, Func<long, long>? what = null)
        {
            string name = chain[0];
            if (_classes.TryGetValue(name, out StdClass? known))
            {
                return known;
            }

            string[] decorated = chain.Select(c => $".?AV{c}@@").ToArray();
            what ??= self =>
            {
                long text = Read(self + 4);
                return text != 0 ? text : StaticString(UnknownExceptionText);
            };

            long vtable = Vtable(
                name,
                decorated,
                ("`scalar deleting destructor'", () => DeletingDestructor()),
                ("what", () => _frame.Return(what(_frame.Arg(0)))));

            StdClass made = new(name, decorated, vtable, size);
            _classes[name] = made;
            return made;
        }

        /// <summary>
        /// <c>void* exception::`scalar deleting destructor'(unsigned flags)</c>: the destructor
        /// frees <c>_Mywhat</c> when <c>_Mydofree</c> says it owns it; bit 0 of the flags then
        /// frees the object. Returns <c>this</c>.
        /// </summary>
        private void DeletingDestructor()
        {
            long self = _frame.Arg(0);
            long flags = _frame.Arg(1);
            if (self != 0)
            {
                if ((_emulator.ReadMemory(self + 8, 1)[0] & 1) != 0)
                {
                    _emulator.FreeHeap(Read(self + 4));
                    Write(self + 4, 0);
                    _emulator.WriteMemory(self + 8, [0]);
                }

                if ((flags & 1) != 0)
                {
                    _hostObjects.Remove(self);
                    _emulator.FreeHeap(self);
                }
            }

            _frame.Return(self);
        }

        /// <summary>
        /// Allocates an instance: vfptr, then <c>_Mywhat</c> - a heap copy the object owns
        /// (<c>_Mydofree</c> = 1) when <paramref name="owned"/>, else a static string - and zeroes
        /// the rest.
        /// </summary>
        public long NewObject(StdClass type, string? message, bool owned = true)
        {
            long self = _emulator.AllocateHeap(type.Size);
            _emulator.WriteMemory(self, new byte[type.Size]);
            Write(self, type.Vtable);
            if (message is not null)
            {
                Write(self + 4, owned ? HeapString(message) : StaticString(message));
                _emulator.WriteMemory(self + 8, [owned ? (byte)1 : (byte)0]);
            }

            _hostObjects.Add(self);
            return self;
        }

        /// <summary>
        /// Constructs an instance in memory the caller allocated (an imported constructor): vfptr, then
        /// <c>_Mywhat</c> as <see cref="NewObject"/> writes it, or null and <c>_Mydofree</c> 0 for no message.
        /// </summary>
        public void InitObject(StdClass type, long self, string? message)
        {
            if (self == 0)
            {
                return;
            }

            _emulator.WriteMemory(self, new byte[type.Size]);
            Write(self, type.Vtable);
            if (message is not null)
            {
                Write(self + 4, HeapString(message));
                _emulator.WriteMemory(self + 8, [1]);
            }
        }

        /// <summary>
        /// A ConcRT exception class (concrt.h): <c>Concurrency::&lt;name&gt; : std::exception</c>, the
        /// shape of task_canceled, invalid_operation, missing_wait and the rest.
        /// </summary>
        public StdClass ConcurrencyException(string name) => Class([name + "@Concurrency", "exception" + Std]);

        /// <summary>Throws a host-built object into the image, catchable as every class in its chain.</summary>
        public void Throw(StdClass type, long self) => _throw(new ThrownException(self, type.Catchable));

        private const string Std = "@std";

        public StdClass LogicError(string derived) =>
            derived == "logic_error" ? Class(["logic_error" + Std, "exception" + Std]) : Class([derived + Std, "logic_error" + Std, "exception" + Std]);

        public StdClass RuntimeError(string derived) =>
            derived == "runtime_error" ? Class(["runtime_error" + Std, "exception" + Std]) : Class([derived + Std, "runtime_error" + Std, "exception" + Std]);

        // bad_alloc() : exception("bad allocation", 1) - the constructor that does not copy.
        public StdClass BadAlloc => Class(["bad_alloc" + Std, "exception" + Std]);

        // VC11 <functional>: bad_function_call overrides what() to return the literal.
        public StdClass BadFunctionCall => Class(["bad_function_call" + Std, "exception" + Std], what: _ => StaticString("bad function call"));

        public StdClass BadException => Class(["bad_exception" + Std, "exception" + Std]);

        /// <summary><c>system_error : runtime_error</c>, then <c>error_code _Mycode {int, const error_category*}</c> at +12.</summary>
        public StdClass SystemError => Class(["system_error" + Std, "runtime_error" + Std, "exception" + Std], size: 20);

        /// <summary><c>future_error : logic_error</c> with <c>error_code</c> at +12; what() is <c>_Get_future_error_what(code)</c>.</summary>
        public StdClass FutureError => Class(
            ["future_error" + Std, "logic_error" + Std, "exception" + Std],
            size: 20,
            what: self => StaticString(FutureWhat(unchecked((int)Read(self + 12)))));

        /// <summary><c>regex_error : runtime_error</c> with <c>regex_constants::error_type _Err</c> at +12.</summary>
        public StdClass RegexError => Class(["regex_error" + Std, "runtime_error" + Std, "exception" + Std], size: 16);

        private void ThrowMessage(StdClass type)
        {
            // VC11 builds a std::string from the argument and copies it into the exception; a
            // null pointer is undefined there, and is thrown here with an empty message.
            string message = _frame.ReadNarrowString(_frame.Arg(0), 4096);
            Throw(type, NewObject(type, message));
        }

        private void ThrowBadAlloc() => Throw(BadAlloc, NewObject(BadAlloc, "bad allocation", owned: false));

        private void RegisterThrowers(Dictionary<string, Action> handlers)
        {
            handlers["?_Xlength_error@std@@YAXPBD@Z"] = () => ThrowMessage(LogicError("length_error"));
            handlers["?_Xout_of_range@std@@YAXPBD@Z"] = () => ThrowMessage(LogicError("out_of_range"));
            handlers["?_Xinvalid_argument@std@@YAXPBD@Z"] = () => ThrowMessage(LogicError("invalid_argument"));
            handlers["?_Xoverflow_error@std@@YAXPBD@Z"] = () => ThrowMessage(RuntimeError("overflow_error"));
            handlers["?_Xruntime_error@std@@YAXPBD@Z"] = () => ThrowMessage(RuntimeError("runtime_error"));
            handlers["?_Xbad_alloc@std@@YAXXZ"] = ThrowBadAlloc;
            handlers["?_Nomemory@std@@YAXXZ"] = ThrowBadAlloc;
            handlers["?_Xbad_function_call@std@@YAXXZ"] = () => Throw(BadFunctionCall, NewObject(BadFunctionCall, null));

            // void _Xregex_error(regex_constants::error_type): throw regex_error(code), whose
            // runtime_error message is regex_error::_Stringify(code).
            handlers["?_Xregex_error@std@@YAXW4error_type@regex_constants@1@@Z"] = () =>
            {
                int code = _frame.SignedArg(0);
                long self = NewObject(RegexError, RegexMessage(code));
                Write(self + 12, code);
                Throw(RegexError, self);
            };

            // void _Throw_Cpp_error(int): throw system_error(error_code(_Code[n], generic_category()), _Mesg[n]).
            handlers["?_Throw_Cpp_error@std@@YAXH@Z"] = () => ThrowCppError(_frame.SignedArg(0));

            // void _Throw_C_error(int): a <thr/threads.h> result turned into _Throw_Cpp_error.
            // _Thrd_nomem and _Thrd_timedout -> resource_unavailable_try_again, _Thrd_busy ->
            // device_or_resource_busy, _Thrd_error -> invalid_argument. VC11 calls abort() for any
            // other code; nothing here ends the guest, so an unknown code (which no caller passes:
            // they call this only on failure) returns instead.
            handlers["?_Throw_C_error@std@@YAXH@Z"] = () =>
            {
                switch (_frame.SignedArg(0))
                {
                    case 1: case 2: ThrowCppError(6); break;
                    case 3: ThrowCppError(0); break;
                    case 4: ThrowCppError(1); break;
                    default: _frame.Return(0); break;
                }
            };

            // void _Throw_future_error(const error_code&): throw future_error(code). The category
            // pointer is the caller's own future_category().
            handlers["?_Throw_future_error@std@@YAXABVerror_code@1@@Z"] = () =>
            {
                long code = _frame.Arg(0);
                long self = NewObject(FutureError, string.Empty);
                if (code != 0)
                {
                    Write(self + 12, Read(code));
                    Write(self + 16, Read(code + 4));
                }

                Throw(FutureError, self);
            };

            // stdext::threads (Dinkumware's compatibility layer): lock_error and
            // thread_resource_error are runtime_errors with fixed messages.
            handlers["?_Throw_lock_error@threads@stdext@@YAXXZ"] = () =>
            {
                StdClass type = Class(["lock_error@threads@stdext", "runtime_error" + Std, "exception" + Std]);
                Throw(type, NewObject(type, "lock error"));
            };
            handlers["?_Throw_resource_error@threads@stdext@@YAXXZ"] = () =>
            {
                StdClass type = Class(["thread_resource_error@threads@stdext", "runtime_error" + Std, "exception" + Std]);
                Throw(type, NewObject(type, "thread resource error"));
            };
        }

        /// <summary>xthread's <c>_Mesg</c>/<c>_Code</c> tables, indexed by the <c>_*</c> constants of <c>&lt;xthreads.h&gt;</c>.</summary>
        private static readonly (int Errc, string Message)[] CppErrors =
        [
            (16, "device or resource busy"),          // _DEVICE_OR_RESOURCE_BUSY  errc::device_or_resource_busy (EBUSY)
            (22, "invalid argument"),                 // _INVALID_ARGUMENT         errc::invalid_argument (EINVAL)
            (3, "no such process"),                   // _NO_SUCH_PROCESS          errc::no_such_process (ESRCH)
            (12, "not enough memory"),                // _NOT_ENOUGH_MEMORY        errc::not_enough_memory (ENOMEM)
            (1, "operation not permitted"),           // _OPERATION_NOT_PERMITTED  errc::operation_not_permitted (EPERM)
            (36, "resource deadlock would occur"),    // _RESOURCE_DEADLOCK_WOULD_OCCUR (EDEADLK)
            (11, "resource unavailable try again"),   // _RESOURCE_UNAVAILABLE_TRY_AGAIN (EAGAIN)
        ];

        private void ThrowCppError(int index)
        {
            (int errc, string message) = index >= 0 && index < CppErrors.Length ? CppErrors[index] : (22, "invalid argument");
            long self = NewObject(SystemError, message);
            Write(self + 12, errc);
            Write(self + 16, GenericCategory);
            Throw(SystemError, self);
        }

        /// <summary>regex_error::_Stringify (VC11 &lt;regex&gt;), by <c>regex_constants::error_type</c>.</summary>
        public static string RegexMessage(int code) => code switch
        {
            0 => "regex_error(error_collate): The expression contained an invalid collating element name.",
            1 => "regex_error(error_ctype): The expression contained an invalid character class name.",
            2 => "regex_error(error_escape): The expression contained an invalid escaped character, or a trailing escape.",
            3 => "regex_error(error_backref): The expression contained an invalid back reference.",
            4 => "regex_error(error_brack): The expression contained mismatched [ and ].",
            5 => "regex_error(error_paren): The expression contained mismatched ( and ).",
            6 => "regex_error(error_brace): The expression contained mismatched { and }.",
            7 => "regex_error(error_badbrace): The expression contained an invalid range in a { expression }.",
            8 => "regex_error(error_range): The expression contained an invalid character range, such as [b-a] in most encodings.",
            9 => "regex_error(error_space): There was insufficient memory to convert the expression into a finite state machine.",
            10 => "regex_error(error_badrepeat): One of *?+{ was not preceded by a valid regular expression.",
            11 => "regex_error(error_complexity): The complexity of an attempted match against a regular expression exceeded a pre-set level.",
            12 => "regex_error(error_stack): There was insufficient memory to determine whether the regular expression could match the specified character sequence.",
            13 => "regex_error(error_parse)",
            14 => "regex_error(error_syntax)",
            _ => "regex_error",
        };

        // ------------------------------------------------------------------------------------
        // Error maps and categories
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// syserror.cpp's <c>_Sys_errtab</c>: <c>errc</c> value (MSVC's &lt;errno.h&gt;, POSIX
        /// supplement from 100) to the generic category's message.
        /// </summary>
        public static readonly IReadOnlyDictionary<int, string> SyserrorMessages = new Dictionary<int, string>
        {
            [102] = "address family not supported", [100] = "address in use", [101] = "address not available",
            [113] = "already connected", [7] = "argument list too long", [33] = "argument out of domain",
            [14] = "bad address", [9] = "bad file descriptor", [104] = "bad message", [32] = "broken pipe",
            [106] = "connection aborted", [103] = "connection already in progress", [107] = "connection refused",
            [108] = "connection reset", [18] = "cross device link", [109] = "destination address required",
            [16] = "device or resource busy", [41] = "directory not empty", [8] = "executable format error",
            [17] = "file exists", [27] = "file too large", [38] = "filename too long",
            [40] = "function not supported", [110] = "host unreachable", [111] = "identifier removed",
            [42] = "illegal byte sequence", [25] = "inappropriate io control operation", [4] = "interrupted",
            [22] = "invalid argument", [29] = "invalid seek", [5] = "io error", [21] = "is a directory",
            [115] = "message size", [116] = "network down", [117] = "network reset", [118] = "network unreachable",
            [119] = "no buffer space", [10] = "no child process", [121] = "no link", [39] = "no lock available",
            [120] = "no message available", [122] = "no message", [123] = "no protocol option",
            [28] = "no space on device", [124] = "no stream resources", [6] = "no such device or address",
            [19] = "no such device", [2] = "no such file or directory", [3] = "no such process",
            [20] = "not a directory", [128] = "not a socket", [125] = "not a stream", [126] = "not connected",
            [12] = "not enough memory", [129] = "not supported", [105] = "operation canceled",
            [112] = "operation in progress", [1] = "operation not permitted", [130] = "operation not supported",
            [140] = "operation would block", [133] = "owner dead", [13] = "permission denied",
            [134] = "protocol error", [135] = "protocol not supported", [30] = "read only file system",
            [36] = "resource deadlock would occur", [11] = "resource unavailable try again",
            [34] = "result out of range", [127] = "state not recoverable", [137] = "stream timeout",
            [139] = "text file busy", [138] = "timed out", [23] = "too many files open in system",
            [24] = "too many files open", [31] = "too many links", [114] = "too many symbolic link levels",
            [132] = "value too large", [136] = "wrong protocol type",
        };

        /// <summary>
        /// syserror.cpp's <c>_Win_errtab</c>: a Win32 / Winsock error code to the generic message
        /// that describes it - what <c>system_category().message()</c> answers.
        /// </summary>
        public static readonly IReadOnlyDictionary<int, string> WinerrorMessages = new Dictionary<int, string>
        {
            [5] = "permission denied",            // ERROR_ACCESS_DENIED
            [183] = "file exists",                // ERROR_ALREADY_EXISTS
            [20] = "no such device",              // ERROR_BAD_UNIT
            [111] = "filename too long",          // ERROR_BUFFER_OVERFLOW
            [170] = "device or resource busy",    // ERROR_BUSY
            [142] = "device or resource busy",    // ERROR_BUSY_DRIVE
            [82] = "permission denied",           // ERROR_CANNOT_MAKE
            [1011] = "io error",                  // ERROR_CANTOPEN
            [1012] = "io error",                  // ERROR_CANTREAD
            [1013] = "io error",                  // ERROR_CANTWRITE
            [16] = "permission denied",           // ERROR_CURRENT_DIRECTORY
            [55] = "no such device",              // ERROR_DEV_NOT_EXIST
            [2404] = "device or resource busy",   // ERROR_DEVICE_IN_USE
            [145] = "directory not empty",        // ERROR_DIR_NOT_EMPTY
            [267] = "invalid argument",           // ERROR_DIRECTORY
            [112] = "no space on device",         // ERROR_DISK_FULL
            [80] = "file exists",                 // ERROR_FILE_EXISTS
            [2] = "no such file or directory",    // ERROR_FILE_NOT_FOUND
            [39] = "no space on device",          // ERROR_HANDLE_DISK_FULL
            [12] = "permission denied",           // ERROR_INVALID_ACCESS
            [15] = "no such device",              // ERROR_INVALID_DRIVE
            [1] = "function not supported",       // ERROR_INVALID_FUNCTION
            [6] = "invalid argument",             // ERROR_INVALID_HANDLE
            [123] = "invalid argument",           // ERROR_INVALID_NAME
            [33] = "no lock available",           // ERROR_LOCK_VIOLATION
            [212] = "no lock available",          // ERROR_LOCKED
            [131] = "invalid argument",           // ERROR_NEGATIVE_SEEK
            [998] = "permission denied",          // ERROR_NOACCESS
            [8] = "not enough memory",            // ERROR_NOT_ENOUGH_MEMORY
            [21] = "resource unavailable try again", // ERROR_NOT_READY
            [17] = "cross device link",           // ERROR_NOT_SAME_DEVICE
            [110] = "io error",                   // ERROR_OPEN_FAILED
            [2401] = "device or resource busy",   // ERROR_OPEN_FILES
            [995] = "operation canceled",         // ERROR_OPERATION_ABORTED
            [14] = "not enough memory",           // ERROR_OUTOFMEMORY
            [3] = "no such file or directory",    // ERROR_PATH_NOT_FOUND
            [30] = "io error",                    // ERROR_READ_FAULT
            [1237] = "resource unavailable try again", // ERROR_RETRY
            [25] = "io error",                    // ERROR_SEEK
            [32] = "permission denied",           // ERROR_SHARING_VIOLATION
            [4] = "too many files open",          // ERROR_TOO_MANY_OPEN_FILES
            [29] = "io error",                    // ERROR_WRITE_FAULT
            [19] = "permission denied",           // ERROR_WRITE_PROTECT
            [10013] = "permission denied",        // WSAEACCES
            [10048] = "address in use",           // WSAEADDRINUSE
            [10049] = "address not available",    // WSAEADDRNOTAVAIL
            [10047] = "address family not supported", // WSAEAFNOSUPPORT
            [10037] = "connection already in progress", // WSAEALREADY
            [10009] = "bad file descriptor",      // WSAEBADF
            [10053] = "connection aborted",       // WSAECONNABORTED
            [10061] = "connection refused",       // WSAECONNREFUSED
            [10054] = "connection reset",         // WSAECONNRESET
            [10039] = "destination address required", // WSAEDESTADDRREQ
            [10014] = "bad address",              // WSAEFAULT
            [10065] = "host unreachable",         // WSAEHOSTUNREACH
            [10036] = "operation in progress",    // WSAEINPROGRESS
            [10004] = "interrupted",              // WSAEINTR
            [10022] = "invalid argument",         // WSAEINVAL
            [10056] = "already connected",        // WSAEISCONN
            [10024] = "too many files open",      // WSAEMFILE
            [10040] = "message size",             // WSAEMSGSIZE
            [10063] = "filename too long",        // WSAENAMETOOLONG
            [10050] = "network down",             // WSAENETDOWN
            [10052] = "network reset",            // WSAENETRESET
            [10051] = "network unreachable",      // WSAENETUNREACH
            [10055] = "no buffer space",          // WSAENOBUFS
            [10042] = "no protocol option",       // WSAENOPROTOOPT
            [10057] = "not connected",            // WSAENOTCONN
            [10038] = "not a socket",             // WSAENOTSOCK
            [10045] = "operation not supported",  // WSAEOPNOTSUPP
            [10043] = "protocol not supported",   // WSAEPROTONOSUPPORT
            [10041] = "wrong protocol type",      // WSAEPROTOTYPE
            [10060] = "timed out",                // WSAETIMEDOUT
            [10035] = "operation would block",    // WSAEWOULDBLOCK
        };

        /// <summary>
        /// future.cpp's messages, by <c>future_errc</c> (VC11 numbers it from 1:
        /// broken_promise, future_already_retrieved, promise_already_satisfied, no_state).
        /// </summary>
        public static string? FutureErrorMessage(int code) => code switch
        {
            1 => "broken promise",
            2 => "future already retrieved",
            3 => "promise already satisfied",
            4 => "no state",
            _ => null,
        };

        private static string FutureWhat(int code) => FutureErrorMessage(code) ?? "unknown future error";

        private long _genericCategory, _systemCategory, _iostreamCategory;

        /// <summary>MSVCP110's own <c>generic_category()</c> object.</summary>
        public long GenericCategory => _genericCategory != 0 ? _genericCategory : _genericCategory =
            Category("_Generic_error_category", "generic", v => SyserrorMessages.GetValueOrDefault(v, "unknown error"), (self, v) => (v, self));

        /// <summary>MSVCP110's own <c>system_category()</c>: Win32 codes, mapped to generic where one exists.</summary>
        public long SystemCategory => _systemCategory != 0 ? _systemCategory : _systemCategory =
            Category("_System_error_category", "system", v => WinerrorMessages.GetValueOrDefault(v, "unknown error"),
                (self, v) => SyserrorMessages.ContainsKey(v) ? (v, GenericCategory) : (v, self));

        /// <summary>MSVCP110's own <c>iostream_category()</c>: io_errc::stream (1) is "iostream stream error".</summary>
        public long IostreamCategory => _iostreamCategory != 0 ? _iostreamCategory : _iostreamCategory =
            Category("_Iostream_error_category", "iostream",
                v => v == 1 ? "iostream stream error" : SyserrorMessages.GetValueOrDefault(v, "unknown error"), (self, v) => (v, self));

        /// <summary>
        /// An <c>error_category</c> object: <c>{vfptr}</c>, with the VC11 virtuals in vtable order -
        /// destructor, <c>name</c>, <c>message</c>, <c>default_error_condition</c>, then the two
        /// <c>equivalent</c> overloads.
        /// </summary>
        /// <remarks>
        /// MSVC places an overload set at the first overload's position in reverse declaration
        /// order, so slot 4 should be <c>equivalent(const error_code&amp;, int)</c> and slot 5
        /// <c>equivalent(int, const error_condition&amp;)</c>. Both slots tell the two apart by
        /// their arguments instead of trusting that: an error value is a small integer and the
        /// other argument a reference into guest memory, so a call means the same thing whichever
        /// slot it arrives through.
        /// </remarks>
        private long Category(string className, string name, Func<int, string> message, Func<long, int, (int Value, long Category)> defaultCondition)
        {
            long self = _emulator.AllocateHeap(8);
            _emulator.WriteMemory(self, new byte[8]);
            string[] chain = className == "_Generic_error_category"
                ? [$".?AV{className}@std@@", ".?AVerror_category@std@@"]
                : [$".?AV{className}@std@@", ".?AV_Generic_error_category@std@@", ".?AVerror_category@std@@"];

            void Equivalent()
            {
                long me = _frame.Arg(0), a = _frame.Arg(1), b = _frame.Arg(2);
                static bool Small(long v) => v < 0x10000 || v >= 0xFFFF0000;
                bool codeFirst = !Small(a) && Small(b);
                if (codeFirst)
                {
                    // equivalent(const error_code& code, int errval): *this == code.category() && code.value() == errval
                    _frame.Return(Read(a + 4) == me && unchecked((int)Read(a)) == unchecked((int)(uint)b) ? 1 : 0);
                }
                else
                {
                    // equivalent(int errval, const error_condition& cond): default_error_condition(errval) == cond
                    (int value, long category) = defaultCondition(me, unchecked((int)(uint)a));
                    _frame.Return(b != 0 && unchecked((int)Read(b)) == value && Read(b + 4) == category ? 1 : 0);
                }
            }

            long vtable = Vtable(
                className,
                chain,
                ("`scalar deleting destructor'", () => _frame.Return(_frame.Arg(0))),   // a static object: never freed
                ("name", () => _frame.Return(StaticString(name))),
                // string message(int) const: this r0, hidden result r1, the value r2.
                ("message", () =>
                {
                    WriteStdString(_frame.Arg(1), message(unchecked((int)(uint)_frame.Arg(2))));
                    _frame.Return(_frame.Arg(1));
                }),
                // error_condition default_error_condition(int) const: this r0, hidden result r1.
                ("default_error_condition", () =>
                {
                    (int value, long category) = defaultCondition(_frame.Arg(0), unchecked((int)(uint)_frame.Arg(2)));
                    Write(_frame.Arg(1), value);
                    Write(_frame.Arg(1) + 4, category);
                    _frame.Return(_frame.Arg(1));
                }),
                ("equivalent", Equivalent),
                ("equivalent", Equivalent));
            Write(self, vtable);
            return self;
        }

        /// <summary>
        /// Constructs a <c>std::string</c> at <paramref name="at"/>: VC11's release layout
        /// (<c>_ITERATOR_DEBUG_LEVEL</c> 0), <c>{union {char _Buf[16]; char* _Ptr} _Bx; size_t _Mysize; size_t _Myres}</c>,
        /// short strings in place. A title built with debug iterators would have a proxy word in
        /// front; shipped titles are release builds.
        /// </summary>
        public void WriteStdString(long at, string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            byte[] layout = new byte[24];
            if (bytes.Length < 16)
            {
                bytes.CopyTo(layout, 0);
                BitConverter.GetBytes(15).CopyTo(layout, 20);
            }
            else
            {
                long buffer = _emulator.AllocateHeap(bytes.Length + 1);
                _emulator.WriteMemory(buffer, [.. bytes, 0]);
                BitConverter.GetBytes((uint)buffer).CopyTo(layout, 0);
                BitConverter.GetBytes(bytes.Length).CopyTo(layout, 20);
            }

            BitConverter.GetBytes(bytes.Length).CopyTo(layout, 16);
            _emulator.WriteMemory(at, layout);
        }

        private void RegisterErrorMaps(Dictionary<string, Action> handlers)
        {
            // const char* _Syserror_map(int): the generic message for an errc value, or NULL.
            handlers["?_Syserror_map@std@@YAPBDH@Z"] = () =>
                _frame.Return(SyserrorMessages.TryGetValue(_frame.SignedArg(0), out string? text) ? StaticString(text) : 0);

            // const char* _Winerror_map(int): the message for a Win32/Winsock error code, or NULL.
            handlers["?_Winerror_map@std@@YAPBDH@Z"] = () =>
                _frame.Return(WinerrorMessages.TryGetValue(_frame.SignedArg(0), out string? text) ? StaticString(text) : 0);

            // const char* _Future_error_map(int): the future_errc message, or NULL.
            handlers["?_Future_error_map@std@@YAPBDH@Z"] = () =>
                _frame.Return(FutureErrorMessage(_frame.SignedArg(0)) is { } text ? StaticString(text) : 0);

            // const char* _Get_future_error_what(int): future_error::what() for a code; never NULL.
            handlers["?_Get_future_error_what@std@@YAPBDH@Z"] = () => _frame.Return(StaticString(FutureWhat(_frame.SignedArg(0))));
        }

        // ------------------------------------------------------------------------------------
        // _Lockit: the library's global recursive locks, one per kind
        // ------------------------------------------------------------------------------------

        private readonly Dictionary<int, (int Owner, int Count)> _lockits = new();

        /// <summary>Who holds lock <paramref name="kind"/> and how deep, for the report.</summary>
        public (int Owner, int Count) LockitState(int kind) => _lockits.GetValueOrDefault(kind);

        private bool TryTakeLockit(int kind)
        {
            int me = _emulator.CurrentThreadId;
            (int owner, int count) = _lockits.GetValueOrDefault(kind);
            if (count == 0 || owner == me)
            {
                _lockits[kind] = (me, count + 1);
                return true;
            }

            return false;
        }

        private void ConstructLockit(long self, int kind)
        {
            if (self != 0)
            {
                Write(self, kind);
            }

            _frame.Return(self);
            TakeLockit(kind);
        }

        /// <summary>Takes lock <paramref name="kind"/>, blocking while another thread holds it. r0 must already hold the result.</summary>
        private void TakeLockit(int kind)
        {
            if (TryTakeLockit(kind))
            {
                return;
            }

            bool switched = _emulator.BlockCurrentThread(
                () => _lockits.GetValueOrDefault(kind).Count == 0,
                () => TryTakeLockit(kind),
                why: $"_Lockit {kind} held by thread {_lockits[kind].Owner}");
            if (!switched)
            {
                // Nobody else can run, so nobody will release it: take it rather than hang.
                _lockits[kind] = (_emulator.CurrentThreadId, 1);
            }
        }

        private void ReleaseLockit(int kind)
        {
            (int owner, int count) = _lockits.GetValueOrDefault(kind);
            if (count > 0)
            {
                _lockits[kind] = count == 1 ? (0, 0) : (owner, count - 1);
            }
        }

        private void RegisterLocks(Dictionary<string, Action> handlers)
        {
            // _Lockit::_Lockit(int kind): stores the kind in _Locktype and takes that lock - a
            // critical section, so recursive. Returns this.
            handlers["??0_Lockit@std@@QAA@H@Z"] = () => ConstructLockit(_frame.Arg(0), _frame.SignedArg(1));

            // _Lockit::_Lockit() is _LOCK_LOCALE (0).
            handlers["??0_Lockit@std@@QAA@XZ"] = () => ConstructLockit(_frame.Arg(0), 0);

            // _Lockit::~_Lockit(): releases the lock named by _Locktype.
            handlers["??1_Lockit@std@@QAA@XZ"] = () =>
            {
                long self = _frame.Arg(0);
                if (self != 0)
                {
                    ReleaseLockit(unchecked((int)Read(self)));
                }

                _frame.Return(0);
            };

            // static void _Lockit::_Lockit_ctor(int) / _Lockit_dtor(int): the same, without an object.
            handlers["?_Lockit_ctor@_Lockit@std@@SAXH@Z"] = () =>
            {
                _frame.Return(0);
                TakeLockit(_frame.SignedArg(0));
            };
            handlers["?_Lockit_dtor@_Lockit@std@@SAXH@Z"] = () =>
            {
                ReleaseLockit(_frame.SignedArg(0));
                _frame.Return(0);
            };

            // _Init_locks: the CRT's one-time set-up of those locks; there is nothing to set up.
            handlers["??0_Init_locks@std@@QAA@XZ"] = () => _frame.Return(_frame.Arg(0));
            handlers["??1_Init_locks@std@@QAA@XZ"] = () => _frame.Return(0);
        }

        // ------------------------------------------------------------------------------------
        // _Container_base12: the debug-iterator bookkeeping
        // ------------------------------------------------------------------------------------

        private void RegisterContainers(Dictionary<string, Action> handlers)
        {
            // Layout (VC11 <xutility>):
            //   _Container_base12 { _Container_proxy* _Myproxy; }
            //   _Container_proxy  { const _Container_base12* _Mycont; _Iterator_base12* _Myfirstiter; }
            //   _Iterator_base12  { _Container_proxy* _Myproxy; _Iterator_base12* _Mynextiter; }

            // _Container_base12(): _Myproxy(0). Without this the generic constructor stand-in
            // would plant a shaped vtable pointer where the proxy pointer belongs.
            handlers["??0_Container_base12@std@@QAA@XZ"] = () =>
            {
                long self = _frame.Arg(0);
                if (self != 0)
                {
                    Write(self, 0);
                }

                _frame.Return(self);
            };

            // _Container_base0 is the base with debug iterators off (_ITERATOR_DEBUG_LEVEL 0): its
            // _Orphan_all() and _Swap_all(_Container_base0&) are empty, as VC11's <xutility>.
            handlers["?_Orphan_all@_Container_base0@std@@QAAXXZ"] = () => _frame.Return(0);
            handlers["?_Swap_all@_Container_base0@std@@QAAXAAU12@@Z"] = () => _frame.Return(0);

            // ~_Container_base12(): _Orphan_all().
            handlers["??1_Container_base12@std@@QAA@XZ"] = () =>
            {
                OrphanAll(_frame.Arg(0));
                _frame.Return(0);
            };

            // void _Orphan_all(): detaches every iterator registered with this container's
            // proxy, so each now reports no container, and empties the list.
            handlers["?_Orphan_all@_Container_base12@std@@QAAXXZ"] = () =>
            {
                OrphanAll(_frame.Arg(0));
                _frame.Return(0);
            };

            // void _Swap_all(_Container_base12& right): exchanges the two proxies and points
            // each proxy back at its new owner, so iterators follow the elements they refer to.
            handlers["?_Swap_all@_Container_base12@std@@QAAXAAU12@@Z"] = () =>
            {
                long self = _frame.Arg(0), right = _frame.Arg(1);
                if (self != 0 && right != 0)
                {
                    long mine = Read(self), theirs = Read(right);
                    Write(self, theirs);
                    Write(right, mine);
                    if (theirs != 0)
                    {
                        Write(theirs, self);
                    }

                    if (mine != 0)
                    {
                        Write(mine, right);
                    }
                }

                _frame.Return(0);
            };
        }

        /// <summary>Walks the proxy's iterator list clearing each <c>_Myproxy</c>, then empties it. Bounded against a cycle.</summary>
        public void OrphanAll(long container)
        {
            if (container == 0)
            {
                return;
            }

            long proxy = Read(container);
            if (proxy == 0)
            {
                return;
            }

            HashSet<long> seen = new();
            for (long iterator = Read(proxy + 4); iterator != 0 && seen.Add(iterator); iterator = Read(iterator + 4))
            {
                Write(iterator, 0);
            }

            Write(proxy + 4, 0);
        }

        // ------------------------------------------------------------------------------------
        // exception_ptr (MSVCR110's __ExceptionPtr* functions underneath std::exception_ptr)
        // ------------------------------------------------------------------------------------

        /// <summary>One captured exception. <see cref="Owned"/>: a copy this record must destroy.</summary>
        private sealed class Captured
        {
            public required long Object { get; init; }

            public required IReadOnlyList<string> Types { get; init; }

            public long ThrowInfo { get; init; }

            public bool Owned { get; init; }

            public int References { get; set; } = 1;
        }

        /// <summary>
        /// Live captures, by the guest address that identifies them. An <c>exception_ptr</c> is
        /// two words; both hold that address (VC11 keeps a shared_ptr there, which this replaces
        /// with a host-side reference count). A null pointer is two zeros.
        /// </summary>
        private readonly Dictionary<long, Captured> _captured = new();

        /// <summary>Live exception_ptr captures, for the report and the tests.</summary>
        public int LiveExceptionPointers => _captured.Count;

        private void Store(long pointer, long record)
        {
            if (pointer != 0)
            {
                Write(pointer, record);
                Write(pointer + 4, record);
            }
        }

        private long RecordOf(long pointer) => pointer == 0 ? 0 : Read(pointer);

        private void AddReference(long record)
        {
            if (_captured.TryGetValue(record, out Captured? capture))
            {
                capture.References++;
            }
        }

        /// <summary>
        /// Drops a reference. The last one destroys an owned copy - its ThrowInfo's destructor,
        /// which is guest code, then the block - and continues at <paramref name="returnTo"/>.
        /// Returns whether guest code was entered (the handler must then do nothing more).
        /// </summary>
        private bool Release(long record, long returnTo)
        {
            if (!_captured.TryGetValue(record, out Captured? capture) || --capture.References > 0)
            {
                return false;
            }

            _captured.Remove(record);
            _emulator.FreeHeap(record);
            if (!capture.Owned)
            {
                return false;
            }

            long destructor = capture.ThrowInfo == 0 ? 0 : Read(capture.ThrowInfo + 4);
            if (destructor == 0)
            {
                _emulator.FreeHeap(capture.Object);
                return false;
            }

            _emulator.CallEmulated("exception_ptr destroy", (_emulator.ImageBase + destructor) | 1, [capture.Object], onReturn: () =>
            {
                _emulator.FreeHeap(capture.Object);
                _frame.Return(0);
                _emulator.ContinueAt(returnTo);
            }, recycle: true);
            return true;
        }

        /// <summary>
        /// Captures an exception into a new record and stores it at <paramref name="pointer"/>,
        /// running <paramref name="then"/> afterwards. A guest throw's object lives in the
        /// throwing frame, so it is copied - with the ThrowInfo's copy constructor when it has
        /// one, bitwise when it is simple - as the CRT does. A host-built object is on the heap
        /// and never destroyed by a catch, so it is shared.
        /// </summary>
        private void Capture(long pointer, long exceptionObject, IReadOnlyList<string> types, long throwInfo, Action then)
        {
            void Finish(long copy, bool owned)
            {
                long record = _emulator.AllocateHeap(8);
                _captured[record] = new Captured { Object = copy, Types = types, ThrowInfo = throwInfo, Owned = owned };
                Store(pointer, record);
                then();
            }

            long catchable = throwInfo == 0 ? 0 : FirstCatchableType(throwInfo);
            if (catchable == 0 || exceptionObject == 0)
            {
                Finish(exceptionObject, owned: false);
                return;
            }

            long properties = Read(catchable);
            long size = Math.Max(1, Read(catchable + 20));
            long copyFunction = Read(catchable + 24);
            long copy = _emulator.AllocateHeap(size);

            if (copyFunction == 0)
            {
                _emulator.WriteMemory(copy, _emulator.ReadMemory(exceptionObject, (int)size));
                Finish(copy, owned: true);
                return;
            }

            long returnTo = _emulator.ReturnAddress;
            long[] arguments = (properties & 4) != 0 ? [copy, exceptionObject, 1] : [copy, exceptionObject];
            _emulator.CallEmulated("exception_ptr copy constructor", (_emulator.ImageBase + copyFunction) | 1, arguments, onReturn: () =>
            {
                Finish(copy, owned: true);
                _emulator.ContinueAt(returnTo);
            }, recycle: true);
        }

        /// <summary>The address of a ThrowInfo's first (most derived) CatchableType, or 0.</summary>
        private long FirstCatchableType(long throwInfo)
        {
            long arrayRva = Read(throwInfo + 12);
            if (arrayRva == 0)
            {
                return 0;
            }

            long array = _emulator.ImageBase + arrayRva;
            if (Read(array) == 0)
            {
                return 0;
            }

            long rva = Read(array + 4);
            return rva == 0 ? 0 : _emulator.ImageBase + rva;
        }

        private void RegisterExceptionPointers(Dictionary<string, Action> handlers)
        {
            // void __ExceptionPtrCreate(void*): a null exception_ptr.
            handlers["?__ExceptionPtrCreate@@YAXPAX@Z"] = () =>
            {
                Store(_frame.Arg(0), 0);
                _frame.Return(0);
            };

            // void __ExceptionPtrDestroy(void*)
            handlers["?__ExceptionPtrDestroy@@YAXPAX@Z"] = () =>
            {
                long pointer = _frame.Arg(0);
                long record = RecordOf(pointer);
                _frame.Return(0);
                Release(record, _emulator.ReturnAddress);
            };

            // void __ExceptionPtrCopy(void* dst, const void* src): copy-construct.
            handlers["?__ExceptionPtrCopy@@YAXPAXPBX@Z"] = () =>
            {
                long record = RecordOf(_frame.Arg(1));
                AddReference(record);
                Store(_frame.Arg(0), record);
                _frame.Return(0);
            };

            // void __ExceptionPtrAssign(void* dst, const void* src)
            handlers["?__ExceptionPtrAssign@@YAXPAXPBX@Z"] = () =>
            {
                long destination = _frame.Arg(0);
                long record = RecordOf(_frame.Arg(1));
                long old = RecordOf(destination);
                AddReference(record);
                Store(destination, record);
                _frame.Return(0);
                Release(old, _emulator.ReturnAddress);
            };

            // bool __ExceptionPtrCompare(const void*, const void*)
            handlers["?__ExceptionPtrCompare@@YA_NPBX0@Z"] = () =>
                _frame.Return(RecordOf(_frame.Arg(0)) == RecordOf(_frame.Arg(1)) ? 1 : 0);

            // bool __ExceptionPtrToBool(const void*)
            handlers["?__ExceptionPtrToBool@@YA_NPBX@Z"] = () => _frame.Return(RecordOf(_frame.Arg(0)) != 0 ? 1 : 0);

            // void __ExceptionPtrSwap(void*, void*)
            handlers["?__ExceptionPtrSwap@@YAXPAX0@Z"] = () =>
            {
                long a = RecordOf(_frame.Arg(0)), b = RecordOf(_frame.Arg(1));
                Store(_frame.Arg(0), b);
                Store(_frame.Arg(1), a);
                _frame.Return(0);
            };

            // void __ExceptionPtrCurrentException(void*): what the innermost running catch is
            // handling, or null outside any catch.
            handlers["?__ExceptionPtrCurrentException@@YAXPAX@Z"] = () =>
            {
                long pointer = _frame.Arg(0);
                _frame.Return(0);
                if (_handled() is not { } handled)
                {
                    Store(pointer, 0);
                    return;
                }

                Capture(pointer, handled.Object, handled.CatchableTypes, handled.ThrowInfo, then: () => _frame.Return(0));
            };

            // void __ExceptionPtrCopyException(void* ptr, const void* object, const void* throwInfo):
            // make_exception_ptr - a copy of the object, catchable as the ThrowInfo says.
            handlers["?__ExceptionPtrCopyException@@YAXPAXPBX1@Z"] = () =>
            {
                long pointer = _frame.Arg(0), exceptionObject = _frame.Arg(1), throwInfo = _frame.Arg(2);
                ThrownException described = _emulator.ExceptionModel.ReadThrow(exceptionObject, throwInfo);
                // make_exception_ptr hands over a freshly created (null) pointer. Should one ever
                // hold a capture, its reference is dropped; an owned object that needs its guest
                // destructor run is then left to leak rather than entered from the middle of this.
                long old = RecordOf(pointer);
                if (_captured.TryGetValue(old, out Captured? previous) && --previous.References <= 0)
                {
                    _captured.Remove(old);
                }

                _frame.Return(0);
                Capture(pointer, exceptionObject, described.CatchableTypes, throwInfo, then: () => _frame.Return(0));
            };

            // void __ExceptionPtrRethrow(const void*): throws the captured object again.
            handlers["?__ExceptionPtrRethrow@@YAXPBX@Z"] = () => Rethrow(RecordOf(_frame.Arg(0)));

            // void _Rethrow_future_exception(exception_ptr): rethrow_exception(ptr). The pointer
            // arrives by value; it is recognised whether the two words came in r0:r1 or behind a
            // reference to a caller-made temporary, since a capture's address is unmistakable.
            handlers["?_Rethrow_future_exception@std@@YAXVexception_ptr@1@@Z"] = () =>
            {
                long first = _frame.Arg(0);
                long record = _captured.ContainsKey(first) ? first
                    : first != 0 && _emulator.Cpu.IsMapped(first) && _captured.ContainsKey(Read(first)) ? Read(first)
                    : 0;
                Rethrow(record);
            };
        }

        /// <summary>
        /// rethrow_exception: the captured object, with its own catchable types. A null
        /// exception_ptr throws std::bad_exception, which is what MSVCR110 raises for it.
        /// </summary>
        private void Rethrow(long record)
        {
            if (!_captured.TryGetValue(record, out Captured? capture))
            {
                Throw(BadException, NewObject(BadException, "bad exception", owned: false));
                return;
            }

            // A host-raised Platform exception has no ThrowInfo and was captured as its handle.
            ThrownException again = new(capture.Object, capture.Types) { ThrowInfo = capture.ThrowInfo };
            _throw(again with { ObjectIsHandle = capture.ThrowInfo == 0 && again.IsPointer });
        }

        // ------------------------------------------------------------------------------------
        // The rest
        // ------------------------------------------------------------------------------------

        private long _newHandler;

        private void RegisterMisc(Dictionary<string, Action> handlers)
        {
            // _Yarn<char> { char* _Myptr; char _Nul; } - a heap-owned C string.
            // _Yarn& operator=(const char*): frees the old copy, then copies the new one (NULL
            // leaves it empty). Assigning the string it already holds is a no-op.
            handlers["??4?$_Yarn@D@std@@QAAAAV01@PBD@Z"] = () =>
            {
                long self = _frame.Arg(0), text = _frame.Arg(1);
                if (self != 0 && text != Read(self))
                {
                    _emulator.FreeHeap(Read(self));
                    Write(self, 0);
                    if (text != 0)
                    {
                        Write(self, HeapString(_frame.ReadNarrowString(text)));
                    }
                }

                _frame.Return(self);
            };

            // const char* c_str() const: the copy, or the empty _Nul when there is none.
            handlers["?c_str@?$_Yarn@D@std@@QBAPBDXZ"] = () =>
            {
                long self = _frame.Arg(0);
                long text = self == 0 ? 0 : Read(self);
                if (text == 0 && self != 0)
                {
                    _emulator.WriteMemory(self + 4, [0]);
                }

                _frame.Return(text != 0 ? text : self + 4);
            };

            // _Yarn<char>() and ~_Yarn(), should a title import them.
            handlers["??0?$_Yarn@D@std@@QAA@XZ"] = () =>
            {
                long self = _frame.Arg(0);
                if (self != 0)
                {
                    Write(self, 0);
                    _emulator.WriteMemory(self + 4, [0]);
                }

                _frame.Return(self);
            };
            handlers["??1?$_Yarn@D@std@@QAA@XZ"] = () =>
            {
                long self = _frame.Arg(0);
                if (self != 0)
                {
                    _emulator.FreeHeap(Read(self));
                    Write(self, 0);
                }

                _frame.Return(0);
            };

            // unsigned int _Random_device(): random_device's source, a cryptographic random word
            // (rand_s on the device).
            handlers["?_Random_device@std@@YAIXZ"] = () =>
                _frame.Return(BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)));

            // new_handler set_new_handler(new_handler): installs the handler, returns the previous
            // one. The handler is stored only: allocation here never retries through it.
            handlers["?set_new_handler@std@@YAP6AXXZP6AXXZ@Z"] = () =>
            {
                long previous = _newHandler;
                _newHandler = _frame.Arg(0);
                _frame.Return(previous);
            };        }
    }
}
