using System.Security.Cryptography;
using System.Text;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// MSVCR110's runtime proper - floating point, memory, time, process control, C++ EH and RTTI
    /// support, threads - to the VC11 CRT's documented contracts. The string, character,
    /// conversion and printf families live elsewhere; stdio is in <c>FileLibrary.Crt.cs</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ending the process.</b> Where the CRT ends the process - <c>_amsg_exit</c>, a failed
    /// assert, <c>_invoke_watson</c>, the default invalid-parameter handler - the guest is stopped
    /// the way <c>exit</c> and <c>abort</c> already stop it, with the reason recorded in
    /// <see cref="CrtTerminations"/> so the report says why. That is the game failing, as it would
    /// on a phone; nothing here stops a guest for any other reason.
    /// </para>
    /// <para>
    /// <b>Time.</b> This runtime's clock is UTC with no daylight saving: localtime and gmtime agree
    /// (see CrtLibrary), so <c>_timezone</c> and <c>_daylight</c> are 0 and both zone names are
    /// "Coordinated Universal Time", which is what the CRT reads from a phone set to UTC.
    /// </para>
    /// </remarks>
    public sealed partial class HostStubs
    {
        /// <summary>Every time the CRT ended the process, with why: the first one is the run's stop reason.</summary>
        public List<string> CrtTerminations { get; } = new();

        /// <summary>Calls to entry points that only the OS exception dispatcher calls, which this host never runs.</summary>
        public List<string> CrtUnreachableCalls { get; } = new();

        private long _invalidParameterHandler;
        private long _userMathErrorHandler;
        private readonly Dictionary<int, long> _signalActions = new();
        private readonly Dictionary<int, int> _threadLocaleMode = new();
        private long _localeNameC;
        private long _timezoneCell, _daylightCell, _sysNerrCell;
        private long _ctimeBuffer, _wctimeBuffer, _asctimeBuffer;
        private long _stdExceptionVtable;
        private long _unknownExceptionText;

        private const int Einval = 22, Erange = 34, Enomem = 12, Enoent = 2, Eilseq = 42;

        private void RegisterCrtRuntime()
        {
            Files.CrtInvalidParameter = CrtInvalidParameter;
            RegisterCrtProcess();
            RegisterCrtFloatingPoint();
            RegisterCrtMemory();
            RegisterCrtTime();
            RegisterCrtExceptionSupport();
            RegisterCrtExceptionPointers();
            RegisterCrtThreads();
        }

        private void SetErrno(int value) => Files.CrtSetErrno(value);

        /// <summary>Ends the guest as the CRT ends the process, and says why.</summary>
        private void CrtTerminate(string reason)
        {
            if (CrtTerminations.Count < 64)
            {
                CrtTerminations.Add(reason);
            }

            _emulator.Stop(reason);
        }

        /// <summary>
        /// The CRT's invalid-parameter path, entered with the function's failure value already in
        /// r0 (and r1). The guest's handler, if it set one with _set_invalid_parameter_handler, is
        /// called with five nulls and the function then returns its failure value; otherwise the
        /// default handler ends the process (_invoke_watson), as in a release CRT.
        /// </summary>
        private void CrtInvalidParameter(string function, long result, int errno)
        {
            if (_invalidParameterHandler == 0)
            {
                CrtTerminate($"invalid parameter passed to {function} (errno {errno}); the CRT's default handler ends the process");
                return;
            }

            long r0 = _emulator.ReadRegister(Arm.UC_ARM_REG_R0), r1 = _emulator.ReadRegister(Arm.UC_ARM_REG_R1);
            long returnTo = _emulator.ReturnAddress;
            _emulator.CallEmulated("invalid parameter handler", _invalidParameterHandler, [0, 0, 0, 0, 0], () =>
            {
                _emulator.WriteRegister(Arm.UC_ARM_REG_R0, r0);
                _emulator.WriteRegister(Arm.UC_ARM_REG_R1, r1);
                _emulator.ContinueAt(returnTo);
            }, recycle: true);
        }

        private void InvalidParameter(string function, long result, int errno)
        {
            SetErrno(errno);
            Return(result);
            CrtInvalidParameter(function, result, errno);
        }

        /// <summary>stdout and stderr: the debug log printf writes to, bounded as it bounds it.</summary>
        private void PrintToLog(string text)
        {
            if (Extras.Printed.Count < 200)
            {
                Extras.Printed.Add(text);
            }
        }

        private string Narrow(int index) => _frame.ReadNarrowString(Arg(index), 4096);

        private string Wide(int index) => Arg(index) == 0 ? string.Empty : _emulator.ReadUtf16String(Arg(index), 4096);

        private long StaticNarrow(ref long cell, string text)
        {
            if (cell == 0)
            {
                cell = _emulator.AllocateHeap(text.Length + 1);
                _frame.WriteNarrowString(cell, text);
            }

            return cell;
        }

        private long StaticCell(ref long cell, int size = 8)
        {
            if (cell == 0)
            {
                cell = _emulator.AllocateHeap(size);
                _emulator.WriteMemory(cell, new byte[size]);
            }

            return cell;
        }

        // =======================================================================================
        // Process, environment, locale
        // =======================================================================================

        private void RegisterCrtProcess()
        {
            // _invalid_parameter_handler _set_invalid_parameter_handler(_invalid_parameter_handler)
            _handlers["_set_invalid_parameter_handler"] = () =>
            {
                long previous = _invalidParameterHandler;
                _invalidParameterHandler = Arg(0);
                Return(previous);
            };

            // void _invalid_parameter_noinfo_noreturn(void): the handler, then _invoke_watson.
            _handlers["_invalid_parameter_noinfo_noreturn"] = () =>
            {
                if (_invalidParameterHandler == 0)
                {
                    CrtTerminate("_invalid_parameter_noinfo_noreturn: an invalid parameter, and the CRT's default handler ends the process");
                    return;
                }

                _emulator.CallEmulated("invalid parameter handler", _invalidParameterHandler, [0, 0, 0, 0, 0],
                    () => CrtTerminate("_invalid_parameter_noinfo_noreturn: the invalid-parameter handler returned, and _invoke_watson ends the process"),
                    recycle: true);
            };

            _handlers["_invoke_watson"] = () =>
                CrtTerminate($"_invoke_watson({(Arg(0) == 0 ? "" : Wide(0))}) - the CRT's fail-fast for an invalid parameter");

            // void _amsg_exit(int rterrnum): "runtime error R60nn", then the process ends.
            _handlers["_amsg_exit"] = () => CrtTerminate($"_amsg_exit: runtime error R60{SignedArg(0):D2}");

            // void _wassert(const wchar_t* message, const wchar_t* file, unsigned line): prints, then abort().
            _handlers["_wassert"] = () =>
            {
                string message = $"Assertion failed: {Wide(0)}, file {Wide(1)}, line {Arg(2)}";
                PrintToLog(message);
                CrtTerminate($"_wassert: {message}");
            };

            // void _cexit(void): runs the atexit table and flushes streams, without ending the process.
            // _onexit/__dllonexit here register nothing, and every stream writes straight through, so
            // there is nothing to run and nothing to flush.
            _handlers["_cexit"] = () => Return(0);

            // void __setusermatherr(_HANDLE_MATH_ERROR): the math functions here report through
            // errno and never call _matherr, so the handler is kept and never called.
            _handlers["__setusermatherr"] = () =>
            {
                _userMathErrorHandler = Arg(0);
                Return(0);
            };

            _handlers["signal"] = Signal;

            // int __CppXcptFilter(unsigned long code, PEXCEPTION_POINTERS) and _XcptFilter: the
            // __except filters CRT start-up wraps main in. Only SEH dispatch calls them, which this
            // host never performs; answered as they answer for an exception with no signal action.
            _handlers["__CppXcptFilter"] = () => XcptFilter("__CppXcptFilter");
            _handlers["_XcptFilter"] = () => XcptFilter("_XcptFilter");

            // char* getenv(const char*): a WP8 app has no environment block (its envp is { NULL }).
            _handlers["getenv"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("getenv", 0, Einval);
                    return;
                }

                Return(0);
            };

            // int system(const char*): an app cannot start a command processor. system(NULL) asks
            // whether there is one: 0, no.
            _handlers["system"] = () =>
            {
                if (Arg(0) == 0)
                {
                    Return(0);
                    return;
                }

                SetErrno(Enoent);
                Return(0xFFFFFFFFL);
            };

            // FILE* _popen(const char* command, const char* mode) / int _pclose(FILE*): no processes.
            _handlers["_popen"] = () =>
            {
                string mode = Arg(1) == 0 ? string.Empty : Narrow(1);
                if (Arg(0) == 0 || Arg(1) == 0 || mode.Length == 0 || mode[0] is not ('r' or 'w') ||
                    mode[1..].Any(c => c is not ('t' or 'b')) || mode.Length > 2)
                {
                    InvalidParameter("_popen", 0, Einval);
                    return;
                }

                SetErrno(Enoent);
                Return(0);
            };
            _handlers["_pclose"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("_pclose", 0xFFFFFFFFL, Einval);
                    return;
                }

                SetErrno(Einval);
                Return(0xFFFFFFFFL);
            };

            // An app has no console: _getch and getchar read nothing.
            _handlers["_getch"] = () => Return(0xFFFFFFFFL);
            _handlers["getchar"] = () => Return(0xFFFFFFFFL);

            // int puts(const char*): stdout here is the debug log, as printf treats it.
            _handlers["puts"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("puts", 0xFFFFFFFFL, Einval);
                    return;
                }

                PrintToLog(Narrow(0));
                Return(0);
            };

            // void perror(const char*): "s: message" for errno, to stderr (the debug log).
            _handlers["perror"] = () =>
            {
                string prefix = Arg(0) == 0 ? string.Empty : Narrow(0);
                string message = ErrnoMessage(Files.CrtGetErrno());
                PrintToLog(prefix.Length > 0 ? $"{prefix}: {message}" : message);
                Return(0);
            };

            // errno_t _set_errno(int)
            _handlers["_set_errno"] = () =>
            {
                SetErrno(SignedArg(0));
                Return(0);
            };

            // int* __sys_nerr(void): the number of entries in _sys_errlist, 43 in VC11.
            _handlers["__sys_nerr"] = () =>
            {
                long cell = StaticCell(ref _sysNerrCell, 4);
                _emulator.WriteUInt32(cell, (uint)ErrnoMessages.Length);
                Return(cell);
            };

            // char* setlocale(int category, const char* locale): only the "C" locale exists here.
            // "" asks for the native locale, which this runtime defines as "C"; any other name is not
            // available and answers NULL, the documented failure.
            _handlers["setlocale"] = () =>
            {
                int category = SignedArg(0);
                if (category is < 0 or > 5)
                {
                    InvalidParameter("setlocale", 0, Einval);
                    return;
                }

                string? requested = Arg(1) == 0 ? null : Narrow(1);
                Return(requested is null or "" or "C" ? StaticNarrow(ref _localeNameC, "C") : 0);
            };

            // int _configthreadlocale(int type): the previous setting, per thread.
            _handlers["_configthreadlocale"] = () =>
            {
                const int enable = 1, disable = 2;
                int type = SignedArg(0);
                int thread = _emulator.CurrentThreadId;
                int previous = _threadLocaleMode.GetValueOrDefault(thread, disable);
                switch (type)
                {
                    case 0: break;
                    case enable or disable: _threadLocaleMode[thread] = type; break;
                    case -1: _threadLocaleMode[thread] = disable; break;
                    default:
                        InvalidParameter("_configthreadlocale", 0xFFFFFFFFL, Einval);
                        return;
                }

                Return(previous);
            };

            // int __lconv_init(void): the CRT's own locale start-up; the "C" lconv needs none.
            _handlers["__lconv_init"] = () => Return(0);

            // errno_t rand_s(unsigned int*): cryptographically random, as the OS's RtlGenRandom.
            _handlers["rand_s"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("rand_s", Einval, Einval);
                    return;
                }

                _emulator.WriteUInt32(Arg(0), BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)));
                Return(0);
            };

            _handlers["bsearch"] = Bsearch;
        }

        private static readonly string[] ErrnoMessages =
        [
            "No error", "Operation not permitted", "No such file or directory", "No such process",
            "Interrupted function call", "Input/output error", "No such device or address", "Arg list too long",
            "Exec format error", "Bad file descriptor", "No child processes", "Resource temporarily unavailable",
            "Not enough space", "Permission denied", "Bad address", "Unknown error", "Resource device", "File exists",
            "Improper link", "No such device", "Not a directory", "Is a directory", "Invalid argument",
            "Too many open files in system", "Too many open files", "Inappropriate I/O control operation",
            "Unknown error", "File too large", "No space left on device", "Invalid seek", "Read-only file system",
            "Too many links", "Broken pipe", "Domain error", "Result too large", "Unknown error",
            "Resource deadlock avoided", "Unknown error", "Filename too long", "No locks available",
            "Function not implemented", "Directory not empty", "Illegal byte sequence",
        ];

        private static string ErrnoMessage(int errno) => errno >= 0 && errno < ErrnoMessages.Length ? ErrnoMessages[errno] : "Unknown error";

        /// <summary>
        /// void (*signal(int sig, void (*func)(int)))(int): the previous action. SIGINT 2, SIGILL 4,
        /// SIGFPE 8, SIGSEGV 11, SIGTERM 15, SIGBREAK 21, SIGABRT 22 (and its old number 6). SIG_ACK
        /// and SIG_SGE are not actions a program may set. The actions are kept; this host raises no
        /// signals of its own.
        /// </summary>
        private void Signal()
        {
            const long sigErr = 0xFFFFFFFFL;
            int signal = SignedArg(0);
            long action = Arg(1);
            if (signal == 6)
            {
                signal = 22;
            }

            if (signal is not (2 or 4 or 8 or 11 or 15 or 21 or 22) || action is 3 or 4)
            {
                InvalidParameter("signal", sigErr, Einval);
                return;
            }

            long previous = _signalActions.GetValueOrDefault(signal, 0);
            if (action != 2)   // SIG_GET only reads
            {
                _signalActions[signal] = action;
            }

            Return(previous);
        }

        /// <summary>
        /// _XcptFilter's answer for code (arg 0): a signal action of SIG_IGN continues execution
        /// (-1); none, or SIG_DFL, continues the search (0) - the unhandled-exception path. A user
        /// handler would be reset and called with the signal; SEH never reaches here, so that path
        /// is recorded rather than built.
        /// </summary>
        private void XcptFilter(string name)
        {
            long code = Arg(0) & 0xFFFFFFFFL;
            if (CrtUnreachableCalls.Count < 64)
            {
                CrtUnreachableCalls.Add($"{name}(0x{code:X8})");
            }

            int signal = code switch
            {
                0xC0000005 => 11,
                0xC000001D or 0xC0000096 => 4,
                >= 0xC000008D and <= 0xC0000093 or 0xC00002B4 or 0xC00002B5 => 8,
                _ => 0,
            };

            long action = signal == 0 ? 0 : _signalActions.GetValueOrDefault(signal, 0);
            Return(action == 1 ? 0xFFFFFFFFL : 0);
        }

        /// <summary>
        /// void* bsearch(const void* key, const void* base, size_t num, size_t width,
        /// int (*compare)(const void* key, const void* element)): binary search, asking the
        /// image's comparator through a callback at each step.
        /// </summary>
        private void Bsearch()
        {
            long key = Arg(0), array = Arg(1), count = Arg(2), width = Arg(3), compare = Arg(4);
            if ((array == 0 && count != 0) || width == 0 || compare == 0)
            {
                InvalidParameter("bsearch", 0, Einval);
                return;
            }

            long returnTo = _emulator.ReturnAddress;
            void Step(long low, long high)
            {
                if (low > high)
                {
                    Return(0);
                    _emulator.ContinueAt(returnTo);
                    return;
                }

                long middle = low + ((high - low) / 2);
                long element = array + (middle * width);
                _emulator.CallEmulated("bsearch comparator", compare, [key, element], () =>
                {
                    int order = SignedArg(0);
                    if (order == 0)
                    {
                        Return(element);
                        _emulator.ContinueAt(returnTo);
                    }
                    else if (order < 0)
                    {
                        Step(low, middle - 1);
                    }
                    else
                    {
                        Step(middle + 1, high);
                    }
                }, recycle: true);
            }

            if (count == 0)
            {
                Return(0);
                return;
            }

            Step(0, count - 1);
        }

        private int SignedArg(int index) => _frame.SignedArg(index);

        // =======================================================================================
        // Floating point: the VFP's FPSCR, in the CRT's control and status words
        // =======================================================================================

        private const uint EmInexact = 0x1, EmUnderflow = 0x2, EmOverflow = 0x4, EmZeroDivide = 0x8, EmInvalid = 0x10, EmDenormal = 0x80000;
        private const uint McwEm = 0x8001F, McwRc = 0x300, McwDn = 0x03000000, McwPc = 0x30000, McwIc = 0x40000;
        private const uint RcNear = 0, RcDown = 0x100, RcUp = 0x200, RcChop = 0x300, DnFlush = 0x01000000;
        private const uint SwInexact = 0x1, SwUnderflow = 0x2, SwOverflow = 0x4, SwZeroDivide = 0x8, SwInvalid = 0x10, SwDenormal = 0x80000;
        private const uint FpscrFlags = 0x9F, FpscrEnables = 0x9F00, FpscrRMode = 0x00C00000, FpscrFz = 0x01000000, FpscrDn = 0x02000000;

        private uint Fpscr
        {
            get => (uint)_emulator.Cpu.RegRead(Arm.UC_ARM_REG_FPSCR);
            set => _emulator.Cpu.RegWrite(Arm.UC_ARM_REG_FPSCR, value);
        }

        /// <summary>
        /// The control word the FPSCR describes. The trap-enable bits are never set (see
        /// <see cref="ApplyControl"/>), so every exception reads as masked - as on the phones WP8
        /// shipped on, whose VFP has no trapping and reads those bits as zero.
        /// </summary>
        private static uint ControlFrom(uint fpscr)
        {
            uint em = McwEm;
            if ((fpscr & (1 << 8)) != 0) em &= ~EmInvalid;
            if ((fpscr & (1 << 9)) != 0) em &= ~EmZeroDivide;
            if ((fpscr & (1 << 10)) != 0) em &= ~EmOverflow;
            if ((fpscr & (1 << 11)) != 0) em &= ~EmUnderflow;
            if ((fpscr & (1 << 12)) != 0) em &= ~EmInexact;
            if ((fpscr & (1 << 15)) != 0) em &= ~EmDenormal;

            uint rc = ((fpscr >> 22) & 3) switch
            {
                1 => RcUp,      // RP
                2 => RcDown,    // RM
                3 => RcChop,    // RZ
                _ => RcNear,
            };

            return em | rc | ((fpscr & FpscrFz) != 0 ? DnFlush : 0);
        }

        /// <summary>
        /// Writes rounding and denormal control into the FPSCR. Exception unmasking is accepted and
        /// not applied: the trap-enable bits are RAZ/WI on the cores WP8 ran on, and the CPU behind
        /// this host does not deliver floating-point traps either.
        /// </summary>
        private static uint ApplyControl(uint fpscr, uint control)
        {
            uint mode = (control & McwRc) switch
            {
                RcUp => 1u,
                RcDown => 2u,
                RcChop => 3u,
                _ => 0u,
            };

            fpscr = (fpscr & ~(FpscrRMode | FpscrFz | FpscrEnables)) | (mode << 22);
            return (control & McwDn) != 0 ? fpscr | FpscrFz : fpscr;
        }

        private static uint StatusFrom(uint fpscr)
        {
            uint status = 0;
            if ((fpscr & 0x01) != 0) status |= SwInvalid;
            if ((fpscr & 0x02) != 0) status |= SwZeroDivide;
            if ((fpscr & 0x04) != 0) status |= SwOverflow;
            if ((fpscr & 0x08) != 0) status |= SwUnderflow;
            if ((fpscr & 0x10) != 0) status |= SwInexact;
            if ((fpscr & 0x80) != 0) status |= SwDenormal;
            return status;
        }

        private void RegisterCrtFloatingPoint()
        {
            // unsigned int _clearfp(void): the status word, then the sticky flags cleared.
            _handlers["_clearfp"] = () =>
            {
                uint fpscr = Fpscr;
                Fpscr = fpscr & ~FpscrFlags;
                Return(StatusFrom(fpscr));
            };

            _handlers["_statusfp"] = () => Return(StatusFrom(Fpscr));

            // unsigned int _controlfp(unsigned int new, unsigned int mask): the control word after
            // the change. Precision and infinity control do not exist on ARM and are ignored.
            _handlers["_controlfp"] = () =>
            {
                uint mask = (uint)Arg(1) & (McwEm | McwRc | McwDn);
                uint control = (ControlFrom(Fpscr) & ~mask) | ((uint)Arg(0) & mask);
                Fpscr = ApplyControl(Fpscr, control);
                Return(ControlFrom(Fpscr));
            };

            // errno_t _controlfp_s(unsigned int* current, unsigned int new, unsigned int mask): as
            // _controlfp, but asking for precision or infinity control on ARM is EINVAL.
            _handlers["_controlfp_s"] = () =>
            {
                uint mask = (uint)Arg(2);
                if ((mask & ~(McwEm | McwRc | McwDn)) != 0)
                {
                    if (Arg(0) != 0)
                    {
                        _emulator.WriteUInt32(Arg(0), ControlFrom(Fpscr));
                    }

                    InvalidParameter("_controlfp_s", Einval, Einval);
                    return;
                }

                uint control = (ControlFrom(Fpscr) & ~mask) | ((uint)Arg(1) & mask);
                Fpscr = ApplyControl(Fpscr, control);
                if (Arg(0) != 0)
                {
                    _emulator.WriteUInt32(Arg(0), ControlFrom(Fpscr));
                }

                Return(0);
            };

            // void _fpreset(void): the default state - round to nearest, no flush, no sticky flags.
            _handlers["_fpreset"] = () =>
            {
                Fpscr &= ~(FpscrFlags | FpscrEnables | FpscrRMode | FpscrFz | FpscrDn);
                Return(0);
            };

            // int _fpclass(double)
            _handlers["_fpclass"] = () => Return(FpClass(_frame.DoubleArg(0)));

            // double _copysign(double x, double y): |x| with y's sign, NaNs included.
            _handlers["_copysign"] = () => _frame.ReturnDouble(Math.CopySign(_frame.DoubleArg(0), _frame.DoubleArg(1)));

            // double _hypot(double, double) / float _hypotf(float, float): ERANGE when it overflows.
            _handlers["_hypot"] = () =>
            {
                double x = _frame.DoubleArg(0), y = _frame.DoubleArg(1);
                double result = double.Hypot(x, y);
                if (double.IsInfinity(result) && double.IsFinite(x) && double.IsFinite(y))
                {
                    SetErrno(Erange);
                }

                _frame.ReturnDouble(result);
            };
            _handlers["_hypotf"] = () =>
            {
                float x = _frame.FloatArg(0), y = _frame.FloatArg(1);
                float result = float.Hypot(x, y);
                if (float.IsInfinity(result) && float.IsFinite(x) && float.IsFinite(y))
                {
                    SetErrno(Erange);
                }

                _frame.ReturnFloat(result);
            };

            // ldiv_t ldiv(long, long) and div_t div(int, int): an 8-byte struct, which AAPCS returns
            // in memory - the caller's buffer comes in r0 and the operands follow in r1 and r2. (The
            // earlier div here answered in r0:r1; this replaces it.) Dividing by zero is the integer
            // divide-by-zero exception, which ends the process.
            _handlers["ldiv"] = () => DivideInto("ldiv");
            _handlers["div"] = () => DivideInto("div");
        }

        private void DivideInto(string name)
        {
            long result = Arg(0);
            int numerator = SignedArg(1), denominator = SignedArg(2);
            if (denominator == 0)
            {
                CrtTerminate($"{name}: integer divide by zero");
                return;
            }

            int quotient = numerator == int.MinValue && denominator == -1 ? int.MinValue : numerator / denominator;
            int remainder = denominator == -1 ? 0 : numerator % denominator;
            _emulator.WriteUInt32(result, unchecked((uint)quotient));
            _emulator.WriteUInt32(result + 4, unchecked((uint)remainder));
            Return(result);
        }

        internal static int FpClass(double value)
        {
            const int snan = 0x1, qnan = 0x2, ninf = 0x4, nn = 0x8, nd = 0x10, nz = 0x20, pz = 0x40, pd = 0x80, pn = 0x100, pinf = 0x200;
            long bits = BitConverter.DoubleToInt64Bits(value);
            bool negative = bits < 0;
            if (double.IsNaN(value))
            {
                return (bits & (1L << 51)) != 0 ? qnan : snan;
            }

            if (double.IsInfinity(value))
            {
                return negative ? ninf : pinf;
            }

            if (value == 0)
            {
                return negative ? nz : pz;
            }

            if (double.IsSubnormal(value))
            {
                return negative ? nd : pd;
            }

            return negative ? nn : pn;
        }

        // =======================================================================================
        // Memory
        // =======================================================================================

        private void RegisterCrtMemory()
        {
            // errno_t memcpy_s(void* dest, rsize_t destSize, const void* src, rsize_t count): the
            // destination is zeroed when the source is NULL or does not fit.
            _handlers["memcpy_s"] = () =>
            {
                long destination = Arg(0), size = Arg(1), source = Arg(2), count = Arg(3);
                if (count == 0)
                {
                    Return(0);
                    return;
                }

                if (destination == 0)
                {
                    InvalidParameter("memcpy_s", Einval, Einval);
                    return;
                }

                if (source == 0 || size < count)
                {
                    if (size > 0 && size <= CallFrame.SaneLengthLimit)
                    {
                        _emulator.WriteMemory(destination, new byte[size]);
                    }

                    int errno = source == 0 ? Einval : Erange;
                    InvalidParameter("memcpy_s", errno, errno);
                    return;
                }

                CopyGuest(destination, source, count);
                Return(0);
            };

            // errno_t memmove_s(void* dest, rsize_t numberOfElements, const void* src, rsize_t count)
            _handlers["memmove_s"] = () =>
            {
                long destination = Arg(0), size = Arg(1), source = Arg(2), count = Arg(3);
                if (count == 0)
                {
                    Return(0);
                    return;
                }

                if (destination == 0 || source == 0)
                {
                    InvalidParameter("memmove_s", Einval, Einval);
                    return;
                }

                if (size < count)
                {
                    InvalidParameter("memmove_s", Erange, Erange);
                    return;
                }

                CopyGuest(destination, source, count);
                Return(0);
            };

            // void* _recalloc(void* block, size_t num, size_t size): realloc, with the bytes past the
            // old block zeroed.
            _handlers["_recalloc"] = () =>
            {
                long block = Arg(0), count = Arg(1), size = Arg(2);
                if (count != 0 && size > 0xFFFFFFE0L / count)
                {
                    SetErrno(Enomem);
                    Return(0);
                    return;
                }

                long total = count * size;
                long old = block == 0 ? 0 : _emulator.AllocationSizeOf(block);
                if (block != 0 && total == 0)
                {
                    _emulator.FreeHeap(block);
                    Return(0);
                    return;
                }

                long fresh = _emulator.AllocateHeap(Math.Max(total, 1));
                if (fresh == 0)
                {
                    SetErrno(Enomem);
                    Return(0);
                    return;
                }

                long keep = Math.Min(old, total);
                if (keep > 0)
                {
                    CopyGuest(fresh, block, keep);
                }

                if (total > keep)
                {
                    _emulator.WriteMemory(fresh + keep, new byte[total - keep]);
                }

                if (block != 0)
                {
                    _emulator.FreeHeap(block);
                }

                Return(fresh);
            };

            // size_t _msize(void*): the block's usable size. The allocator rounds requests up to its
            // size classes, so this can exceed what was asked for, never fall short of it.
            _handlers["_msize"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("_msize", 0xFFFFFFFFL, Einval);
                    return;
                }

                long size = _emulator.AllocationSizeOf(Arg(0));
                Return(size == 0 ? 0xFFFFFFFFL : size);
            };

            // void* _expand(void* block, size_t size): resize in place, or NULL (ENOMEM) when it
            // cannot - which here is whenever the block's size class is too small.
            _handlers["_expand"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("_expand", 0, Einval);
                    return;
                }

                long size = Arg(1);
                long usable = _emulator.AllocationSizeOf(Arg(0));
                if (size > 0xFFFFFFE0L || size > usable)
                {
                    SetErrno(Enomem);
                    Return(0);
                    return;
                }

                Return(Arg(0));
            };
        }

        private void CopyGuest(long destination, long source, long count)
        {
            const int chunk = 1 << 20;
            if (destination > source && destination < source + count)
            {
                // Overlapping with the destination above: copy from the end down.
                for (long done = count; done > 0;)
                {
                    int step = (int)Math.Min(chunk, done);
                    done -= step;
                    _emulator.WriteMemory(destination + done, _emulator.ReadMemory(source + done, step));
                }

                return;
            }

            for (long done = 0; done < count;)
            {
                int step = (int)Math.Min(chunk, count - done);
                _emulator.WriteMemory(destination + done, _emulator.ReadMemory(source + done, step));
                done += step;
            }
        }

        // =======================================================================================
        // Time
        // =======================================================================================

        private const long MaxTime64 = 32535215999L;   // 3000-12-31 23:59:59, the CRT's _MAX__TIME64_T

        private const string ZoneName = "Coordinated Universal Time";

        private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

        private static readonly string[] MonthNamesFull =
            ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

        private void RegisterCrtTime()
        {
            // void _ftime64(struct __timeb64*): {__time64_t time; unsigned short millitm; short
            // timezone; short dstflag}, from the same clock as _ftime64_s.
            _handlers["_ftime64"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("_ftime64", 0, Einval);
                    return;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                byte[] block = new byte[16];
                BitConverter.GetBytes(now.ToUnixTimeSeconds()).CopyTo(block, 0);
                BitConverter.GetBytes((ushort)now.Millisecond).CopyTo(block, 8);
                _emulator.WriteMemory(Arg(0), block);
                Return(0);
            };

            // __time64_t _mkgmtime64(struct tm*): the tm read as UTC, normalised in place.
            _handlers["_mkgmtime64"] = () =>
            {
                if (Arg(0) == 0)
                {
                    _emulator.WriteRegister(Arm.UC_ARM_REG_R1, 0xFFFFFFFFL);
                    InvalidParameter("_mkgmtime64", 0xFFFFFFFFL, Einval);
                    return;
                }

                int[] tm = ReadTm(Arg(0));
                long seconds = TimeFromTm(tm);
                if (seconds < 0 || seconds > MaxTime64)
                {
                    SetErrno(Einval);
                    _frame.Return64(-1);
                    return;
                }

                WriteTm(Arg(0), TmFromTime(seconds));
                _frame.Return64(seconds);
            };

            // long* __timezone(void) / int* __daylight(void): the zone is UTC with no DST.
            _handlers["__timezone"] = () => Return(StaticCell(ref _timezoneCell, 4));
            _handlers["__daylight"] = () => Return(StaticCell(ref _daylightCell, 4));

            // errno_t _get_tzname(size_t* returned, char* buffer, size_t size, int index)
            _handlers["_get_tzname"] = () =>
            {
                long returned = Arg(0), buffer = Arg(1), size = Arg(2);
                int index = SignedArg(3);
                if (returned == 0 || (buffer == 0 && size != 0) || index is < 0 or > 1)
                {
                    InvalidParameter("_get_tzname", Einval, Einval);
                    return;
                }

                _emulator.WriteUInt32(returned, (uint)ZoneName.Length + 1);
                if (buffer == 0)
                {
                    Return(0);
                    return;
                }

                if (size < ZoneName.Length + 1)
                {
                    InvalidParameter("_get_tzname", Erange, Erange);
                    return;
                }

                _frame.WriteNarrowString(buffer, ZoneName);
                Return(0);
            };

            // char* _ctime64(const __time64_t*) / wchar_t* _wctime64: "Wed Jan 02 02:03:55 1980\n".
            _handlers["_ctime64"] = () => CtimeInto(wide: false);
            _handlers["_wctime64"] = () => CtimeInto(wide: true);

            // char* asctime(const struct tm*)
            _handlers["asctime"] = () =>
            {
                if (Arg(0) == 0)
                {
                    InvalidParameter("asctime", 0, Einval);
                    return;
                }

                int[] tm = ReadTm(Arg(0));
                if (!AsctimeFieldsValid(tm))
                {
                    InvalidParameter("asctime", 0, Einval);
                    return;
                }

                long buffer = StaticCell(ref _asctimeBuffer, 26);
                _frame.WriteNarrowString(buffer, Asctime(tm));
                Return(buffer);
            };

            // size_t strftime(char*, size_t, const char*, const struct tm*) and wcsftime, in the
            // "C" locale, with the VC11 CRT's directives and checks. These replace CrtLibrary's.
            _handlers["strftime"] = () => FormatTimeInto("strftime", wide: false);
            _handlers["_strftime_l"] = () => FormatTimeInto("_strftime_l", wide: false);
            _handlers["wcsftime"] = () => FormatTimeInto("wcsftime", wide: true);
            _handlers["_wcsftime_l"] = () => FormatTimeInto("_wcsftime_l", wide: true);
        }

        private int[] ReadTm(long address)
        {
            byte[] raw = _emulator.ReadMemory(address, 36);
            int[] tm = new int[9];
            for (int i = 0; i < 9; i++)
            {
                tm[i] = BitConverter.ToInt32(raw, i * 4);
            }

            return tm;
        }

        private void WriteTm(long address, int[] tm)
        {
            byte[] raw = new byte[36];
            for (int i = 0; i < 9; i++)
            {
                BitConverter.GetBytes(tm[i]).CopyTo(raw, i * 4);
            }

            _emulator.WriteMemory(address, raw);
        }

        /// <summary>Days since 1970-01-01 of a proleptic Gregorian date (month 1-12).</summary>
        internal static long DaysFromCivil(long year, long month, long day)
        {
            year -= month <= 2 ? 1 : 0;
            long era = (year >= 0 ? year : year - 399) / 400;
            long yearOfEra = year - (era * 400);
            long dayOfYear = ((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5 + day - 1;
            long dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
            return (era * 146097) + dayOfEra - 719468;
        }

        /// <summary>
        /// Seconds since the epoch for a tm whose fields may be out of range (mktime's normalisation):
        /// months carry into years, then days, hours, minutes and seconds are simply summed.
        /// </summary>
        internal static long TimeFromTm(int[] tm)
        {
            long month = tm[4];
            long year = tm[5] + 1900L + Math.DivRem(month, 12, out long monthIndex);
            if (monthIndex < 0)
            {
                monthIndex += 12;
                year--;
            }

            long days = DaysFromCivil(year, monthIndex + 1, 1) + tm[3] - 1;
            return (days * 86400) + (tm[2] * 3600L) + (tm[1] * 60L) + tm[0];
        }

        internal static int[] TmFromTime(long seconds)
        {
            DateTime when = DateTime.UnixEpoch.AddSeconds(seconds);
            return [when.Second, when.Minute, when.Hour, when.Day, when.Month - 1, when.Year - 1900, (int)when.DayOfWeek, when.DayOfYear - 1, 0];
        }

        private static bool AsctimeFieldsValid(int[] tm) =>
            tm[0] is >= 0 and <= 59 && tm[1] is >= 0 and <= 59 && tm[2] is >= 0 and <= 23 && tm[3] is >= 1 and <= 31 &&
            tm[4] is >= 0 and <= 11 && tm[5] is >= 0 and <= 8099 && tm[6] is >= 0 and <= 6;

        internal static string Asctime(int[] tm) =>
            $"{DayNames[tm[6]][..3]} {MonthNamesFull[tm[4]][..3]} {tm[3]:D2} {tm[2]:D2}:{tm[1]:D2}:{tm[0]:D2} {tm[5] + 1900}\n";

        private void CtimeInto(bool wide)
        {
            string name = wide ? "_wctime64" : "_ctime64";
            if (Arg(0) == 0)
            {
                InvalidParameter(name, 0, Einval);
                return;
            }

            long seconds = BitConverter.ToInt64(_emulator.ReadMemory(Arg(0), 8));
            if (seconds < 0 || seconds > MaxTime64)
            {
                SetErrno(Einval);
                Return(0);
                return;
            }

            string text = Asctime(TmFromTime(seconds));
            if (wide)
            {
                long buffer = StaticCell(ref _wctimeBuffer, 52);
                _emulator.WriteMemory(buffer, Encoding.Unicode.GetBytes(text + "\0"));
                Return(buffer);
            }
            else
            {
                long buffer = StaticCell(ref _ctimeBuffer, 26);
                _frame.WriteNarrowString(buffer, text);
                Return(buffer);
            }
        }

        private void FormatTimeInto(string name, bool wide)
        {
            long destination = Arg(0), max = Arg(1), formatAddress = Arg(2), tmAddress = Arg(3);
            if (destination == 0 || max == 0 || formatAddress == 0 || tmAddress == 0)
            {
                if (destination != 0 && max > 0)
                {
                    _emulator.WriteMemory(destination, wide ? [0, 0] : [0]);
                }

                InvalidParameter(name, 0, Einval);
                return;
            }

            string format = wide ? Wide(2) : Narrow(2);
            string? text = FormatTime(format, ReadTm(tmAddress));
            if (text is null)
            {
                _emulator.WriteMemory(destination, wide ? [0, 0] : [0]);
                InvalidParameter(name, 0, Einval);
                return;
            }

            if (text.Length + 1 > max)
            {
                _emulator.WriteMemory(destination, wide ? [0, 0] : [0]);
                SetErrno(Erange);
                Return(0);
                return;
            }

            _emulator.WriteMemory(destination, wide ? Encoding.Unicode.GetBytes(text + "\0") : [.. Encoding.Latin1.GetBytes(text), 0]);
            Return(text.Length);
        }

        /// <summary>
        /// strftime in the "C" locale, as the VC11 CRT formats it; null for a directive it rejects
        /// or a field out of range for the directive that reads it (the CRT's invalid parameter).
        /// The '#' flag drops leading zeros, and gives %c and %x their long forms.
        /// </summary>
        internal static string? FormatTime(string format, int[] tm)
        {
            int sec = tm[0], min = tm[1], hour = tm[2], mday = tm[3], mon = tm[4], year = tm[5], wday = tm[6], yday = tm[7];
            StringBuilder text = new();
            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] != '%')
                {
                    text.Append(format[i]);
                    continue;
                }

                if (++i >= format.Length)
                {
                    return null;
                }

                bool alternate = false;
                if (format[i] == '#')
                {
                    alternate = true;
                    if (++i >= format.Length)
                    {
                        return null;
                    }
                }

                string Number(int value, int width) => alternate ? value.ToString() : value.ToString("D" + width);
                string? piece = format[i] switch
                {
                    'a' => wday is >= 0 and <= 6 ? DayNames[wday][..3] : null,
                    'A' => wday is >= 0 and <= 6 ? DayNames[wday] : null,
                    'b' => mon is >= 0 and <= 11 ? MonthNamesFull[mon][..3] : null,
                    'B' => mon is >= 0 and <= 11 ? MonthNamesFull[mon] : null,
                    'c' => alternate
                        ? FormatTime("%A, %B %#d, %Y, %H:%M:%S", tm)
                        : FormatTime("%m/%d/%y %H:%M:%S", tm),
                    'd' => mday is >= 1 and <= 31 ? Number(mday, 2) : null,
                    'H' => hour is >= 0 and <= 23 ? Number(hour, 2) : null,
                    'I' => hour is >= 0 and <= 23 ? Number(hour % 12 == 0 ? 12 : hour % 12, 2) : null,
                    'j' => yday is >= 0 and <= 365 ? Number(yday + 1, 3) : null,
                    'm' => mon is >= 0 and <= 11 ? Number(mon + 1, 2) : null,
                    'M' => min is >= 0 and <= 59 ? Number(min, 2) : null,
                    'p' => hour is >= 0 and <= 23 ? (hour < 12 ? "AM" : "PM") : null,
                    'S' => sec is >= 0 and <= 60 ? Number(sec, 2) : null,
                    'U' => wday is >= 0 and <= 6 && yday is >= 0 and <= 365 ? Number((yday + 7 - wday) / 7, 2) : null,
                    'w' => wday is >= 0 and <= 6 ? wday.ToString() : null,
                    'W' => wday is >= 0 and <= 6 && yday is >= 0 and <= 365 ? Number((yday + 7 - ((wday + 6) % 7)) / 7, 2) : null,
                    'x' => alternate ? FormatTime("%A, %B %#d, %Y", tm) : FormatTime("%m/%d/%y", tm),
                    'X' => FormatTime("%H:%M:%S", tm),
                    'y' => year is >= -1900 and <= 8099 ? Number(((year % 100) + 100) % 100, 2) : null,
                    'Y' => year is >= -1900 and <= 8099 ? (year + 1900).ToString() : null,
                    'z' or 'Z' => ZoneName,
                    '%' => "%",
                    _ => null,
                };

                if (piece is null)
                {
                    return null;
                }

                text.Append(piece);
            }

            return text.ToString();
        }

        // =======================================================================================
        // C++ exception and RTTI support
        // =======================================================================================

        /// <summary>
        /// The vtable std::exception and the std exceptions this runtime constructs share: slot 0 the
        /// scalar deleting destructor (this, flags), slot 1 what(). Layout of the object: vfptr,
        /// _Mywhat (char*), _Mydofree (bool) - 12 bytes.
        /// </summary>
        private long StdExceptionVtable()
        {
            if (_stdExceptionVtable != 0)
            {
                return _stdExceptionVtable;
            }

            _stdExceptionVtable = _emulator.AllocateHeap(16);
            long destructor = _emulator.RegisterVtableMethod("std::exception::`scalar deleting destructor'", () =>
            {
                long self = Arg(0);
                DestroyStdException(self);
                if ((Arg(1) & 1) != 0)
                {
                    _emulator.FreeHeap(self);
                }

                Return(self);
            });
            long what = _emulator.RegisterVtableMethod("std::exception::what", () =>
            {
                long message = _emulator.ReadUInt32(Arg(0) + 4, 0);
                Return(message != 0 ? message : StaticNarrow(ref _unknownExceptionText, "Unknown exception"));
            });
            _emulator.WriteUInt32(_stdExceptionVtable, 0);
            _emulator.WriteUInt32(_stdExceptionVtable + 4, (uint)ArmEmulator.ThumbEntry(destructor));
            _emulator.WriteUInt32(_stdExceptionVtable + 8, (uint)ArmEmulator.ThumbEntry(what));
            _stdExceptionVtable += 4;   // the word before is the (absent) RTTI locator
            return _stdExceptionVtable;
        }

        private void ConstructStdException(long self, long message, bool copy)
        {
            if (self == 0)
            {
                return;
            }

            long stored = message;
            if (copy && message != 0)
            {
                byte[] text = Encoding.Latin1.GetBytes(_frame.ReadNarrowString(message, 4096));
                stored = _emulator.AllocateHeap(text.Length + 1);
                _emulator.WriteMemory(stored, [.. text, 0]);
            }

            _emulator.WriteUInt32(self, (uint)StdExceptionVtable());
            _emulator.WriteUInt32(self + 4, (uint)stored);
            _emulator.WriteMemory(self + 8, [(byte)(copy && message != 0 ? 1 : 0), 0, 0, 0]);
        }

        private void DestroyStdException(long self)
        {
            if (self == 0)
            {
                return;
            }

            if (_emulator.ReadMemory(self + 8, 1)[0] != 0)
            {
                _emulator.FreeHeap(_emulator.ReadUInt32(self + 4, 0));
            }

            _emulator.WriteUInt32(self + 4, 0);
            _emulator.WriteMemory(self + 8, [0]);
        }

        /// <summary>Throws a std exception the runtime itself raises (bad_typeid, bad_exception).</summary>
        private void ThrowStdException(string message, params string[] catchableTypes)
        {
            long self = _emulator.AllocateHeap(12);
            long text = _emulator.AllocateHeap(message.Length + 1);
            _frame.WriteNarrowString(text, message);
            _emulator.WriteUInt32(self, (uint)StdExceptionVtable());
            _emulator.WriteUInt32(self + 4, (uint)text);
            _emulator.WriteMemory(self + 8, [0, 0, 0, 0]);
            ThrowIntoImage(new ThrownException(self, catchableTypes));
        }

        private void RegisterCrtExceptionSupport()
        {
            // EXCEPTION_DISPOSITION __CxxFrameHandler3 / __C_specific_handler(record, frame, context,
            // dispatcher): the language handlers named in the image's unwind data. Only the OS's SEH
            // dispatcher calls them; C++ throws here are dispatched by the host's own unwinder, which
            // reads the same tables directly. So these are recorded and answer
            // ExceptionContinueSearch, what either answers for a frame with nothing to handle.
            _handlers["__CxxFrameHandler3"] = () => Unreachable("__CxxFrameHandler3", 1);
            _handlers["__C_specific_handler"] = () => Unreachable("__C_specific_handler", 1);

            // int __uncaught_exception(void): the C spelling of std::uncaught_exception.
            _handlers["__uncaught_exception"] = () => Return(_unwindingThreads.Contains(_emulator.CurrentThreadId) ? 1 : 0);

            // std::exception(const char* const& message, int): keeps the pointer, does not copy it.
            _handlers["??0exception@std@@QAA@ABQBDH@Z"] = () =>
            {
                ConstructStdException(Arg(0), Arg(1) == 0 ? 0 : _emulator.ReadUInt32(Arg(1), 0), copy: false);
                Return(Arg(0));
            };

            // std::bad_cast(const char* message = "bad cast"), its copy constructor and destructor.
            _handlers["??0bad_cast@std@@QAA@PBD@Z"] = () =>
            {
                ConstructStdException(Arg(0), Arg(1), copy: true);
                Return(Arg(0));
            };
            _handlers["??0bad_cast@std@@QAA@ABV01@@Z"] = () =>
            {
                long self = Arg(0), other = Arg(1);
                bool owned = other != 0 && _emulator.ReadMemory(other + 8, 1)[0] != 0;
                ConstructStdException(self, other == 0 ? 0 : _emulator.ReadUInt32(other + 4, 0), copy: owned);
                Return(self);
            };
            _handlers["??1bad_cast@std@@UAA@XZ"] = () =>
            {
                DestroyStdException(Arg(0));
                Return(0);
            };

            // bool type_info::operator==(const type_info&) const / operator!=: decorated names
            // compared from their second character, as the CRT does (so copies in two modules agree).
            _handlers["??8type_info@@QBA_NABV0@@Z"] = () => Return(SameType(Arg(0), Arg(1)) ? 1 : 0);
            _handlers["??9type_info@@QBA_NABV0@@Z"] = () => Return(SameType(Arg(0), Arg(1)) ? 0 : 1);

            // const char* type_info::name(__type_info_node*) const: the undecorated name, made once and
            // cached in the type_info's _M_data, with the allocation linked onto the module's list.
            _handlers["?name@type_info@@QBAPBDPAU__type_info_node@@@Z"] = TypeInfoName;

            // void __clean_type_info_names_internal(__type_info_node*): frees that list (module exit).
            _handlers["__clean_type_info_names_internal"] = () =>
            {
                long head = Arg(0);
                long node = head == 0 ? 0 : _emulator.ReadUInt32(head + 4, 0);
                int guard = 0;
                while (node != 0 && guard++ < 100_000)
                {
                    long next = _emulator.ReadUInt32(node + 4, 0);
                    _emulator.FreeHeap(_emulator.ReadUInt32(node, 0));
                    _emulator.FreeHeap(node);
                    node = next;
                }

                if (head != 0)
                {
                    _emulator.WriteUInt32(head + 4, 0);
                }

                Return(0);
            };

            // type_info* __RTtypeid(void* object): the dynamic type, from the complete object locator
            // before the vtable. A null object is std::bad_typeid; one with no RTTI is
            // std::__non_rtti_object.
            _handlers["__RTtypeid"] = () =>
            {
                long self = Arg(0);
                if (self == 0)
                {
                    ThrowStdException("Attempted a typeid of NULL pointer!", ".?AVbad_typeid@std@@", ".?AVexception@std@@");
                    return;
                }

                long vtable = _emulator.ReadUInt32(self, 0);
                long locator = vtable < 4 ? 0 : _emulator.ReadUInt32(vtable - 4, 0);
                long type = locator == 0 ? 0 : _emulator.ReadUInt32(locator + 12, 0);
                if (type == 0)
                {
                    ThrowStdException("Access violation - no RTTI data!", ".?AV__non_rtti_object@std@@", ".?AVbad_typeid@std@@", ".?AVexception@std@@");
                    return;
                }

                Return(type);
            };
        }

        private void Unreachable(string name, long answer)
        {
            if (CrtUnreachableCalls.Count < 64)
            {
                CrtUnreachableCalls.Add($"{name} from 0x{_emulator.ReturnAddress & ~1L:X8}");
            }

            Return(answer);
        }

        private string DecoratedName(long typeInfo) => typeInfo == 0 ? string.Empty : _frame.ReadNarrowString(typeInfo + 8, 4096);

        private bool SameType(long a, long b)
        {
            string left = DecoratedName(a), right = DecoratedName(b);
            return string.Equals(left.Length > 0 ? left[1..] : left, right.Length > 0 ? right[1..] : right, StringComparison.Ordinal);
        }

        private void TypeInfoName()
        {
            long self = Arg(0), head = Arg(1);
            long cached = _emulator.ReadUInt32(self + 4, 0);
            if (cached != 0)
            {
                Return(cached);
                return;
            }

            string name = UndecorateTypeName(DecoratedName(self));
            long text = _emulator.AllocateHeap(name.Length + 1);
            _frame.WriteNarrowString(text, name);
            _emulator.WriteUInt32(self + 4, (uint)text);
            if (head != 0)
            {
                long node = _emulator.AllocateHeap(8);
                _emulator.WriteUInt32(node, (uint)text);
                _emulator.WriteUInt32(node + 4, _emulator.ReadUInt32(head + 4, 0));
                _emulator.WriteUInt32(head + 4, (uint)node);
            }

            Return(text);
        }

        /// <summary>
        /// An RTTI type descriptor's name (".?AVFoo@ns@@") as type_info::name() gives it ("class
        /// ns::Foo"): classes, structs, unions, enums, namespaces, templates with type and integer
        /// arguments, the built-in types, pointers and references. Anything else comes back as the
        /// decorated name without its leading '.', which is never mistaken for a real one.
        /// </summary>
        internal static string UndecorateTypeName(string decorated)
        {
            try
            {
                return decorated.StartsWith('.') ? new TypeNameReader(decorated).ReadTop() : decorated;
            }
            catch (Exception)
            {
                return decorated.TrimStart('.');
            }
        }

        private sealed class TypeNameReader(string text)
        {
            private int _at = 1;
            private List<string> _names = new();
            private List<string> _types = new();

            public string ReadTop()
            {
                string result = ReadType();
                return _at == text.Length ? result : throw new FormatException();
            }

            private char Peek => _at < text.Length ? text[_at] : throw new FormatException();

            private char Next() => _at < text.Length ? text[_at++] : throw new FormatException();

            private static string Qualify(string type, char cv) => cv switch
            {
                'B' => type + " const",
                'C' => type + " volatile",
                'D' => type + " const volatile",
                'A' => type,
                _ => throw new FormatException(),
            };

            private string ReadType()
            {
                int start = _at;
                string type = ReadTypeCore();
                if (_at - start > 1 && !char.IsDigit(text[start]))
                {
                    _types.Add(type);
                }

                return type;
            }

            private string ReadTypeCore()
            {
                char c = Next();
                switch (c)
                {
                    case '?':
                    {
                        // ".?AV..." : the storage class comes first, then the type it qualifies.
                        char storage = Next();
                        return Qualify(ReadType(), storage);
                    }
                    case 'C': return "signed char";
                    case 'D': return "char";
                    case 'E': return "unsigned char";
                    case 'F': return "short";
                    case 'G': return "unsigned short";
                    case 'H': return "int";
                    case 'I': return "unsigned int";
                    case 'J': return "long";
                    case 'K': return "unsigned long";
                    case 'M': return "float";
                    case 'N': return "double";
                    case 'O': return "long double";
                    case 'X': return "void";
                    case '_':
                        return Next() switch
                        {
                            'J' => "__int64",
                            'K' => "unsigned __int64",
                            'N' => "bool",
                            'W' => "wchar_t",
                            _ => throw new FormatException(),
                        };
                    case 'V': return "class " + ReadQualifiedName();
                    case 'U': return "struct " + ReadQualifiedName();
                    case 'T': return "union " + ReadQualifiedName();
                    case 'W':
                        Next();   // the enum's underlying type
                        return "enum " + ReadQualifiedName();
                    case 'P' or 'Q' or 'R' or 'S' or 'A':
                    {
                        if (Peek == 'E')
                        {
                            Next();   // __ptr64
                        }

                        char cv = Next();
                        if (cv is not ('A' or 'B' or 'C' or 'D'))
                        {
                            throw new FormatException();
                        }

                        string pointee = Qualify(ReadType(), cv);
                        string suffix = c switch
                        {
                            'A' => " &",
                            'Q' => " * const",
                            'R' => " * volatile",
                            'S' => " * const volatile",
                            _ => " *",
                        };
                        return pointee + suffix;
                    }

                    default:
                        if (char.IsDigit(c))
                        {
                            return _types[c - '0'];
                        }

                        throw new FormatException();
                }
            }

            private string ReadQualifiedName()
            {
                List<string> parts = new();
                while (Peek != '@')
                {
                    parts.Add(ReadNameFragment());
                }

                Next();
                parts.Reverse();
                return string.Join("::", parts);
            }

            private string ReadSimpleName()
            {
                int end = text.IndexOf('@', _at);
                if (end < 0)
                {
                    throw new FormatException();
                }

                string name = text[_at..end];
                _at = end + 1;
                return name;
            }

            private string ReadNameFragment()
            {
                char c = Peek;
                if (char.IsDigit(c))
                {
                    Next();
                    return _names[c - '0'];
                }

                if (c == '?' && _at + 1 < text.Length && text[_at + 1] == '$')
                {
                    // A template's arguments have their own back-reference tables, and the
                    // template's own name is the first entry in them ("?$basic_string@...V?$
                    // allocator@D@2@@std@@": the 2 is std, after basic_string and char_traits<char>).
                    _at += 2;
                    List<string> outerNames = _names, outerTypes = _types;
                    _names = new List<string>();
                    _types = new List<string>();
                    string template = ReadSimpleName();
                    _names.Add(template);
                    List<string> arguments = new();
                    while (Peek != '@')
                    {
                        arguments.Add(Peek == '$' ? ReadNonTypeArgument() : ReadType());
                    }

                    Next();
                    _names = outerNames;
                    _types = outerTypes;
                    string joined = string.Join(",", arguments);
                    string full = $"{template}<{joined}{(joined.EndsWith('>') ? " " : "")}>";
                    _names.Add(full);
                    return full;
                }

                if (c == '?' && text.AsSpan(_at).StartsWith("?A0x"))
                {
                    ReadSimpleName();
                    _names.Add("`anonymous namespace'");
                    return "`anonymous namespace'";
                }

                string simple = ReadSimpleName();
                _names.Add(simple);
                return simple;
            }

            private string ReadNonTypeArgument()
            {
                Next();   // '$'
                if (Next() != '0')
                {
                    throw new FormatException();
                }

                bool negative = Peek == '?';
                if (negative)
                {
                    Next();
                }

                long value;
                if (char.IsDigit(Peek))
                {
                    value = Next() - '0' + 1;
                }
                else
                {
                    value = 0;
                    while (Peek != '@')
                    {
                        char digit = Next();
                        value = (value * 16) + (digit - 'A');
                    }

                    Next();
                }

                return (negative ? -value : value).ToString();
            }
        }

        // =======================================================================================
        // std::exception_ptr (VC11: two words, the first a token for a host-held record)
        // =======================================================================================

        private sealed class StoredException
        {
            public long Object;
            public IReadOnlyList<string> Names = [];
            public int References;
            public bool Owned;
            public long Destructor;
        }

        private readonly Dictionary<long, StoredException> _storedExceptions = new();

        /// <summary>The ThrowInfo each object the image threw was thrown with, for copying it later.</summary>
        private readonly Dictionary<long, long> _throwInfoByObject = new();

        private void RegisterCrtExceptionPointers()
        {
            // Remember what each guest throw described its object with: exception_ptr needs the
            // object's size and copy constructor, and the throw is the only place they are named.
            if (_handlers.TryGetValue("_CxxThrowException", out Action? throwOriginal))
            {
                _handlers["_CxxThrowException"] = () =>
                {
                    if (Arg(0) != 0 && Arg(1) != 0)
                    {
                        if (_throwInfoByObject.Count > 256)
                        {
                            _throwInfoByObject.Clear();
                        }

                        _throwInfoByObject[Arg(0)] = Arg(1);
                    }

                    throwOriginal();
                };
            }

            // std::exception_ptr (__ExceptionPtr*) is MSVCP110's and lives in MsvcpCore.cs.
        }

        private void WriteExceptionPtr(long destination, long token)
        {
            if (destination != 0)
            {
                _emulator.WriteUInt32(destination, (uint)token);
                _emulator.WriteUInt32(destination + 4, 0);
            }
        }

        private void AddReference(long token)
        {
            if (_storedExceptions.TryGetValue(token, out StoredException? stored))
            {
                stored.References++;
            }
        }

        /// <summary>
        /// Drops a reference; the last one destroys a copy this runtime made (its destructor, from the
        /// ThrowInfo, run in the guest) and frees it. Set the handler's return value first.
        /// </summary>
        private void ReleaseException(long token)
        {
            if (!_storedExceptions.TryGetValue(token, out StoredException? stored) || --stored.References > 0)
            {
                return;
            }

            _storedExceptions.Remove(token);
            _emulator.FreeHeap(token);
            if (!stored.Owned)
            {
                return;
            }

            if (stored.Destructor == 0)
            {
                _emulator.FreeHeap(stored.Object);
                return;
            }

            long returnTo = _emulator.ReturnAddress;
            _emulator.CallEmulated("exception_ptr destructor", stored.Destructor, [stored.Object], () =>
            {
                _emulator.FreeHeap(stored.Object);
                Return(0);
                _emulator.ContinueAt(returnTo);
            }, recycle: true);
        }

        /// <summary>
        /// Stores a copy of <paramref name="exceptionObject"/> behind a new exception_ptr. With a
        /// ThrowInfo the copy is made as the CRT makes it - the copy constructor its first catchable
        /// type names (run in the guest), or the bytes for a simple type; without one (an exception the
        /// host raised, which lives on the heap for good) the object itself is held.
        /// </summary>
        private void CaptureException(long destination, long exceptionObject, long throwInfo, IReadOnlyList<string> names)
        {
            long Store(long instance, bool owned, long destructor)
            {
                long token = _emulator.AllocateHeap(8);
                _storedExceptions[token] = new StoredException
                {
                    Object = instance, Names = names, References = 1, Owned = owned, Destructor = destructor,
                };
                return token;
            }

            long size = 0, copyConstructor = 0, destructor = 0;
            bool virtualBase = false;
            long imageBase = _emulator.ImageBase;
            if (throwInfo != 0)
            {
                uint unwindRva = _emulator.ReadUInt32(throwInfo + 4, 0);
                destructor = unwindRva == 0 ? 0 : (imageBase + unwindRva) | 1;
                uint arrayRva = _emulator.ReadUInt32(throwInfo + 12, 0);
                if (arrayRva != 0 && _emulator.ReadUInt32(imageBase + arrayRva, 0) > 0)
                {
                    long catchable = imageBase + _emulator.ReadUInt32(imageBase + arrayRva + 4, 0);
                    virtualBase = (_emulator.ReadUInt32(catchable, 0) & 4) != 0;
                    size = _emulator.ReadUInt32(catchable + 20, 0);
                    uint copyRva = _emulator.ReadUInt32(catchable + 24, 0);
                    copyConstructor = copyRva == 0 ? 0 : (imageBase + copyRva) | 1;
                }
            }

            if (size is <= 0 or > 0x10000 || exceptionObject == 0)
            {
                WriteExceptionPtr(destination, Store(exceptionObject, owned: false, 0));
                Return(0);
                return;
            }

            long copy = _emulator.AllocateHeap(size);
            if (copyConstructor == 0)
            {
                CopyGuest(copy, exceptionObject, size);
                WriteExceptionPtr(destination, Store(copy, owned: true, destructor));
                Return(0);
                return;
            }

            long returnTo = _emulator.ReturnAddress;
            long[] arguments = virtualBase ? [copy, exceptionObject, 1] : [copy, exceptionObject];
            _emulator.CallEmulated("exception_ptr copy", copyConstructor, arguments, () =>
            {
                WriteExceptionPtr(destination, Store(copy, owned: true, destructor));
                Return(0);
                _emulator.ContinueAt(returnTo);
            }, recycle: true);
        }

        // =======================================================================================
        // Threads
        // =======================================================================================

        private const long FirstBeginThreadHandle = 0x0D000000;

        private sealed class BeginThread
        {
            public int Id;
            public int SuspendCount;
        }

        private readonly Dictionary<long, BeginThread> _beginThreads = new();
        private long _nextBeginThreadHandle = FirstBeginThreadHandle;
        private bool _threadHandlesWired;

        private void RegisterCrtThreads()
        {
            // uintptr_t _beginthreadex(void* security, unsigned stackSize, unsigned (__stdcall*
            // start)(void*), void* argument, unsigned initFlag, unsigned* threadId): a guest thread,
            // and a handle WaitForSingleObject, ResumeThread and CloseHandle accept. CREATE_SUSPENDED
            // (4) holds it until ResumeThread. The stack size is the scheduler's own.
            _handlers["_beginthreadex"] = () =>
            {
                long start = Arg(2), argument = Arg(3), flags = Arg(4), threadIdOut = Arg(5);
                if (start == 0)
                {
                    InvalidParameter("_beginthreadex", 0, Einval);
                    return;
                }

                WireThreadHandles();
                long handle = _nextBeginThreadHandle;
                _nextBeginThreadHandle += 4;
                BeginThread thread = new() { SuspendCount = (flags & 4) != 0 ? 1 : 0 };
                _beginThreads[handle] = thread;

                if (thread.SuspendCount == 0)
                {
                    thread.Id = _emulator.CreateGuestThread("_beginthreadex", start, argument);
                }
                else
                {
                    // Starts in a host trap that waits for ResumeThread, then tail-calls start(argument)
                    // with lr still at the thread-exit trap, so start's return ends the thread.
                    long gate = _emulator.RegisterVtableMethod("_beginthreadex suspended start", () =>
                    {
                        Return(argument);
                        _emulator.ContinueAt(start);
                        _emulator.BlockCurrentThread(() => thread.SuspendCount == 0, why: "CREATE_SUSPENDED");
                    });
                    thread.Id = _emulator.CreateGuestThread("_beginthreadex", ArmEmulator.ThumbEntry(gate), handle);
                }

                if (threadIdOut != 0)
                {
                    _emulator.WriteUInt32(threadIdOut, (uint)(0x1000 + thread.Id));
                }

                Return(handle);
            };
        }

        /// <summary>
        /// Teaches the wait, resume and close entry points this runtime's thread handles, falling
        /// through to whoever handled them before for every other handle. Done at the first
        /// _beginthreadex, when every library has registered.
        /// </summary>
        private void WireThreadHandles()
        {
            if (_threadHandlesWired)
            {
                return;
            }

            _threadHandlesWired = true;
            const long waitObject0 = 0, waitTimeout = 0x102, infinite = 0xFFFFFFFF;

            foreach (string name in new[] { "WaitForSingleObject", "WaitForSingleObjectEx" })
            {
                Action? previous = _handlers.GetValueOrDefault(name);
                _handlers[name] = () =>
                {
                    if (!_beginThreads.TryGetValue(Arg(0), out BeginThread? thread))
                    {
                        if (previous is null)
                        {
                            Return(0xFFFFFFFFL);   // WAIT_FAILED
                        }
                        else
                        {
                            previous();
                        }

                        return;
                    }

                    long milliseconds = Arg(1);
                    bool Done() => _emulator.IsThreadFinished(thread.Id);
                    if (Done())
                    {
                        Return(waitObject0);
                        return;
                    }

                    if (milliseconds == 0)
                    {
                        Return(waitTimeout);
                        return;
                    }

                    if (milliseconds == infinite)
                    {
                        Return(waitObject0);
                        if (!_emulator.BlockCurrentThread(Done, why: $"thread {thread.Id}"))
                        {
                            Return(Done() ? waitObject0 : waitTimeout);
                        }

                        return;
                    }

                    // A timed wait gives the other threads a turn, then answers for what happened.
                    Return(waitTimeout);
                    if (!_emulator.BlockCurrentThread(() => true, () => Return(Done() ? waitObject0 : waitTimeout)))
                    {
                        Return(Done() ? waitObject0 : waitTimeout);
                    }
                };
            }

            Action? resume = _handlers.GetValueOrDefault("ResumeThread");
            _handlers["ResumeThread"] = () =>
            {
                if (!_beginThreads.TryGetValue(Arg(0), out BeginThread? thread))
                {
                    if (resume is null)
                    {
                        Return(0xFFFFFFFFL);
                    }
                    else
                    {
                        resume();
                    }

                    return;
                }

                int previousCount = thread.SuspendCount;
                if (thread.SuspendCount > 0)
                {
                    thread.SuspendCount--;
                }

                Return(previousCount);
            };

            Action? close = _handlers.GetValueOrDefault("CloseHandle");
            _handlers["CloseHandle"] = () =>
            {
                if (_beginThreads.Remove(Arg(0)))
                {
                    Return(1);
                    return;
                }

                if (close is null)
                {
                    Return(0);
                }
                else
                {
                    close();
                }
            };
        }
    }
}
