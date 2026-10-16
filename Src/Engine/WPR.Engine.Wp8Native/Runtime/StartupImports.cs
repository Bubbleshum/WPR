using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The CRT, vccorlib and Win32 imports every title reaches before its first frame, each to its
    /// documented contract: the top of the 1b to-do list (the runtime convergence plan). All of
    /// these used to be answered with 0, which the trap policy turned into a stop at boot.
    /// </summary>
    public sealed partial class HostStubs
    {
        /// <summary>CoInitializeEx/RoInitialize calls not yet balanced by an uninitialise, per thread.</summary>
        private readonly Dictionary<int, int> _comInitialisedThreads = new();
        private long _mainArguments;
        private long _commandLineArguments;

        /// <summary>
        /// CoInitializeEx / RoInitialize: S_OK the first time on a thread, S_FALSE (1) for each nested
        /// call after; CoUninitialize / RoUninitialize undo one. There are no apartments here, so
        /// nothing else is owed.
        /// </summary>
        private long InitialiseApartment()
        {
            int thread = _emulator.CurrentThreadId;
            int depth = _comInitialisedThreads.GetValueOrDefault(thread) + 1;
            _comInitialisedThreads[thread] = depth;
            return depth == 1 ? 0 : 1;
        }

        private void UninitialiseApartment()
        {
            int thread = _emulator.CurrentThreadId;
            if (_comInitialisedThreads.TryGetValue(thread, out int depth))
            {
                if (depth <= 1)
                {
                    _comInitialisedThreads.Remove(thread);
                }
                else
                {
                    _comInitialisedThreads[thread] = depth - 1;
                }
            }
        }

        private void RegisterStartupImports(string imageDirectory)
        {
            RegisterExceptionQueries();
            RegisterJumps();

            // BOOL DisableThreadLibraryCalls(HMODULE): nonzero on success. Thread attach/detach
            // notifications are never sent here, so there is nothing to disable.
            _handlers["DisableThreadLibraryCalls"] = () => Return(1);

            // HANDLE GetCurrentThread(void): the pseudo-handle (HANDLE)-2, which every API taking a
            // thread handle reads as "the calling thread". 0 is NULL.
            _handlers["GetCurrentThread"] = () => Return(0xFFFFFFFE);

            // HRESULT CoInitializeEx(void*, DWORD): see InitialiseApartment.
            _handlers["CoInitializeEx"] = () => Return(InitialiseApartment());

            // void _lock(int) / _unlock(int): the CRT's numbered locks. They follow whatever the
            // critical-section model is, looked up per call so they change when it does.
            _handlers["_lock"] = () => _handlers["EnterCriticalSection"]();
            _handlers["_unlock"] = () => _handlers["LeaveCriticalSection"]();

            string argv0 = MainModuleName(imageDirectory);

            // int __getmainargs(int* argc, char*** argv, char*** env, int wildcard, _startupinfo*):
            // 0 on success. argv is { argv0, NULL } and env is { NULL } - a WP8 app has no command
            // line and no environment block.
            _handlers["__getmainargs"] = () =>
            {
                if (_mainArguments == 0)
                {
                    byte[] name = System.Text.Encoding.ASCII.GetBytes(argv0 + "\0");
                    long text = _emulator.AllocateHeap(name.Length);
                    _emulator.WriteMemory(text, name);
                    _mainArguments = _emulator.AllocateHeap(12);   // argv[0], argv[1] = NULL, env[0] = NULL
                    _emulator.WriteUInt32(_mainArguments, (uint)text);
                    _emulator.WriteUInt32(_mainArguments + 4, 0);
                    _emulator.WriteUInt32(_mainArguments + 8, 0);
                }

                WriteOut(Arg(0), 1);
                WriteOut(Arg(1), _mainArguments);
                WriteOut(Arg(2), _mainArguments + 8);
                Return(0);
            };

            // wchar_t** Platform::Details::GetCmdArguments(int* argc): what vccorlib builds
            // main(Platform::Array<String^>^ args) from. The same { argv0, NULL }, wide.
            _handlers["?GetCmdArguments@Details@Platform@@YAPAPA_WPAH@Z"] = () =>
            {
                if (_commandLineArguments == 0)
                {
                    byte[] name = System.Text.Encoding.Unicode.GetBytes(argv0 + "\0");
                    long text = _emulator.AllocateHeap(name.Length);
                    _emulator.WriteMemory(text, name);
                    _commandLineArguments = _emulator.AllocateHeap(8);
                    _emulator.WriteUInt32(_commandLineArguments, (uint)text);
                    _emulator.WriteUInt32(_commandLineArguments + 4, 0);
                }

                WriteOut(Arg(0), 1);
                Return(_commandLineArguments);
            };

            // void* Platform::Details::GetIBoxArrayVtable(void*) is in VccorlibLibrary.cs, with GetIBoxVtable.
        }

        private readonly HashSet<int> _unwindingThreads = new();

        /// <summary>
        /// bool std::uncaught_exception(): true while a thrown exception has not reached its
        /// handler on this thread - which is exactly when the cleanup funclets run. ostream's
        /// sentry destructor asks it on every insertion, so 0 was right almost always; the
        /// exception is the one case the function exists for.
        /// </summary>
        private void RegisterExceptionQueries()
        {
            _handlers["?uncaught_exception@std@@YA_NXZ"] = () => Return(_unwindingThreads.Contains(_emulator.CurrentThreadId) ? 1 : 0);
        }

        // ARM _JUMP_BUFFER (setjmp.h): Frame, R4-R11, Sp, Pc, Fpscr, then D8-D15 at 48. 112 bytes.
        private const int JumpFrame = 0, JumpR4 = 4, JumpSp = 36, JumpPc = 40, JumpFpscr = 44, JumpD8 = 48;

        /// <summary>
        /// int setjmp(jmp_buf, frame) / void longjmp(jmp_buf, int). setjmp saves the callee-saved
        /// state - r4-r11, sp, the return address, d8-d15 - and returns 0; longjmp puts it back and
        /// returns from that setjmp a second time with its value, or 1 for 0. FPSCR is recorded as
        /// 0 and not restored: the CPU interface has no FPSCR access, and nothing here changes
        /// rounding modes. No C++ destructors run between the two (MSVC's longjmp would unwind
        /// them under /EH); the callers met so far are C libraries - libpng and libjpeg error
        /// recovery - which have none.
        /// </summary>
        private void RegisterJumps()
        {
            _handlers["setjmp"] = () =>
            {
                long buffer = Arg(0);
                _emulator.WriteUInt32(buffer + JumpFrame, (uint)Arg(1));
                for (int i = 0; i < 8; i++)
                {
                    _emulator.WriteUInt32(buffer + JumpR4 + (i * 4), (uint)_emulator.ReadRegister(Arm.UC_ARM_REG_R4 + i));
                }

                _emulator.WriteUInt32(buffer + JumpSp, (uint)_emulator.ReadRegister(Arm.UC_ARM_REG_SP));
                _emulator.WriteUInt32(buffer + JumpPc, (uint)_emulator.ReturnAddress);
                _emulator.WriteUInt32(buffer + JumpFpscr, 0);
                for (int i = 0; i < 16; i++)   // d8-d15 are s16-s31
                {
                    _emulator.WriteUInt32(buffer + JumpD8 + (i * 4), _emulator.Cpu.VfpRead(16 + i));
                }

                Return(0);
            };
            _handlers["_setjmp"] = _handlers["setjmp"];

            _handlers["longjmp"] = () =>
            {
                long buffer = Arg(0);
                long value = Arg(1) == 0 ? 1 : Arg(1);
                for (int i = 0; i < 8; i++)
                {
                    _emulator.WriteRegister(Arm.UC_ARM_REG_R4 + i, _emulator.ReadUInt32(buffer + JumpR4 + (i * 4)));
                }

                for (int i = 0; i < 16; i++)
                {
                    _emulator.Cpu.VfpWrite(16 + i, _emulator.ReadUInt32(buffer + JumpD8 + (i * 4)));
                }

                _emulator.WriteRegister(Arm.UC_ARM_REG_SP, _emulator.ReadUInt32(buffer + JumpSp));
                Return(value);
                _emulator.ContinueAt(_emulator.ReadUInt32(buffer + JumpPc));
            };
        }

        private void WriteOut(long pointer, long value)
        {
            if (pointer != 0)
            {
                _emulator.WriteUInt32(pointer, (uint)value);
            }
        }

        /// <summary>argv[0]: where the main module would be installed on a phone.</summary>
        private static string MainModuleName(string imageDirectory)
        {
            string[] executables = Directory.Exists(imageDirectory) ? Directory.GetFiles(imageDirectory, "*.exe") : [];
            string name = executables.Length == 1 ? Path.GetFileName(executables[0]) : "app.exe";
            return @"C:\Data\Programs\Install\" + name;
        }
    }
}
