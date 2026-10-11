using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>A module's static TLS (IMAGE_DIRECTORY_ENTRY_TLS), as the loader set it up.</summary>
    public sealed class ModuleTls
    {
        internal ModuleTls(int index, long templateStart, int templateLength, int zeroFill, long indexAddress, IReadOnlyList<long> callbacks)
        {
            Index = index;
            TemplateStart = templateStart;
            TemplateLength = templateLength;
            ZeroFill = zeroFill;
            IndexAddress = indexAddress;
            Callbacks = callbacks;
        }

        /// <summary>The slot in every thread's ThreadLocalStoragePointer array, written to <see cref="IndexAddress"/>.</summary>
        public int Index { get; }

        /// <summary>StartAddressOfRawData: the initialised part of the template, in the mapped image.</summary>
        public long TemplateStart { get; }

        public int TemplateLength { get; }

        /// <summary>SizeOfZeroFill: zero bytes after the template.</summary>
        public int ZeroFill { get; }

        /// <summary>AddressOfIndex (the image's <c>_tls_index</c>), or 0 when it names nothing writable.</summary>
        public long IndexAddress { get; }

        /// <summary>The PIMAGE_TLS_CALLBACK array, in order.</summary>
        public IReadOnlyList<long> Callbacks { get; }

        public int BlockSize => TemplateLength + ZeroFill;
    }

    /// <summary>
    /// Thread environment blocks and static TLS: what <c>__declspec(thread)</c> compiles to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MSVC for ARM reads a thread-local variable as
    /// <c>mrc p15,0,rT,c13,c0,2 ; ldr rT,[rT,#0x2C] ; ldr rT,[rT,rIndex,lsl #2] ; ldr rX,[rT,#offset]</c>:
    /// CP15 TPIDRURW is the TEB, TEB+0x2C is ThreadLocalStoragePointer, that is an array indexed
    /// by the module's <c>_tls_index</c>, and the slot is the thread's copy of the module's
    /// template. Before this TPIDRURW was 0 on dynarmic, so every such read walked the null page:
    /// every thread shared one zero-filled "block" at address 0, and the template's initial values
    /// were never seen (Game.exe of 8e162189 does it 1,847 times).
    /// </para>
    /// <para>
    /// Each guest thread now has its own TEB (the main thread's at <see cref="TebBase"/>, the rest
    /// on the heap) holding NT_TIB's stack bounds and Self, the client id, and the TLS array.
    /// Indices go to modules with a TLS directory in load order, the main image first, as
    /// LdrpInitializeTls assigns them. A module loaded later (LoadPackagedLibrary) gets the lowest
    /// free index and a block in every live thread, as Windows has done since Vista; FreeLibrary
    /// clears the slot and frees the index.
    /// </para>
    /// <para>
    /// TLS callbacks run where the loader runs them: for a DLL, before its DllMain with the same
    /// reason; for the main image, after every start-up DLL and before the entry point (for a
    /// component, before the DllMain its host calls); for THREAD_ATTACH/DETACH, per module in
    /// initialisation order (reverse for detach), callbacks then DllMain. A module with TLS keeps
    /// its thread notifications even after DisableThreadLibraryCalls, as on Windows - the call
    /// succeeds and changes nothing.
    /// </para>
    /// <para>
    /// Part of the loader: <c>WPR_WP8_NO_MODULES=1</c> turns all of it off and TPIDRURW stays 0,
    /// which is the behaviour from before. Unicorn ignores TPIDRURW writes, so this only takes
    /// effect on dynarmic.
    /// </para>
    /// </remarks>
    public sealed partial class ArmEmulator
    {
        /// <summary>TEB offsets (32-bit NT_TIB/TEB, identical on ARM).</summary>
        private const int TebStackBase = 0x04, TebStackLimit = 0x08, TebSelf = 0x18,
            TebProcessId = 0x20, TebThreadId = 0x24, TebTlsPointer = 0x2C;

        private const int TebBytes = 0x1000;

        /// <summary>The process id GetCurrentProcessId reports, so the TEB agrees with it.</summary>
        private const int GuestProcessId = 0x2000;

        /// <summary>Largest TLS block accepted from a directory; anything bigger is a corrupt directory.</summary>
        private const int MaxTlsBlock = 16 * 1024 * 1024;

        private const int MaxTlsCallbacks = 256;

        /// <summary>Which module owns each TLS index; null for a free one.</summary>
        private readonly List<LoadedModule?> _tlsOwners = new();

        /// <summary>What happened while setting up static TLS: indices, blocks, rejected directories.</summary>
        public List<string> TlsLog { get; } = new();

        private bool UsesThreadEnvironment => !LoaderDisabled;

        /// <summary>The TEB of a guest thread (1 is the main thread), or 0.</summary>
        public long ThreadEnvironmentBlock(int threadId)
            => _threads.FirstOrDefault(t => t.Id == threadId)?.Teb ?? (threadId == 1 && UsesThreadEnvironment ? TebBase : 0);

        /// <summary>The thread's copy of a module's TLS block, or 0.</summary>
        public long TlsBlockOf(int threadId, LoadedModule module)
        {
            GuestThread? thread = _threads.FirstOrDefault(t => t.Id == threadId);
            if (thread is null || thread.TlsArray == 0 || module.Tls is not { } tls || tls.Index >= thread.TlsCapacity)
            {
                return 0;
            }

            return ReadUInt32(thread.TlsArray + (tls.Index * 4L));
        }

        /// <summary>The main thread's TEB, filled once the stack exists; called from the constructor.</summary>
        private void SetUpMainThreadEnvironment()
        {
            if (!UsesThreadEnvironment)
            {
                return;
            }

            GuestThread main = CurrentThread;
            main.Teb = TebBase;
            main.StackHigh = StackBase + StackSize;
            main.StackLow = StackBase;
            WriteThreadEnvironment(main);
            LoadThreadPointer(main);
        }

        private void WriteThreadEnvironment(GuestThread thread)
        {
            byte[] teb = new byte[TebBytes];
            BitConverter.TryWriteBytes(teb.AsSpan(TebStackBase), (uint)thread.StackHigh);
            BitConverter.TryWriteBytes(teb.AsSpan(TebStackLimit), (uint)thread.StackLow);
            BitConverter.TryWriteBytes(teb.AsSpan(TebSelf), (uint)thread.Teb);
            BitConverter.TryWriteBytes(teb.AsSpan(TebProcessId), (uint)GuestProcessId);

            // The id GetCurrentThreadId answers (ThreadLibrary).
            BitConverter.TryWriteBytes(teb.AsSpan(TebThreadId), (uint)(0x1000 + thread.Id));
            BitConverter.TryWriteBytes(teb.AsSpan(TebTlsPointer), (uint)thread.TlsArray);
            _cpu.MemWrite(thread.Teb, teb);
        }

        /// <summary>Points CP15 TPIDRURW (and TPIDRURO, which used to hold the TEB) at the thread's TEB.</summary>
        private void LoadThreadPointer(GuestThread thread)
        {
            // Unicorn accepts a TPIDRURW write as a no-op and warns about it every time.
            if (thread.Teb == 0 || _cpu.Capabilities.Name.StartsWith("Unicorn", StringComparison.Ordinal))
            {
                return;
            }

            _cpu.RegWrite(Arm.UC_ARM_REG_C13_C0_2, thread.Teb);
            _cpu.RegWrite(Arm.UC_ARM_REG_C13_C0_3, thread.Teb);
        }

        /// <summary>A new thread's TEB and its copy of every module's TLS block.</summary>
        private void SetUpThreadEnvironment(GuestThread thread, long stackLow, long stackHigh)
        {
            if (!UsesThreadEnvironment)
            {
                return;
            }

            thread.Teb = AllocateHeap(TebBytes);
            thread.StackLow = stackLow;
            thread.StackHigh = stackHigh;
            for (int index = 0; index < _tlsOwners.Count; index++)
            {
                if (_tlsOwners[index] is { Tls: { } tls } module)
                {
                    SetTlsSlot(thread, index, CreateTlsBlock(module, tls));
                }
            }

            WriteThreadEnvironment(thread);
        }

        /// <summary>
        /// Reads a freshly mapped (and relocated) module's TLS directory, gives it an index,
        /// writes the index to its <c>_tls_index</c>, and gives every live thread its block.
        /// </summary>
        private void AttachStaticTls(LoadedModule module)
        {
            if (!UsesThreadEnvironment || module.Image.TlsDirectoryRva == 0 || module.Image.TlsDirectorySize < 24)
            {
                return;
            }

            long directory = module.ImageBase + module.Image.TlsDirectoryRva;
            if (!module.Contains(directory) || !module.Contains(directory + 23))
            {
                TlsLog.Add($"{module.Name}: TLS directory at RVA 0x{module.Image.TlsDirectoryRva:X} is outside the image; no static TLS");
                return;
            }

            // The directory holds VAs, which base relocation has already adjusted.
            long start = ReadUInt32(directory);
            long end = ReadUInt32(directory + 4);
            long indexAddress = ReadUInt32(directory + 8);
            long callbackArray = ReadUInt32(directory + 12);
            long zeroFill = ReadUInt32(directory + 16);

            long length = end - start;
            if (length < 0 || length + zeroFill > MaxTlsBlock || (length > 0 && !(module.Contains(start) && module.Contains(end - 1))))
            {
                TlsLog.Add($"{module.Name}: TLS template 0x{start:X8}-0x{end:X8} (+{zeroFill} zero) is not inside the image; no static TLS");
                return;
            }

            List<long> callbacks = new();
            if (callbackArray != 0 && module.Contains(callbackArray))
            {
                for (long at = callbackArray; callbacks.Count < MaxTlsCallbacks && module.Contains(at + 3); at += 4)
                {
                    long callback = ReadUInt32(at);
                    if (callback == 0)
                    {
                        break;
                    }

                    callbacks.Add(callback);
                }
            }
            else if (callbackArray != 0)
            {
                TlsLog.Add($"{module.Name}: TLS callback array 0x{callbackArray:X8} is outside the image; no callbacks run");
            }

            int index = _tlsOwners.IndexOf(null);
            if (index < 0)
            {
                index = _tlsOwners.Count;
                _tlsOwners.Add(module);
            }
            else
            {
                _tlsOwners[index] = module;
            }

            if (indexAddress != 0 && module.Contains(indexAddress))
            {
                WriteUInt32(indexAddress, (uint)index);
            }
            else
            {
                TlsLog.Add($"{module.Name}: AddressOfIndex 0x{indexAddress:X8} is not in the image; the index was not written");
                indexAddress = 0;
            }

            ModuleTls tls = new(index, start, (int)length, (int)zeroFill, indexAddress, callbacks);
            module.Tls = tls;

            int threads = 0;
            foreach (GuestThread thread in LiveThreads())
            {
                SetTlsSlot(thread, index, CreateTlsBlock(module, tls));
                threads++;
            }

            TlsLog.Add($"{module.Name}: TLS index {index}, {tls.BlockSize} byte block ({length} template + {zeroFill} zero), " +
                       $"{callbacks.Count} callback(s), {threads} thread(s) given a block");
        }

        /// <summary>The main thread (created on first use) and every thread that has not finished.</summary>
        private IEnumerable<GuestThread> LiveThreads()
        {
            _ = CurrentThread;
            return _threads.Where(t => !t.Finished && t.Teb != 0).ToList();
        }

        /// <summary>A copy of the module's template as it is now in the image, zero-filled after.</summary>
        private long CreateTlsBlock(LoadedModule module, ModuleTls tls)
        {
            int size = Math.Max(tls.BlockSize, 4);
            long block = AllocateHeap(size);
            byte[] bytes = new byte[size];
            if (tls.TemplateLength > 0)
            {
                ReadMemory(tls.TemplateStart, tls.TemplateLength).CopyTo(bytes, 0);
            }

            _cpu.MemWrite(block, bytes);
            return block;
        }

        /// <summary>Stores a slot of the thread's TLS array, growing (and re-pointing the TEB at) the array when needed.</summary>
        private void SetTlsSlot(GuestThread thread, int index, long value)
        {
            if (index >= thread.TlsCapacity)
            {
                int capacity = Math.Max(64, thread.TlsCapacity);
                while (capacity <= index)
                {
                    capacity *= 2;
                }

                long array = AllocateHeap(capacity * 4L);
                byte[] bytes = new byte[capacity * 4];
                if (thread.TlsArray != 0)
                {
                    ReadMemory(thread.TlsArray, thread.TlsCapacity * 4).CopyTo(bytes, 0);
                }

                _cpu.MemWrite(array, bytes);

                // The old array is left alone: another frame may still hold a pointer into it,
                // which on Windows is what LdrpHandleTlsData's deferred free is for.
                thread.TlsArray = array;
                thread.TlsCapacity = capacity;
                if (thread.Teb != 0)
                {
                    WriteUInt32(thread.Teb + TebTlsPointer, (uint)array);
                }
            }

            WriteUInt32(thread.TlsArray + (index * 4L), (uint)value);
        }

        /// <summary>FreeLibrary: every thread's slot cleared and the index given back.</summary>
        private void DetachStaticTls(LoadedModule module)
        {
            if (module.Tls is not { } tls)
            {
                return;
            }

            foreach (GuestThread thread in _threads)
            {
                if (thread.TlsArray != 0 && tls.Index < thread.TlsCapacity)
                {
                    long block = ReadUInt32(thread.TlsArray + (tls.Index * 4L));
                    WriteUInt32(thread.TlsArray + (tls.Index * 4L), 0);
                    FreeHeap(block);
                }
            }

            if (tls.Index < _tlsOwners.Count && _tlsOwners[tls.Index] == module)
            {
                _tlsOwners[tls.Index] = null;
            }

            TlsLog.Add($"{module.Name}: TLS index {tls.Index} released");
        }
    }
}
