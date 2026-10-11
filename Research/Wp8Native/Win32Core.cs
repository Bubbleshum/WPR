using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The api-ms-win-core-* and api-ms-win-eventing-* sets that have no other home: error
    /// handling, debugging, process and thread identity, handles, system information, virtual
    /// memory queries, event tracing, time zones and date/time conversion, and localisation - each
    /// to its documented Windows Phone 8 contract, with the documented last-error on failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device described is a Windows Phone 8 handset of the Lumia 920 class: two ARMv7 cores
    /// with VFPv4, NEON and hardware divide, 1 GB of RAM, en-US. The locale is en-US everywhere
    /// (GetUserPreferredUILanguages has always said so); its data is spelled out rather than taken
    /// from the host, so it is the same on every host and matches what WP8's NLS tables held.
    /// </para>
    /// <para>
    /// The heap family (GetProcessHeap, HeapAlloc...) lives in HostStubs; files in FileLibrary;
    /// waits, locks and condition variables in SyncLibrary. The last-error value is FileLibrary's,
    /// one per guest thread.
    /// </para>
    /// </remarks>
    public sealed partial class Win32Core
    {
        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly HostStubs _stubs;
        private Dictionary<string, Action> _handlers = new();

        private const int ErrorInvalidHandle = 6;
        private const int ErrorBadLength = 24;
        private const int ErrorNotSupported = 50;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorInvalidFlags = 1004;

        static Win32Core()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (Exception)
            {
                // Without the provider only UTF-8/16, ASCII and Latin-1 exist; the code-page
                // conversions say ERROR_INVALID_PARAMETER for the rest.
            }
        }

        public Win32Core(ArmEmulator emulator, CallFrame frame, HostStubs stubs)
        {
            _emulator = emulator;
            _frame = frame;
            _stubs = stubs;
        }

        /// <summary>Everything the image sent to OutputDebugString, capped, for the report.</summary>
        public static List<string> DebugOutput { get; } = new();

        /// <summary>DebugBreak calls: where from. Recorded rather than trapped.</summary>
        public List<string> DebugBreaks { get; } = new();

        private int LastError
        {
            set => _stubs.Files.LastError = value;
        }

        private void Fail(int error, long value = 0)
        {
            LastError = error;
            _frame.Return(value);
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            _handlers = handlers;
            RegisterSystem(handlers);
            RegisterTime(handlers);
            RegisterLocale(handlers);
        }

        private void RegisterSystem(Dictionary<string, Action> handlers)
        {
            // --- errorhandling ---
            handlers["RaiseException"] = _stubs.RaiseExceptionFromImage;

            // LPTOP_LEVEL_EXCEPTION_FILTER SetUnhandledExceptionFilter(filter): the previous filter.
            handlers["SetUnhandledExceptionFilter"] = () =>
            {
                long previous = _unhandledFilter;
                _unhandledFilter = _frame.Arg(0);
                _frame.Return(previous);
            };

            // UINT SetErrorMode(UINT) / GetErrorMode(): the previous mode; no dialog is ever shown.
            handlers["SetErrorMode"] = () =>
            {
                long previous = _errorMode;
                _errorMode = _frame.Arg(0);
                _frame.Return(previous);
            };
            handlers["GetErrorMode"] = () => _frame.Return(_errorMode);

            // --- debug ---
            // BOOL IsDebuggerPresent(void): no debugger is attached to a guest.
            handlers["IsDebuggerPresent"] = () => _frame.Return(0);

            // void OutputDebugString(LPCSTR/LPCWSTR): to the per-game trace, where a debugger's
            // output window would have shown it.
            handlers["OutputDebugStringA"] = () => DebugString(_frame.ReadNarrowString(_frame.Arg(0), 4096));
            handlers["OutputDebugStringW"] = () => DebugString(_emulator.ReadUtf16String(_frame.Arg(0), 4096));

            // void DebugBreak(void): a breakpoint exception for a debugger. Recorded and returned
            // from - with no debugger it would end the process, and a game is never stopped for
            // asking.
            handlers["DebugBreak"] = () =>
            {
                if (DebugBreaks.Count < 32)
                {
                    DebugBreaks.Add($"DebugBreak from 0x{_emulator.ReturnAddress & ~1L:X8}");
                }

                _frame.Return(0);
            };

            // --- processthreads ---
            // HANDLE GetCurrentProcess(void): the pseudo-handle (HANDLE)-1.
            handlers["GetCurrentProcess"] = () => _frame.Return(0xFFFFFFFF);
            handlers["IsProcessorFeaturePresent"] = () => _frame.Return(ProcessorFeature(_frame.Arg(0)) ? 1 : 0);

            // --- handle ---
            handlers["DuplicateHandle"] = DuplicateHandle;

            // --- sysinfo ---
            handlers["GetNativeSystemInfo"] = () => { WriteSystemInfo(_frame.Arg(0)); _frame.Return(0); };
            handlers["GetSystemInfo"] = handlers["GetNativeSystemInfo"];
            handlers["GlobalMemoryStatusEx"] = GlobalMemoryStatusEx;

            // --- memory ---
            handlers["VirtualQuery"] = VirtualQuery;

            // --- eventing: no trace session listens on a phone ---
            // ULONG EventRegister(LPCGUID provider, PENABLECALLBACK, void* context, REGHANDLE* handle):
            // ERROR_SUCCESS and a handle. The enable callback is only called when a session enables
            // the provider, which never happens.
            handlers["EventRegister"] = () =>
            {
                if (_frame.Arg(0) == 0 || _frame.Arg(3) == 0)
                {
                    _frame.Return(ErrorInvalidParameter);
                    return;
                }

                _emulator.WriteUInt64(_frame.Arg(3), (ulong)++_eventProviders);
                _frame.Return(0);
            };

            // ULONG EventUnregister(REGHANDLE) and the writes: ERROR_SUCCESS with nobody listening,
            // which is what Windows answers when no session has the provider enabled.
            handlers["EventUnregister"] = () => _frame.Return(0);
            handlers["EventWrite"] = () => _frame.Return(0);
            handlers["EventWriteTransfer"] = () => _frame.Return(0);
            handlers["EventWriteEx"] = () => _frame.Return(0);
            handlers["EventWriteString"] = () => _frame.Return(0);
            handlers["EventSetInformation"] = () => _frame.Return(0);

            // BOOLEAN EventEnabled(REGHANDLE, PCEVENT_DESCRIPTOR) / EventProviderEnabled: not enabled.
            handlers["EventEnabled"] = () => _frame.Return(0);
            handlers["EventProviderEnabled"] = () => _frame.Return(0);
        }

        private long _unhandledFilter;
        private long _errorMode;
        private long _eventProviders;

        private void DebugString(string text)
        {
            text = text.TrimEnd('\r', '\n');
            System.Diagnostics.Trace.WriteLine("[wpr-wp8-debug] " + text);
            lock (DebugOutput)
            {
                if (DebugOutput.Count < 200)
                {
                    DebugOutput.Add(text);
                }
            }

            _frame.Return(0);
        }

        /// <summary>
        /// IsProcessorFeaturePresent for a Krait-class (Snapdragon S4) Windows Phone 8 CPU. What the
        /// x86 features ask is false; the ARM ones are what that CPU and the emulated one both have.
        /// </summary>
        private static bool ProcessorFeature(long feature) => feature switch
        {
            2 => true,    // PF_COMPARE_EXCHANGE_DOUBLE (ldrexd/strexd)
            12 => true,   // PF_NX_ENABLED
            18 => true,   // PF_ARM_VFP_32_REGISTERS_AVAILABLE
            19 => true,   // PF_ARM_NEON_INSTRUCTIONS_AVAILABLE
            23 => true,   // PF_FASTFAIL_AVAILABLE (Windows 8 and later)
            24 => true,   // PF_ARM_DIVIDE_INSTRUCTION_AVAILABLE
            25 => true,   // PF_ARM_64BIT_LOADSTORE_ATOMIC
            27 => true,   // PF_ARM_FMAC_INSTRUCTIONS_AVAILABLE (VFPv4)
            _ => false,
        };

        /// <summary>
        /// SYSTEM_INFO: architecture ARM (5), 4 KB pages, the 2 GB user space, two processors,
        /// 64 KB allocation granularity. dwProcessorType, wProcessorLevel and wProcessorRevision
        /// are x86-era fields left 0.
        /// </summary>
        private void WriteSystemInfo(long info)
        {
            if (info == 0)
            {
                return;
            }

            byte[] block = new byte[36];
            BitConverter.GetBytes((ushort)5).CopyTo(block, 0);
            BitConverter.GetBytes(0x1000u).CopyTo(block, 4);
            BitConverter.GetBytes(0x00010000u).CopyTo(block, 8);
            BitConverter.GetBytes(0x7FFEFFFFu).CopyTo(block, 12);
            BitConverter.GetBytes(0x3u).CopyTo(block, 16);
            BitConverter.GetBytes(2u).CopyTo(block, 20);
            BitConverter.GetBytes(0x10000u).CopyTo(block, 28);
            _emulator.WriteMemory(info, block);
        }

        private const long PhysicalMemory = 1L << 30;

        /// <summary>
        /// BOOL GlobalMemoryStatusEx(MEMORYSTATUSEX*): dwLength must be 64. A 1 GB phone, with the
        /// guest heap counted as used; there is no page file, so the commit figures equal the
        /// physical ones.
        /// </summary>
        private void GlobalMemoryStatusEx()
        {
            long status = _frame.Arg(0);
            if (status == 0 || _emulator.ReadUInt32(status) != 64)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            long used = Math.Clamp(_emulator.HeapUsed, 0, PhysicalMemory);
            long available = PhysicalMemory - used;
            const long totalVirtual = 0x7FFE0000;
            byte[] block = new byte[64];
            BitConverter.GetBytes(64u).CopyTo(block, 0);
            BitConverter.GetBytes((uint)(used * 100 / PhysicalMemory)).CopyTo(block, 4);
            BitConverter.GetBytes(PhysicalMemory).CopyTo(block, 8);
            BitConverter.GetBytes(available).CopyTo(block, 16);
            BitConverter.GetBytes(PhysicalMemory).CopyTo(block, 24);
            BitConverter.GetBytes(available).CopyTo(block, 32);
            BitConverter.GetBytes(totalVirtual).CopyTo(block, 40);
            BitConverter.GetBytes(Math.Max(0, totalVirtual - used)).CopyTo(block, 48);
            _emulator.WriteMemory(status, block);
            _frame.Return(1);
        }

        /// <summary>
        /// SIZE_T VirtualQuery(const void* address, MEMORY_BASIC_INFORMATION*, SIZE_T length): 28
        /// bytes written, or 0 with ERROR_BAD_LENGTH for a short buffer and ERROR_INVALID_PARAMETER
        /// for an address above the user space that nothing is mapped at.
        /// </summary>
        private void VirtualQuery()
        {
            long address = _frame.Arg(0);
            long buffer = _frame.Arg(1);
            if (buffer == 0 || _frame.Arg(2) < 28)
            {
                Fail(ErrorBadLength);
                return;
            }

            ArmEmulator.MemoryRegion region = _emulator.QueryRegion(address);
            if (region.State == ArmEmulator.MemFree && address >= 0x7FFF0000)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            byte[] block = new byte[28];
            BitConverter.GetBytes((uint)region.Base).CopyTo(block, 0);
            BitConverter.GetBytes((uint)region.AllocationBase).CopyTo(block, 4);
            BitConverter.GetBytes(region.AllocationProtect).CopyTo(block, 8);
            BitConverter.GetBytes((uint)region.Size).CopyTo(block, 12);
            BitConverter.GetBytes(region.State).CopyTo(block, 16);
            BitConverter.GetBytes(region.State == ArmEmulator.MemFree ? ArmEmulator.PageNoAccess : region.Protect).CopyTo(block, 20);
            BitConverter.GetBytes(region.Type).CopyTo(block, 24);
            _emulator.WriteMemory(buffer, block);
            _frame.Return(28);
        }

        private const long CurrentProcess = 0xFFFFFFFF, CurrentThread = 0xFFFFFFFE;
        private const long DuplicateCloseSource = 0x1;

        /// <summary>
        /// BOOL DuplicateHandle(HANDLE sourceProcess, HANDLE source, HANDLE targetProcess, HANDLE*
        /// target, DWORD access, BOOL inherit, DWORD options). Both processes must be this one. The
        /// GetCurrentThread pseudo-handle becomes a real handle on the calling thread, signalled when
        /// it ends; event, mutex, semaphore, file and mapping handles get a second handle on the same
        /// object. DUPLICATE_CLOSE_SOURCE closes the source. Anything else is ERROR_INVALID_HANDLE.
        /// </summary>
        private void DuplicateHandle()
        {
            long sourceProcess = _frame.Arg(0), source = _frame.Arg(1), targetProcess = _frame.Arg(2), target = _frame.Arg(3);
            long options = _frame.Arg(6);
            if (sourceProcess != CurrentProcess || targetProcess != CurrentProcess)
            {
                Fail(ErrorInvalidHandle);
                return;
            }

            long copy;
            if (source == CurrentThread)
            {
                copy = _stubs.Sync.CreateThreadHandle(_emulator.CurrentThreadId);
            }
            else if (source == CurrentProcess)
            {
                // A real process handle would be waitable until the process exits; nothing here
                // models that, so this is refused rather than faked.
                Fail(ErrorNotSupported);
                return;
            }
            else if (_stubs.Sync.OwnsHandle(source))
            {
                copy = _stubs.Sync.DuplicateObjectHandle(source);
            }
            else if (_stubs.Files.OwnsHandle(source))
            {
                copy = _stubs.Files.DuplicateFileHandle(source);
            }
            else
            {
                Fail(ErrorInvalidHandle);
                return;
            }

            if (target != 0)
            {
                _emulator.WriteUInt32(target, (uint)copy);
            }

            if ((options & DuplicateCloseSource) != 0 && _handlers.TryGetValue("CloseHandle", out Action? close))
            {
                _frame.Return(source);
                _emulator.WriteRegister(UnicornEngine.Const.Arm.UC_ARM_REG_R0, source);
                close();
            }

            _frame.Return(1);
        }

        // ---------------------------------------------------------------------------
        // shared helpers
        // ---------------------------------------------------------------------------

        private string Wide(long address, int max = 4096) => address == 0 ? string.Empty : _emulator.ReadUtf16String(address, max);

        /// <summary>A UTF-16 argument with an explicit count, or NUL-terminated when the count is negative.</summary>
        private string WideCounted(long address, int count) =>
            count < 0 ? Wide(address, 1 << 20) : Encoding.Unicode.GetString(_emulator.ReadMemory(address, count * 2));

        /// <summary>
        /// The NLS output convention: with a capacity of 0 return the size needed (with the NUL);
        /// too small is 0 with ERROR_INSUFFICIENT_BUFFER; otherwise write it and return the count
        /// written, NUL included.
        /// </summary>
        private void ReturnWide(string text, long buffer, long capacity)
        {
            int needed = text.Length + 1;
            if (capacity == 0)
            {
                _frame.Return(needed);
                return;
            }

            if (buffer == 0 || capacity < needed)
            {
                Fail(ErrorInsufficientBuffer);
                return;
            }

            _emulator.WriteMemory(buffer, Encoding.Unicode.GetBytes(text + "\0"));
            _frame.Return(needed);
        }
    }
}
