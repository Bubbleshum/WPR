using UnicornEngine;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Hosts one ARMv7 Thumb-2 image on an emulated CPU and traps every call that
    /// leaves it.
    /// </summary>
    /// <remarks>
    /// The central trick is the import table. Rather than emulating msvcr110, d3d11 and
    /// the rest, every IAT slot is rewritten to point at a unique 4-byte slot in a trap
    /// page. Each slot holds a single <c>bx lr</c>, so a call to an import enters the
    /// page, fires a code hook - which is where host code gets to implement the function -
    /// and returns immediately. That boundary is where a real backend would bridge into
    /// managed implementations of Win32, COM and WinRT.
    /// </remarks>
    public sealed class ArmEmulator : IDisposable
    {
        // Address space layout. Chosen to sit clear of a typical 0x00400000 image base.
        private const long StackBase = 0x50000000L;
        private const long StackSize = 4 * 1024 * 1024;
        private const long HeapBase = 0x60000000L;

        /// <summary>
        /// Generous on purpose. The allocator never frees, so every transient allocation
        /// the image makes is permanent here, and a real game churns through them quickly.
        /// 256 MB comfortably exceeds the 180 MB a WP8 app was allowed in total.
        /// </summary>
        private const long HeapSize = 1024L * 1024 * 1024;
        private const long TrapBase = 0xA0000000L;
        private const long TrapSize = 64 * 1024;
        private const long TebBase = 0xA1000000L;
        private const long TebSize = 64 * 1024;

        /// <summary>
        /// Thumb <c>bx r12</c>. Every trap slot holds this one instruction.
        /// </summary>
        /// <remarks>
        /// It is <c>bx r12</c> rather than the more obvious <c>bx lr</c> so that a handler
        /// can choose where the CPU goes next. <see cref="OnTrapEntered"/> presets r12 to lr,
        /// which makes the default behaviour an ordinary return; a handler that wants to
        /// call into emulated code instead overwrites r12 with its target and lr with a
        /// return trap, turning the same instruction into a tail call. r12 is the AAPCS
        /// intra-procedure scratch register, so clobbering it across a call is legal.
        ///
        /// This is what makes host-to-emulated calls possible at all: it never re-enters
        /// the CPU, so it works from inside a hook, where a nested emulation would not.
        /// </remarks>
        private const ushort ThumbBxR12 = 0x4760;

        private const long PageSize = 0x1000;

        private readonly Unicorn _uc;
        private readonly PeImage _image;
        private readonly HostStubs _stubs;
        private readonly WinRtRuntime _winRt;
        private readonly HStringHeap _strings;
        private readonly Dictionary<long, TrapSlot> _traps = new();
        private readonly List<string> _callOrder = new();
        private readonly List<string> _vtableCalls = new();
        private readonly Dictionary<string, int> _callCounts = new(StringComparer.Ordinal);
        private long _trapNext = TrapBase;

        // Unicorn keeps only native function pointers, so these delegates must be rooted
        // here or the GC will collect them mid-run.
        private readonly CodeHook _trapHook;
        private readonly MemWriteHook _watchedWriteHook;
        private readonly CodeHook _heapExecutionHook;
        private readonly EventMemHook _unmappedHook;
        private readonly BlockHook _blockHook;
        private readonly MemWriteHook _trapWriteHook;

        private long _heapNext = HeapBase + PageSize;

        /// <param name="collectBlockStats">
        /// Counts translated blocks and bytes of code executed. This costs a managed
        /// callback on every basic block, which dominates run time on long runs - it is
        /// far more expensive than emulating the instructions - so it is worth turning off
        /// once a run is measured in tens of millions of instructions.
        /// </param>
        public ArmEmulator(PeImage image, string imageDirectory, bool collectBlockStats = true)
        {
            if (image.Machine != PeImage.MachineArmNt)
            {
                throw new NotSupportedException(
                    $"Expected an ARMNT image (0x{PeImage.MachineArmNt:X4}), got 0x{image.Machine:X4}.");
            }

            _image = image;
            _uc = new Unicorn(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);

            MapImage();
            MapScratchRegions();

            // The runtime allocates its objects and vtables out of the emulated heap, so
            // it has to come after the scratch regions exist and before anything runs.
            _strings = new HStringHeap(this);
            Unwinder = new ArmUnwinder(image, this);
            ExceptionModel = new CxxExceptionModel(this, image.ImageBase);
            _winRt = new WinRtRuntime(this, _strings);
            InstallImportTraps();
            _stubs = new HostStubs(this, _winRt, _strings, imageDirectory);
            EnableFloatingPoint();

            _trapHook = OnTrapEntered;
            _watchedWriteHook = OnWatchedWrite;
            _heapExecutionHook = OnHeapExecution;
            _traceHook = OnTracePoint;
            _unmappedHook = OnUnmappedAccess;
            _blockHook = OnBlockEntered;
            _trapWriteHook = OnTrapPageWritten;

            _uc.AddCodeHook(_trapHook, null, TrapBase, TrapBase + TrapSize);
            _uc.AddEventMemHook(_unmappedHook, Common.UC_HOOK_MEM_UNMAPPED | Common.UC_HOOK_MEM_PROT, null);

            // Nothing should ever write to the trap page. Watching it is cheap because
            // legitimate traffic there is zero.
            _uc.AddMemWriteHook(_trapWriteHook, null, TrapBase, TrapBase + TrapSize);
            _uc.AddMemWriteHook(_watchedWriteHook, null, HeapBase, HeapBase + HeapSize);
            _uc.AddMemWriteHook(_watchedWriteHook, null, StackBase, StackBase + StackSize);

            // Nothing this probe puts on the heap is code - only objects, vtables and
            // buffers - so executing from there means the image was handed something that
            // was not a function pointer and jumped to it. Left alone that spends the entire
            // budget sliding through zeroed pages, which decode as Thumb no-ops: the block
            // trace fills with addresses that mean nothing and the last real call, the one
            // that explains it, scrolls out of the ring. This costs nothing until it fires,
            // because the hook only runs on instructions actually executed in range.
            _uc.AddCodeHook(_heapExecutionHook, null, HeapBase, HeapBase + HeapSize);

            if (collectBlockStats)
            {
                _uc.AddBlockHook(_blockHook, null, 1, long.MaxValue);
                BlockStatsCollected = true;
            }
        }

        /// <summary>Imports called, in execution order, with duplicates preserved.</summary>
        public IReadOnlyList<string> CallOrder => _callOrder;

        /// <summary>How many times each import was called.</summary>
        public IReadOnlyDictionary<string, int> CallCounts => _callCounts;

        /// <summary>Pages the run touched that were never mapped, and got zero-filled on demand.</summary>
        public int LazyPagesMapped { get; private set; }

        public long BlocksExecuted { get; private set; }

        public long CodeBytesExecuted { get; private set; }

        /// <summary>Whether the block counters above are being maintained at all.</summary>
        public bool BlockStatsCollected { get; }

        /// <summary>Where the image is mapped, which every RVA in it is relative to.</summary>
        public long ImageBase => _image.ImageBase;

        /// <summary>Number of import slots redirected into the trap page.</summary>
        public int TrappedImportCount => _image.Imports.Count;

        /// <summary>Calls that arrived through a synthesised vtable, in order.</summary>
        public IReadOnlyList<string> VtableCalls => _vtableCalls;

        /// <summary>WinRT runtime classes the image tried to activate, in order.</summary>
        public IReadOnlyList<string> RequestedWinRtClasses => _stubs.RequestedClasses;

        /// <summary>Runtime classes answered with a stand-in rather than an implementation.</summary>
        public IReadOnlyList<string> ImprovisedClasses => _winRt.ImprovisedClasses;

        /// <summary>How many times the image got round its own main loop.</summary>
        public int ProcessEventsCalls => _winRt.ProcessEventsCalls;

        /// <summary>Pointer events delivered to the image, and what it did with them.</summary>
        public IReadOnlyList<string> InputDelivered => _winRt.InputDelivered;

        /// <summary>The Direct3D and DXGI layer, and everything the image asked of it.</summary>
        public Direct3DRuntime Direct3D => _stubs.Direct3D;

        /// <summary>The audio engine, and everything the image asked of it.</summary>
        public XAudio2Runtime XAudio2 => _stubs.XAudio2;

        /// <summary>Points where the image asked for a throw that was never delivered.</summary>
        public IReadOnlyList<string> UndeliveredThrows => _stubs.UndeliveredThrows;

        /// <summary>How many C++ static initialisers ran during startup.</summary>
        public int StaticInitialisersRun => _stubs.StaticInitialisersRun;

        /// <summary>Initialiser tables walked and the entries in them.</summary>
        public IReadOnlyList<string> InitialiserLog => _stubs.InitialiserLog;

        /// <summary>Strings the image formatted through printf.</summary>
        public IReadOnlyList<string> FormattedStrings => _stubs.FormattedStrings;

        /// <summary>What the image asked the PPL task machinery to do.</summary>
        public IReadOnlyList<string> TaskCollectionLog => _stubs.TaskCollectionLog;

        /// <summary>The synthetic WinRT surface this image is running against.</summary>
        public WinRtRuntime WinRt => _winRt;

        /// <summary>Stack unwinding driven by the image's .pdata table.</summary>
        public ArmUnwinder Unwinder { get; }

        /// <summary>File I/O, backed by real files.</summary>
        public FileLibrary Files => _stubs.Files;

        /// <summary>Reader for the MSVC C++ exception structures.</summary>
        public CxxExceptionModel ExceptionModel { get; private set; } = null!;

        /// <summary>The stack at the point the image threw, if it did.</summary>
        public IReadOnlyList<UnwoundFrame> ThrowStack => _stubs.ThrowStack;

        /// <summary>Readable text found inside the thrown object.</summary>
        public IReadOnlyList<string> ThrownText => _stubs.ThrownText;

        /// <summary>What the image threw, if it did.</summary>
        public ThrownException? Thrown => _stubs.Thrown;

        /// <summary>Catch clauses that accept the thrown object.</summary>
        public IReadOnlyList<CatchCandidate> CatchCandidates => _stubs.CatchCandidates;

        /// <summary>Handlers entered, and where each resumed.</summary>
        public IReadOnlyList<string> TransferLog => _stubs.TransferLog;

        /// <summary>
        /// Runs from the image entry point until the instruction budget is exhausted or the
        /// CPU faults. Returns the fault message, or null if it simply ran out of budget.
        /// </summary>
        public string? RunEntryPoint(long instructionBudget)
        {
            string? fault = Run(_image.EntryPoint, instructionBudget);

            // A background callback that dies takes its own thread down, not the process.
            // Resuming the chain here is what makes that true here as well: the CPU is put
            // back exactly as it was before the callback was entered, and the run carries
            // on with whatever was queued behind it. Without this, one bad pointer on a
            // loader thread stops the whole image, and the foreground - which is where the
            // interesting lifecycle lives - never gets to run at all.
            for (int i = 0; i < MaxThreadDeaths && _abandoned is not null; i++)
            {
                FaultDomain dead = _abandoned;
                _abandoned = null;

                RestoreRegisters(dead.Registers);

                // The continuation reads r0 as the callback result. E_UNEXPECTED is the
                // honest answer, and it shows up in the trace as such.
                _uc.RegWrite(Arm.UC_ARM_REG_R0, unchecked((uint)0x8000FFFF));

                // The budget is a runaway guard, not a measurement, so each resumed segment
                // gets it afresh; MaxThreadDeaths is what bounds the total.
                fault = Run(ThumbEntry(dead.ResumeTrap), instructionBudget);
            }

            return fault;
        }

        /// <summary>
        /// How many background callbacks may die before the run gives up.
        /// </summary>
        /// <remarks>
        /// Bounded because "resume and carry on" is only useful while the deaths are
        /// independent. A callback that dies, gets abandoned, and immediately queues
        /// another copy of itself would otherwise spin here forever.
        /// </remarks>
        private const int MaxThreadDeaths = 8;

        // ---------------------------------------------------------------------
        // Fault domains
        //
        // A fault domain marks a stretch of emulated execution that stands in for a
        // separate thread, so a fatal fault inside it can be contained rather than
        // ending the run. Only deferred callbacks get one: the view lifecycle runs on
        // what a real device would call the UI thread, and a null call there is fatal
        // to the app, exactly as it would be on hardware.
        // ---------------------------------------------------------------------

        private sealed record FaultDomain(string Name, long ResumeTrap, long[] Registers);

        private FaultDomain? _domain;
        private FaultDomain? _abandoned;
        private readonly List<string> _threadDeaths = new();

        /// <summary>Background callbacks that died, and what killed each one.</summary>
        public IReadOnlyList<string> ThreadDeaths => _threadDeaths;

        /// <summary>
        /// The null call that ended the run, if one did - as opposed to the contained ones
        /// reported as thread deaths.
        /// </summary>
        public string? UncontainedNullCall { get; private set; }

        private static readonly int[] CoreRegisters =
        [
            Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
            Arm.UC_ARM_REG_R4, Arm.UC_ARM_REG_R5, Arm.UC_ARM_REG_R6, Arm.UC_ARM_REG_R7,
            Arm.UC_ARM_REG_R8, Arm.UC_ARM_REG_R9, Arm.UC_ARM_REG_R10, Arm.UC_ARM_REG_R11,
            Arm.UC_ARM_REG_R12, Arm.UC_ARM_REG_SP, Arm.UC_ARM_REG_LR, Arm.UC_ARM_REG_CPSR,
        ];

        private long[] CaptureRegisters()
        {
            long[] values = new long[CoreRegisters.Length];
            for (int i = 0; i < CoreRegisters.Length; i++)
            {
                values[i] = _uc.RegRead(CoreRegisters[i]);
            }

            return values;
        }

        private void RestoreRegisters(long[] values)
        {
            for (int i = 0; i < CoreRegisters.Length; i++)
            {
                _uc.RegWrite(CoreRegisters[i], values[i]);
            }
        }

        /// <summary>An address the CPU can never reach, used to mean "no stop point".</summary>
        /// <remarks>
        /// Not zero. Unicorn takes the stop address literally, so passing 0 quietly means
        /// "stop when PC reaches 0" - which is exactly what a null call does. Every run
        /// here used to end that way and report an exhausted budget, hiding both the null
        /// call and the fact that the budget was nowhere near spent.
        /// </remarks>
        private const long NeverReached = -1;

        public string? Run(long startAddress, long instructionBudget)
            => Run(startAddress, NeverReached, instructionBudget);

        /// <summary>
        /// Runs until the CPU reaches <paramref name="untilAddress"/>, the budget runs out,
        /// or it faults.
        /// </summary>
        public string? Run(long startAddress, long untilAddress, long instructionBudget)
        {
            try
            {
                _uc.EmuStart(startAddress, untilAddress, 0, instructionBudget);
                return null;
            }
            catch (UnicornEngineException ex)
            {
                return ex.Message;
            }
        }

        public long ReadRegister(int registerId) => _uc.RegRead(registerId);

        public void WriteRegister(int registerId, long value) => _uc.RegWrite(registerId, value);

        /// <summary>
        /// Writes emulated memory on behalf of a host stub, refusing anything that would
        /// land in the trap page.
        /// </summary>
        /// <remarks>
        /// Stubs write through pointers the image supplies, and the image is perfectly
        /// capable of supplying a bad one - a field never initialised, a placeholder object
        /// mistaken for a buffer. Left unchecked, such a write lands wherever it likes,
        /// and the worst place it can land is here: the trap page holds the emulator's own
        /// <c>bx r12</c> instructions.
        ///
        /// This is not hypothetical. A stub once overwrote the low byte of the
        /// QueryPerformanceCounter trap, turning <c>bx r12</c> into <c>bx r0</c>; the next
        /// call through it jumped to whatever r0 happened to hold, and the run died
        /// hundreds of instructions later with nothing to connect it back. Refusing the
        /// write turns that into a diagnostic at the moment it happens.
        /// </remarks>
        public void WriteMemory(long address, byte[] data)
        {
            NoteHostWrite(address, data.Length);
            NoteOverflow(address, data.Length);

            if (address < TrapBase + TrapSize && address + data.Length > TrapBase)
            {
                RejectedWrites.Add(
                    $"refused a {data.Length}-byte write to 0x{address:X8} - that is the trap page" +
                    (_lastTrap is null ? string.Empty : $" (during {_lastTrap})"));
                return;
            }

            _uc.MemWrite(address, data);
        }

        /// <summary>
        /// Writes a host stub tried to make into the emulator's own machinery. Each one is
        /// a bad pointer from the image, and worth seeing.
        /// </summary>
        public List<string> RejectedWrites { get; } = new();

        /// <summary>
        /// Checks that every trap slot still holds its <c>bx r12</c>.
        /// </summary>
        /// <remarks>
        /// The trap page is the one piece of memory that must never change after setup, and
        /// a single altered byte there is catastrophic and almost untraceable: the call
        /// still arrives, the host handler still runs, and then the CPU leaves through the
        /// wrong instruction. Verifying it costs one pass over a few hundred slots and
        /// turns that into a named slot.
        /// </remarks>
        public List<string> VerifyTrapPage()
        {
            List<string> damaged = new();

            foreach ((long address, TrapSlot slot) in _traps.OrderBy(entry => entry.Key))
            {
                ushort instruction = BitConverter.ToUInt16(ReadMemory(address, 2));
                if (instruction != ThumbBxR12)
                {
                    damaged.Add($"0x{address:X8} holds 0x{instruction:X4}, expected 0x{ThumbBxR12:X4} ({slot.Name})");
                }
            }

            return damaged;
        }

        public byte[] ReadMemory(long address, int length)
        {
            byte[] buffer = new byte[length];
            _uc.MemRead(address, buffer);
            return buffer;
        }

        public uint ReadUInt32(long address) => BitConverter.ToUInt32(ReadMemory(address, 4));

        /// <summary>
        /// Reads a word, answering <paramref name="fallback"/> if the address is not mapped.
        /// </summary>
        /// <remarks>
        /// For walking the image's own tables. A malformed or misread table entry points
        /// wherever it likes, and the walker's job is to notice that and give up on that
        /// entry - not to take the process with it.
        /// </remarks>
        public uint ReadUInt32(long address, uint fallback)
        {
            try
            {
                return ReadUInt32(address);
            }
            catch (UnicornEngineException)
            {
                return fallback;
            }
        }

        public void WriteUInt32(long address, uint value) => WriteMemory(address, BitConverter.GetBytes(value));

        public void WriteUInt64(long address, ulong value) => WriteMemory(address, BitConverter.GetBytes(value));

        /// <summary>Writes a WinRT <c>boolean</c>, which is one byte and not four.</summary>
        public void WriteBoolean(long address, bool value) => WriteMemory(address, [value ? (byte)1 : (byte)0]);

        /// <summary>Writes a 32-bit float, the element type of Rect, Point and Size.</summary>
        public void WriteSingle(long address, float value) => WriteMemory(address, BitConverter.GetBytes(value));

        /// <summary>Reads a null-terminated UTF-16 string, as every WinRT class name is.</summary>
        public string ReadUtf16String(long address, int maxChars = 256)
        {
            if (address == 0)
            {
                return string.Empty;
            }

            System.Text.StringBuilder text = new();
            for (int i = 0; i < maxChars; i++)
            {
                ushort unit = BitConverter.ToUInt16(ReadMemory(address + (i * 2), 2));
                if (unit == 0)
                {
                    break;
                }

                text.Append((char)unit);
            }

            return text.ToString();
        }

        public Guid ReadGuid(long address) => new(ReadMemory(address, 16));

        /// <summary>
        /// The allocator behind every malloc-shaped stub: a bump pointer, plus exact-size
        /// reuse of anything that has been freed.
        /// </summary>
        /// <remarks>
        /// It never freed at all until the image reached its main loop, which was fine while
        /// a run was a few hundred thousand instructions and stopped at the first fault. A
        /// running game is a different proposition: this one turns over about 180 KB a frame,
        /// so a quarter of a gigabyte buys roughly fourteen hundred frames and then the run
        /// ends for a reason that has nothing to do with the image.
        ///
        /// Reuse is exact-size and nothing else - no splitting, no coalescing, no adjacent
        /// merge. A game allocates the same handful of sizes over and over, so exact-size
        /// recovers nearly all of it for about fifteen lines; splitting would recover the
        /// rest and bring with it every classic allocator bug, in a component whose whole
        /// job is to make the image's bugs visible rather than to add its own.
        /// </remarks>
        public long AllocateHeap(long size)
        {
            long aligned = Bucket(size);

            if (_freeBlocks.TryGetValue(aligned, out Stack<long>? bucket) && bucket.Count > 0)
            {
                long reused = bucket.Pop();
                RecordAllocation(reused, aligned);
                BytesReused += aligned;
                return reused;
            }

            if (_heapNext + aligned > HeapBase + HeapSize)
            {
                // Throwing here would escape through a hook and take the whole run with it,
                // losing the diagnostics that explain why. Stop cleanly instead and let the
                // caller report - malloc returning null is a legal answer in any case.
                HeapExhausted = true;
                _uc.EmuStop();
                return 0;
            }

            long pointer = _heapNext;
            _heapNext += aligned;

            // Every block in the emulated heap comes through here, so recording the
            // requester makes "who allocated this address" answerable directly rather than
            // by inference. The caller is the emulated return address, which names the
            // code that asked; the source names the stub it asked through.
            RecordAllocation(pointer, aligned);
            long caller = _uc.RegRead(Arm.UC_ARM_REG_LR) & ~1L;

            if (_watchAllocationsFrom.Contains(caller))
            {
                WatchWrites(pointer, aligned, $"block from 0x{caller:X8}");

                // Who asked, not just where from. A block handed out by a shared helper -
                // std::_Allocate, an operator new wrapper, a pool - records that helper as
                // the requester every time, which identifies nothing. The stack above it is
                // what names the caller that chose the size.
                if (_allocationStacks.Count < 24)
                {
                    _allocationStacks.Add(
                        $"0x{pointer:X8} {aligned} bytes from 0x{caller:X8}" +
                        Newline + ScanStack(48));
                }
            }

            return pointer;
        }

        // ---------------------------------------------------------------------
        // Write watches
        //
        // "Which instruction put that value there" is the question static reading of the
        // disassembly is worst at answering, because the write is usually nowhere near the
        // read that trips over it. A watch answers it directly.
        // ---------------------------------------------------------------------

        // ---------------------------------------------------------------------
        // Instruction traces
        //
        // A watch says what a piece of memory became; a trace says what a register held
        // when a particular instruction ran. The second question is the one that comes up
        // once the image is deep enough that its own arithmetic is the suspect - a
        // destination computed as base + stride * index is three values, and the only way
        // to tell which of them is wrong is to look at them.
        // ---------------------------------------------------------------------

        private readonly List<string> _traceLog = new();
        private readonly Dictionary<long, string> _tracePoints = new();
        private readonly CodeHook _traceHook;

        /// <summary>Registers captured each time a traced instruction ran.</summary>
        public IReadOnlyList<string> TraceLog => _traceLog;

        /// <summary>
        /// Logs the register file every time the CPU reaches an address.
        /// </summary>
        public void TraceAt(long address, string label)
        {
            long target = address & ~1L;
            if (!_tracePoints.TryAdd(target, label))
            {
                return;
            }

            _uc.AddCodeHook(_traceHook, null, target, target);
        }

        /// <summary>Few enough to read; a trace point inside a loop fills this instantly.</summary>
        private const int TraceLimit = 40;

        private void OnTracePoint(Unicorn uc, long address, int size, object? userData)
        {
            if (_traceLog.Count >= TraceLimit || !_tracePoints.TryGetValue(address, out string? label))
            {
                return;
            }

            int[] ids =
            [
                Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
                Arm.UC_ARM_REG_R4, Arm.UC_ARM_REG_R5, Arm.UC_ARM_REG_R6, Arm.UC_ARM_REG_R7,
                Arm.UC_ARM_REG_R8, Arm.UC_ARM_REG_R9, Arm.UC_ARM_REG_R10, Arm.UC_ARM_REG_R11,
                Arm.UC_ARM_REG_R12, Arm.UC_ARM_REG_SP, Arm.UC_ARM_REG_LR,
            ];
            string[] names = ["r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7",
                              "r8", "r9", "r10", "r11", "r12", "sp", "lr"];

            var text = new System.Text.StringBuilder($"0x{address:X8} {label}: ");
            for (int i = 0; i < ids.Length; i++)
            {
                text.Append($"{names[i]}=0x{_uc.RegRead(ids[i]):X8} ");
            }

            _traceLog.Add(text.ToString());
        }

        private readonly HashSet<long> _watchAllocationsFrom = new();
        private readonly List<string> _allocationStacks = new();

        /// <summary>Where each watched allocation was asked for, as a call chain.</summary>
        public IReadOnlyList<string> AllocationStacks => _allocationStacks;
        private readonly List<(long Start, long End, string Label)> _watches = new();
        private readonly List<string> _writeLog = new();

        /// <summary>Writes seen by a watch, in order.</summary>
        public IReadOnlyList<string> WriteLog => _writeLog;

        /// <summary>
        /// Watches every heap block allocated on behalf of the code at these addresses.
        /// </summary>
        /// <remarks>
        /// Keyed on the requesting address rather than the block address because the block
        /// address is not knowable before the run: it depends on every allocation before it.
        /// The requesting instruction is stable across runs, which is what makes this usable
        /// as a diagnostic you can set up in advance.
        /// </remarks>
        public void WatchAllocationsFrom(params long[] callers)
        {
            foreach (long caller in callers)
            {
                _watchAllocationsFrom.Add(caller & ~1L);
            }
        }

        /// <summary>Logs every write into a range, with the instruction that made it.</summary>
        public void WatchWrites(long start, long length, string label)
        {
            _watches.Add((start, start + length, label));
            _writeLog.Add($"watching 0x{start:X8}..0x{start + length:X8} ({label})");
        }

        private readonly List<string> _overflows = new();

        /// <summary>Host writes that ran past the end of the block they started in.</summary>
        public IReadOnlyList<string> Overflows => _overflows;

        /// <summary>
        /// Notices a host stub writing past the end of the block it was given.
        /// </summary>
        /// <remarks>
        /// The emulated heap has no guard pages and no headers, so an overrun simply lands
        /// on the next allocation and is discovered later as a field that holds the wrong
        /// thing - a vector element that is a float, a vtable pointer that is a string. By
        /// then the write is thousands of calls in the past and nothing connects the two.
        ///
        /// Only host writes are checked, and that is the right boundary: emulated code
        /// overrunning its own buffer is the image's business and may well be deliberate,
        /// but a stub overrunning is either a size this side computed wrongly or a length
        /// this side failed to clamp. Both are ours.
        /// </remarks>
        private void NoteOverflow(long address, int length)
        {
            if (address < HeapBase || address >= HeapBase + HeapSize || length <= 0)
            {
                return;
            }

            long room = AllocationSizeOf(address);
            if (room == 0 || length <= room)
            {
                return;
            }

            // lr still holds the emulated return address, which names the code that asked -
            // the stub itself is never the culprit, only the messenger.
            string entry = $"{_lastTrap ?? "host"} called from 0x{_uc.RegRead(Arm.UC_ARM_REG_LR) & ~1L:X8} " +
                           $"wrote {length} bytes into {room} bytes of room at " +
                           $"0x{address:X8} ({DescribeAllocation(address)})" +
                           Newline + "      called from:" + Newline + ScanStack(64);
            if (_overflows.Count < 32 && !_overflows.Contains(entry, StringComparer.Ordinal))
            {
                _overflows.Add(entry);
            }
        }

        /// <summary>
        /// Records a write a host stub made, which Unicorn's own hook cannot see.
        /// </summary>
        /// <remarks>
        /// UC_HOOK_MEM_WRITE fires for writes the emulated CPU makes. A stub writing through
        /// the API - memcpy, fread, a WinRT out-parameter - goes straight into the memory
        /// without passing the hook, so a watch that only listened to the CPU would report
        /// the last emulated write and miss the host write that came after it. That is not a
        /// small gap: half of everything written in this run is written by a stub.
        /// </remarks>
        private void NoteHostWrite(long address, int length)
        {
            if (_watches.Count == 0)
            {
                return;
            }

            foreach ((long start, long end, string label) in _watches)
            {
                if (address + length <= start || address >= end)
                {
                    continue;
                }

                if (_writeLog.Count < WriteLogLimit)
                {
                    _writeLog.Add(
                        $"{_lastTrap ?? "host"} wrote {length} bytes " +
                        $"to +0x{address - start:X2} of {label} (host side)");
                }

                return;
            }
        }

        private void OnWatchedWrite(Unicorn uc, long address, int size, long value, object? user)
        {
            foreach ((long start, long end, string label) in _watches)
            {
                if (address < start || address >= end)
                {
                    continue;
                }

                if (_writeLog.Count < WriteLogLimit)
                {
                    _writeLog.Add(
                        $"0x{_uc.RegRead(Arm.UC_ARM_REG_PC) & ~1L:X8} wrote 0x{value:X8} ({size} bytes) " +
                        $"to +0x{address - start:X2} of {label}");
                }

                return;
            }
        }

        /// <summary>Enough to see a pattern, few enough to read. A watch on a hot buffer
        /// would otherwise fill the report with a megabyte of memcpy.</summary>
        private const int WriteLogLimit = 400;

        private readonly List<(long Start, long Size, string Source, long Caller)> _allocations = new();
        private readonly Dictionary<long, int> _blockAt = new();
        private readonly Dictionary<long, Stack<long>> _freeBlocks = new();

        /// <summary>Bytes handed back out of the free list rather than taken from the bump.</summary>
        public long BytesReused { get; private set; }

        /// <summary>Blocks currently on the free list.</summary>
        public int FreeBlockCount => _freeBlocks.Values.Sum(b => b.Count);

        /// <summary>
        /// Rounds a request up to a size the free list can match again.
        /// </summary>
        /// <remarks>
        /// Reuse here is exact-size, so it only helps when the same size comes back - and a
        /// game asking for 1,500 bytes and then 1,504 gets no benefit from having freed the
        /// first. Sixteen-byte granularity below a kilobyte keeps small objects tight;
        /// powers of two above it trade some waste for a free list that actually hits.
        ///
        /// The overflow guard reads its room from this rounded size, so a large block gets a
        /// little slack it did not ask for. That is the same slack a real allocator's size
        /// classes give, and it is worth stating: an overrun of a few bytes past a big
        /// allocation is no longer detected. Overruns of the size that have mattered here -
        /// 144 bytes into 16 - still are.
        /// </remarks>
        private static long Bucket(long size)
        {
            long aligned = Math.Max(16, (size + 15) & ~15L);
            if (aligned <= 1024)
            {
                return aligned;
            }

            long power = 2048;
            while (power < aligned && power < (1L << 40))
            {
                power <<= 1;
            }

            return power;
        }

        private void RecordAllocation(long pointer, long size)
        {
            var record = (pointer, size, _lastTrap ?? "host setup", _uc.RegRead(Arm.UC_ARM_REG_LR));

            // A reused block keeps its place in the list - the list is ordered by address and
            // the binary search below depends on that - and takes on its new requester.
            if (_blockAt.TryGetValue(pointer, out int existing))
            {
                _allocations[existing] = record;
                return;
            }

            _blockAt[pointer] = _allocations.Count;
            _allocations.Add(record);
        }

        /// <summary>
        /// Returns a block to the free list. Anything that is not the start of a live block
        /// is ignored, which covers the two cases a stub cannot distinguish: a null pointer,
        /// and a pointer into the middle of something.
        /// </summary>
        public void FreeHeap(long pointer)
        {
            if (pointer == 0 || !_blockAt.TryGetValue(pointer, out int index))
            {
                return;
            }

            long size = _allocations[index].Size;
            if (!_freeBlocks.TryGetValue(size, out Stack<long>? bucket))
            {
                bucket = new Stack<long>();
                _freeBlocks[size] = bucket;
            }

            // Freeing twice would put the same address in the bucket twice and hand it to two
            // callers at once, turning an image bug into an emulator one.
            if (!bucket.Contains(pointer))
            {
                bucket.Push(pointer);
            }
        }

        public int AllocationCount => _allocations.Count;

        /// <summary>
        /// The size of the block containing an address, or zero if it is not one of ours.
        /// </summary>
        /// <remarks>
        /// The allocator never frees, so it does not need a header - but realloc does need
        /// to know how much of the old block to copy. The provenance list already records
        /// every block, so the answer is there for the asking.
        /// </remarks>
        public long AllocationSizeOf(long address)
        {
            int found = BlockContaining(address);
            return found < 0 ? 0 : _allocations[found].Start + _allocations[found].Size - address;
        }

        /// <summary>
        /// The index of the block containing an address, or -1.
        /// </summary>
        /// <remarks>
        /// Binary search, because this is on the path of every host write now that the
        /// overflow guard exists - a linear scan of a hundred thousand blocks per memcpy is
        /// not a diagnostic, it is a different program. The list is ordered by address
        /// because the bump allocator only ever hands out increasing ones, and a reused
        /// block keeps its original place.
        /// </remarks>
        private int BlockContaining(long address)
        {
            int low = 0;
            int high = _allocations.Count - 1;

            while (low <= high)
            {
                int middle = (low + high) / 2;
                (long start, long size, _, _) = _allocations[middle];

                if (address < start)
                {
                    high = middle - 1;
                }
                else if (address >= start + size)
                {
                    low = middle + 1;
                }
                else
                {
                    return middle;
                }
            }

            return -1;
        }

        /// <summary>Identifies the heap block containing an address, and who asked for it.</summary>
        public string DescribeAllocation(long address)
        {
            int found = BlockContaining(address);
            if (found >= 0)
            {
                (long start, long size, string source, long caller) = _allocations[found];
                {
                    string offset = address == start ? string.Empty : $" +0x{address - start:X}";
                    string contents = string.Join(
                        " ", ReadMemory(start, (int)Math.Min(32, size)).Select(b => b.ToString("X2")));

                    return $"0x{start:X8}{offset}, {size} bytes, from {source}" +
                           (caller == 0 ? string.Empty : $", requested by code at 0x{caller & ~1L:X8}") +
                           $"; block starts {contents}";
                }
            }

            return "not a block from the emulated heap";
        }

        /// <summary>True when the run ended because the bump allocator ran out of room.</summary>
        public bool HeapExhausted { get; private set; }

        /// <summary>Why the run was stopped deliberately, if it was.</summary>
        public string? StopReason { get; private set; }

        /// <summary>
        /// Ends the run with an explanation, for conditions that are fatal but are not CPU
        /// faults - the image doing something the host cannot honour.
        /// </summary>
        public void Stop(string reason)
        {
            StopReason ??= reason;
            _uc.EmuStop();
        }

        /// <summary>Bytes handed out by <see cref="AllocateHeap"/> so far.</summary>
        public long HeapUsed => _heapNext - HeapBase;

        private void MapImage()
        {
            _uc.MemMap(_image.ImageBase, Align(_image.SizeOfImage, PageSize), Common.UC_PROT_ALL);
            _uc.MemWrite(_image.ImageBase, _image.Raw[..(int)_image.SizeOfHeaders].ToArray());

            foreach (PeSection section in _image.Sections)
            {
                if (section.RawSize == 0)
                {
                    continue;
                }

                byte[] bytes = _image.Raw.Slice((int)section.RawPointer, (int)section.RawSize).ToArray();
                _uc.MemWrite(_image.ImageBase + section.VirtualAddress, bytes);
            }
        }

        private void MapScratchRegions()
        {
            _uc.MemMap(StackBase, StackSize, Common.UC_PROT_ALL);
            _uc.MemMap(HeapBase, HeapSize, Common.UC_PROT_ALL);
            // Read and execute, but NOT write. The trap page is the emulator's own machinery
            // - sixteen thousand `bx r12` instructions - and a single stray store into it
            // turns an import into a jump to wherever a register happens to point, thousands
            // of instructions later and with nothing connecting the two.
            //
            // Watching for the write was not enough: UC_HOOK_MEM_WRITE fires *after* the
            // store lands and cannot veto it, so the log said "refused" while the page was
            // being corrupted anyway. Taking the permission away is what actually refuses.
            // Our own writes are unaffected: uc_mem_write bypasses page protection, which is
            // exactly the asymmetry this needs.
            _uc.MemMap(TrapBase, TrapSize, Common.UC_PROT_READ | Common.UC_PROT_EXEC);
            _uc.MemMap(TebBase, TebSize, Common.UC_PROT_ALL);

            _uc.RegWrite(Arm.UC_ARM_REG_SP, StackBase + (StackSize * 3 / 4));

            // Windows on ARM reaches the thread environment block through CP15 TPIDRURW
            // (c13, c0, 2). Unicorn 2.1.3 accepts UC_ARM_REG_C13_C0_2 but treats it as a
            // no-op and warns, so only the read-only sibling actually takes a value here.
            //
            // KNOWN GAP: nothing has needed a real TEB yet - the startup path reaches the
            // first WinRT activation without dereferencing it, and any stray access lands
            // on a lazily mapped zero page. Once threads or SEH come into scope this has to
            // be done properly through UC_ARM_REG_CP_REG, the generic coprocessor accessor.
            _uc.RegWrite(Arm.UC_ARM_REG_C13_C0_3, TebBase);
        }

        /// <summary>What a trap slot stands in for, which decides how a hit is reported.</summary>
        private enum TrapKind
        {
            /// <summary>An entry in the image's import address table.</summary>
            Import,

            /// <summary>A slot in a vtable synthesised by <see cref="WinRtRuntime"/>.</summary>
            VtableMethod,

            /// <summary>Where a host-to-emulated call comes back to. Plumbing, not a call.</summary>
            Return,
        }

        private sealed record TrapSlot(string Name, TrapKind Kind, Action? Handler);

        /// <summary>
        /// Reserves a trap slot and returns its address. Branching to the returned address
        /// runs <paramref name="handler"/> on the host and then returns to the caller.
        /// </summary>
        /// <remarks>
        /// The address is even. Callers storing it in a function pointer must set bit 0 so
        /// the CPU enters Thumb state on arrival - see <see cref="ThumbEntry"/>.
        /// </remarks>
        public long RegisterVtableMethod(string name, Action handler)
            => AllocateTrapSlot(name, TrapKind.VtableMethod, handler);

        /// <summary>Turns a trap address into a value safe to store as an ARM function pointer.</summary>
        public static long ThumbEntry(long trapAddress) => trapAddress | 1;

        /// <summary>
        /// Builds an object with a real vtable whose every slot traps back to the host.
        /// </summary>
        /// <remarks>
        /// The answer to "this function should return an object and does not". Returning
        /// zero is the worst option available: the caller dereferences it immediately.
        /// A shaped object costs a few words, keeps the image running, and names whatever
        /// it calls - which is how the next thing to implement gets found.
        /// </remarks>
        /// <summary>
        /// How much space a stand-in object gets beyond its vtable pointer.
        /// </summary>
        /// <remarks>
        /// Generous on purpose. The image thinks it holds a real class and reads and writes
        /// members at whatever offsets that class has - `[this + 0x18]` and further. An
        /// eight-byte stand-in means those reads fall into the *next* allocation, which
        /// returns a neighbour's data instead of the zero the image would treat as "not
        /// set", and those writes corrupt it. Sized to cover any plausible class, and left
        /// zeroed so an unset member reads as unset.
        /// </remarks>
        private const int ShapedObjectSize = 256;

        public long CreateShapedObject(string name, int slotCount = 16)
        {
            long instance = AllocateHeap(ShapedObjectSize);
            _uc.MemWrite(instance, new byte[ShapedObjectSize]);
            _uc.MemWrite(instance, BitConverter.GetBytes((uint)CreateShapedVtable(name, slotCount)));
            _uc.MemWrite(instance + 4, BitConverter.GetBytes(1u));
            return instance;
        }

        /// <summary>
        /// Builds just a vtable, for giving an object that something else allocated.
        /// </summary>
        /// <remarks>
        /// An imported constructor that is not implemented leaves its object exactly as the
        /// allocator handed it over - all zeros, including the vtable pointer. The object
        /// looks fine until the first virtual call, which goes through null. Writing a
        /// shaped vtable into it makes that call arrive here with a name instead.
        /// </remarks>
        public long CreateShapedVtable(string name, int slotCount = 16)
        {
            long vtable = AllocateHeap(slotCount * 4);

            for (int slot = 0; slot < slotCount; slot++)
            {
                int captured = slot;
                long trap = RegisterVtableMethod($"{name}::slot{captured}", () =>
                {
                    ShapedObjectCalls.Add(
                        $"{name}::slot{captured}  this=0x{_uc.RegRead(Arm.UC_ARM_REG_R0):X8} " +
                        $"r1=0x{_uc.RegRead(Arm.UC_ARM_REG_R1):X8}");

                    _uc.RegWrite(Arm.UC_ARM_REG_R0, 0);
                });

                _uc.MemWrite(vtable + (slot * 4), BitConverter.GetBytes((uint)ThumbEntry(trap)));
            }

            return vtable;
        }

        /// <summary>Calls made against shaped stand-in objects - the next things to implement.</summary>
        public List<string> ShapedObjectCalls { get; } = new();

        /// <summary>
        /// Calls a function made of emulated ARM code from a host trap handler, and runs
        /// <paramref name="onReturn"/> when it returns.
        /// </summary>
        /// <remarks>
        /// Only legal from inside a trap handler, and it does not block: it arranges the
        /// registers so that the <c>bx r12</c> in the trap slot tail-calls
        /// <paramref name="function"/> instead of returning, with lr pointing at a freshly
        /// minted return trap. Control comes back to the host when the emulated function
        /// returns, at which point <paramref name="onReturn"/> runs.
        ///
        /// <paramref name="onReturn"/> inherits the same contract as any handler and MUST
        /// set r12, because lr still points at the return trap - leaving it alone would
        /// send the CPU straight back here forever. Typically it restores the return
        /// address that the original caller was going to use.
        /// </remarks>
        public long CallEmulated(string debugName, long function, ReadOnlySpan<long> arguments, Action onReturn)
        {
            if (arguments.Length > 4)
            {
                throw new ArgumentException(
                    "Only the four register arguments are supported; stack arguments are not.",
                    nameof(arguments));
            }

            int[] argumentRegisters =
            [
                Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
            ];

            for (int i = 0; i < arguments.Length; i++)
            {
                _uc.RegWrite(argumentRegisters[i], arguments[i]);
            }

            long returnTrap = AllocateTrapSlot($"{debugName}<-return", TrapKind.Return, onReturn);

            _uc.RegWrite(Arm.UC_ARM_REG_LR, ThumbEntry(returnTrap));
            _uc.RegWrite(Arm.UC_ARM_REG_R12, function);
            return returnTrap;
        }

        /// <summary>The return address the current trap would have gone back to.</summary>
        public long ReturnAddress => _uc.RegRead(Arm.UC_ARM_REG_LR);

        private readonly Queue<(string Name, long Function, long[] Arguments)> _deferred = new();
        private readonly List<string> _deferredLog = new();

        /// <summary>Deferred callbacks queued and run, in order.</summary>
        public IReadOnlyList<string> DeferredLog => _deferredLog;

        /// <summary>
        /// Queues emulated code to be called later rather than now.
        /// </summary>
        /// <remarks>
        /// This is what stands in for a background thread. Both WinRT's ThreadPool and
        /// ConcRT's scheduler hand over work expecting it to run somewhere else while the
        /// caller carries on; with one CPU the closest honest equivalent is to run it after
        /// the caller has finished, not during. Running it during is measurably worse than
        /// not running it at all - the handler sees half-built state and dies.
        /// </remarks>
        public void QueueDeferredCall(string name, long function, params long[] arguments)
        {
            _deferred.Enqueue((name, function, arguments));
            _deferredLog.Add($"queued {name} at 0x{function:X8} ({_deferred.Count} pending)");
        }

        public int PendingDeferredCalls => _deferred.Count;

        /// <summary>How many times the image blocked and let queued work run instead.</summary>
        public int Yields { get; private set; }

        /// <summary>
        /// Runs one queued callback on behalf of a caller that is blocking, then returns to
        /// that caller. True if there was anything to run.
        /// </summary>
        /// <remarks>
        /// This is the piece that turns "deferred callbacks" into something a producer and a
        /// consumer can actually hand off between. Draining only between view lifecycle steps
        /// works right up until the image enters its main loop and never leaves it: from
        /// there the game polls a flag that background work is supposed to set, and with no
        /// yield point the queue behind that flag never runs. Angry Birds Rio does exactly
        /// this - it spin-waits on a byte, calling Concurrency::wait each time round, and
        /// burned an eight million instruction budget on 2.8 million of those calls.
        ///
        /// Every blocking primitive the image has is therefore a yield point, and this is
        /// what they call. It is cooperative scheduling: work runs only where the image has
        /// said it is willing to be interrupted, which is both safe and, on a single CPU,
        /// the only honest thing to do.
        /// </remarks>
        public bool YieldToDeferredWork()
        {
            if (_deferred.Count == 0)
            {
                return false;
            }

            (string name, long function, long[] arguments) = _deferred.Dequeue();
            _deferredLog.Add($"-> {name} at 0x{function:X8} (from a yield)");
            Yields++;

            long resume = ReturnAddress;
            long[] snapshot = CaptureRegisters();

            long resumeTrap = CallEmulated(name, function, arguments, onReturn: () =>
            {
                _domain = null;
                _deferredLog.Add($"   {name} returned 0x{_uc.RegRead(Arm.UC_ARM_REG_R0):X8}");
                ContinueAt(resume);
            });

            _domain = new FaultDomain(name, resumeTrap, snapshot);
            return true;
        }

        /// <summary>
        /// Runs every queued callback to completion, then continues with
        /// <paramref name="onDrained"/>.
        /// </summary>
        /// <remarks>
        /// A continuation chain, because each callback is emulated code that only
        /// "returns" as an event. <paramref name="onDrained"/> inherits the trap-handler
        /// contract and must set r12.
        /// </remarks>
        public void DrainDeferredCalls(Action onDrained)
        {
            if (_deferred.Count == 0)
            {
                onDrained();
                return;
            }

            (string name, long function, long[] arguments) = _deferred.Dequeue();
            _deferredLog.Add($"-> {name} at 0x{function:X8}");

            // Snapshot before the arguments go in, so an abandoned callback resumes with
            // the caller registers rather than its own half-used ones.
            long[] snapshot = CaptureRegisters();

            long resumeTrap = CallEmulated(name, function, arguments, onReturn: () =>
            {
                _domain = null;
                _deferredLog.Add($"   {name} returned 0x{_uc.RegRead(Arm.UC_ARM_REG_R0):X8}");
                DrainDeferredCalls(onDrained);
            });

            _domain = new FaultDomain(name, resumeTrap, snapshot);
        }

        /// <summary>
        /// True when an address is a stack slot. Used to tell a caller's scratch local from
        /// a pointer to a live object, so an unknown method can safely blank an out-parameter.
        /// </summary>
        public static bool IsStackAddress(long address)
            => address >= StackBase && address < StackBase + StackSize;

        /// <summary>
        /// Sends the CPU to <paramref name="address"/> when the current trap finishes,
        /// instead of returning to its caller.
        /// </summary>
        public void ContinueAt(long address) => _uc.RegWrite(Arm.UC_ARM_REG_R12, address);

        private long AllocateTrapSlot(string name, TrapKind kind, Action? handler)
        {
            if (_trapNext + 4 > TrapBase + TrapSize)
            {
                throw new InvalidOperationException($"Trap page is full ({TrapSize} bytes).");
            }

            long slot = _trapNext;
            _trapNext += 4;

            _uc.MemWrite(slot, BitConverter.GetBytes(ThumbBxR12));
            _traps[slot] = new TrapSlot(name, kind, handler);
            return slot;
        }

        /// <summary>
        /// Imports that are variables, not functions.
        /// </summary>
        /// <remarks>
        /// A DLL can export data as well as code, and the CRT does: <c>_fmode</c> and
        /// <c>_commode</c> are ints, <c>_acmdln</c> is a string pointer, <c>_HUGE</c> and
        /// <c>_FInf</c> are floating-point constants. Their IAT slots hold the address of a
        /// variable, and startup code writes *through* them.
        ///
        /// Pointing one at a trap is therefore actively destructive rather than merely
        /// unimplemented: the image stores through what it thinks is a variable and lands
        /// in the trap page, silently rewriting the emulator's own instructions. That is
        /// exactly what happened - a four-byte store through <c>_fmode</c> clipped the low
        /// byte of a neighbouring trap, turning its <c>bx r12</c> into <c>bx r0</c>, and the
        /// next call through that import left for whatever r0 happened to hold.
        /// </remarks>
        private static readonly Dictionary<string, byte[]> DataImports = new(StringComparer.Ordinal)
        {
            ["_fmode"] = BitConverter.GetBytes(0),                       // _O_TEXT
            ["_commode"] = BitConverter.GetBytes(0),
            ["_HUGE"] = BitConverter.GetBytes(double.PositiveInfinity),
            ["_FInf"] = BitConverter.GetBytes(float.PositiveInfinity),
            ["_acmdln"] = BitConverter.GetBytes(0),                      // char*, filled in below
        };

        /// <summary>Data imports given a real variable instead of a trap.</summary>
        public List<string> DataImportCells { get; } = new();

        private void InstallImportTraps()
        {
            foreach (ImportedFunction import in _image.Imports)
            {
                long slotAddress = _image.ImageBase + import.IatSlotRva;

                if (DataImports.TryGetValue(import.Name, out byte[]? initial))
                {
                    long cell = AllocateHeap(Math.Max(8, initial.Length));
                    _uc.MemWrite(cell, initial);

                    // _acmdln points at the command line, so it needs a string to point to.
                    if (import.Name == "_acmdln")
                    {
                        long commandLine = AllocateHeap(8);
                        _uc.MemWrite(commandLine, [0, 0, 0, 0, 0, 0, 0, 0]);
                        _uc.MemWrite(cell, BitConverter.GetBytes((uint)commandLine));
                    }

                    _uc.MemWrite(slotAddress, BitConverter.GetBytes((uint)cell));
                    DataImportCells.Add($"{import.FullName} -> variable at 0x{cell:X8}");
                    continue;
                }

                long trap = AllocateTrapSlot(import.FullName, TrapKind.Import, handler: null);

                // The low bit tells the CPU to enter Thumb state when it branches here.
                _uc.MemWrite(slotAddress, BitConverter.GetBytes((uint)ThumbEntry(trap)));
            }
        }

        private void EnableFloatingPoint()
        {
            // Grant cp10/cp11 access, then set FPEXC.EN, or every VFP and NEON instruction
            // in the image faults as undefined.
            long cpacr = _uc.RegRead(Arm.UC_ARM_REG_C1_C0_2);
            _uc.RegWrite(Arm.UC_ARM_REG_C1_C0_2, cpacr | (0xFL << 20));
            _uc.RegWrite(Arm.UC_ARM_REG_FPEXC, 0x40000000L);
        }

        private void OnTrapEntered(Unicorn uc, long address, int size, object? userData)
        {
            if (!_traps.TryGetValue(address, out TrapSlot? slot))
            {
                return;
            }

            // Default the tail-call register to the return address, so the bx r12 sitting
            // in this slot behaves as a plain return unless the handler says otherwise.
            _uc.RegWrite(Arm.UC_ARM_REG_R12, _uc.RegRead(Arm.UC_ARM_REG_LR));
            _lastTrap = slot.Name;
            _lastTrapAddress = address;
            _blocksSinceTrap = 0;

            switch (slot.Kind)
            {
                case TrapKind.Import:
                    _callOrder.Add(slot.Name);
                    _callCounts[slot.Name] = _callCounts.GetValueOrDefault(slot.Name) + 1;
                    break;

                case TrapKind.VtableMethod:
                    _vtableCalls.Add(slot.Name);
                    break;

                // Return traps are plumbing for a host-to-emulated call, not calls the
                // image made; counting them buries the real trace.
                case TrapKind.Return:
                    break;
            }

            // Nothing may throw out of a hook. Unicorn calls these from inside its own
            // dispatch loop through a native callback, so a managed exception here does not
            // unwind to the caller of EmuStart - it kills the process, taking the entire
            // report with it. Turning it into a stop keeps everything the run had already
            // established and names the stub that failed.
            try
            {
                if (slot.Handler is not null)
                {
                    slot.Handler();
                }
                else
                {
                    _stubs.Dispatch(slot.Name);
                }
            }
            catch (Exception ex) when (ex is not UnicornEngineException)
            {
                HostFailure ??= $"{slot.Name} threw {ex.GetType().Name}: {ex.Message}";
                Stop($"the host stub for {slot.Name} failed: {ex.Message}");
            }
            catch (UnicornEngineException ex)
            {
                HostFailure ??= $"{slot.Name} touched unmapped memory: {ex.Message}";
                Stop($"the host stub for {slot.Name} touched unmapped memory");
            }
        }

        /// <summary>The host-side failure that ended the run, if one did.</summary>
        public string? HostFailure { get; private set; }

        /// <summary>
        /// The classic null page. Nothing legitimate lives here, so any access is a null
        /// pointer that something dereferenced.
        /// </summary>
        private const long NullPageLimit = 0x10000;

        /// <summary>
        /// Where the run hit a null pointer, if it did: the address touched and the return
        /// address of whoever touched it.
        /// </summary>
        /// <remarks>
        /// Without this a null call just lands on a lazily mapped page of zeros, which
        /// decode as harmless Thumb no-ops - so the CPU quietly spins out the rest of its
        /// budget and reports a final PC of 0, which says nothing about the culprit.
        /// </remarks>
        public (long Address, long CalledFrom)? NullCall { get; private set; }

        /// <summary>Registers at the moment of a null call, for telling apart a null
        /// function pointer from a trap that failed to set its tail-call register.</summary>
        public string? NullCallRegisters { get; private set; }

        /// <summary>The last trap entered before the run ended - who was on the stack.</summary>
        public string? LastTrap => _lastTrap;

        private string? _lastTrap;
        private long _lastTrapAddress;

        /// <summary>Reads and writes through a null pointer, which are tolerated.</summary>
        public int NullDataAccesses { get; private set; }

        private const char Newline = (char)10;

        // The core register file at the moment of a null call, with provenance for every
        // register that looks like a heap pointer. A null call is nearly always "this object
        // was never constructed" or "this field does not hold what the code thinks it holds",
        // and both questions are answered by knowing which allocation a register lands in.
        private string DescribeNullCall()
        {
            int[] ids =
            [
                Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
                Arm.UC_ARM_REG_R4, Arm.UC_ARM_REG_R5, Arm.UC_ARM_REG_R6, Arm.UC_ARM_REG_R7,
                Arm.UC_ARM_REG_R8, Arm.UC_ARM_REG_R9, Arm.UC_ARM_REG_R10, Arm.UC_ARM_REG_R11,
                Arm.UC_ARM_REG_R12, Arm.UC_ARM_REG_SP, Arm.UC_ARM_REG_LR,
            ];
            string[] names =
            [
                "r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7",
                "r8", "r9", "r10", "r11", "r12", "sp", "lr",
            ];

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Length; i++)
            {
                if (i > 0 && i % 5 == 0)
                {
                    text.Append(Newline + "                ");
                }

                text.Append($"{names[i]}=0x{_uc.RegRead(ids[i]):X8} ");
            }

            // Every register that lands in a known block, not a fixed few. Which register
            // holds the object that matters is the compiler's choice, and picking four in
            // advance means the one that explains the fault is the one left out.
            var described = new HashSet<long>();
            for (int i = 0; i < ids.Length; i++)
            {
                long value = _uc.RegRead(ids[i]);
                string where = DescribeAllocation(value);
                if (where.StartsWith("not a block", StringComparison.Ordinal) || !described.Add(value))
                {
                    continue;
                }

                text.Append($"{Newline}                {names[i]} 0x{value:X8} {where}");
                text.Append($"{Newline}                    {DumpWords(value & ~3L, 16)}");
            }

            return text.ToString();
        }

        // Eight words from an address, laid out the way a debugger memory pane shows them.
        // Picking a vtable slot out of a flat byte dump by hand is where mistakes creep in.
        private string DumpWords(long address, int count)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    text.Append($"+{i * 4:X2}={ReadUInt32(address + i * 4):X8} ");
                }
                catch (UnicornEngineException)
                {
                    text.Append($"+{i * 4:X2}=???????? ");
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// The CPU jumped into the emulated heap, which holds no code.
        /// </summary>
        // ---------------------------------------------------------------------
        // Runaway detection
        //
        // A loop that neither calls anything nor ends is the hardest failure here to see:
        // it produces no trace, spends the whole budget, and the report says only that the
        // budget ran out. It is also common, because the usual cause is a container whose
        // begin and end pointers disagree - a walk in fixed strides that steps over its own
        // terminator and runs until something faults, which on 256 MB of mapped heap takes
        // a very long time.
        //
        // Blocks since the last trap is the cheapest signal that separates it from real
        // work: the image cannot do anything useful for millions of basic blocks without
        // allocating, copying or calling out even once.
        // ---------------------------------------------------------------------

        private long _blocksSinceTrap;

        /// <summary>How many basic blocks may run with no call across the boundary.</summary>
        /// <remarks>
        /// Generous on purpose, and tunable with WPR_RUNAWAY, because the line between a
        /// runaway and real work is a judgement rather than a fact. A million turned out to
        /// be too tight: this image builds its Galois-field tables and derives a key for its
        /// save files without calling anything at all, and that is exactly the shape a
        /// runaway has. The tell that separated them was the constant 0x1B in the loop body
        /// - the Rijndael reduction polynomial - which no corrupted container walk would
        /// contain.
        /// </remarks>
        private static readonly long RunawayBlockLimit =
            long.TryParse(Environment.GetEnvironmentVariable("WPR_RUNAWAY"), out long limit) && limit > 0
                ? limit
                : 20_000_000;

        /// <summary>Set when a loop ran long enough to be a runaway rather than work.</summary>
        public string? Runaway { get; private set; }

        /// <summary>
        /// Every word above sp that looks like a return address, newest first.
        /// </summary>
        /// <remarks>
        /// A deliberately dumb complement to <see cref="WalkHere"/>. The .pdata walk is
        /// exact when it works and gives nothing at all when it does not - one function whose
        /// packed unwind this decoder gets slightly wrong ends the chain, and the frames
        /// above it, which are the ones that say who is at fault, are lost with it.
        ///
        /// This cannot be fooled that way because it assumes nothing about frames. It over-
        /// reports instead: a stale value left in dead stack looks exactly like a live return
        /// address. Reading it means treating the list as candidates rather than a stack.
        /// </remarks>
        public string ScanStack(int words)
        {
            long sp = _uc.RegRead(Arm.UC_ARM_REG_SP);
            var found = new List<string>();

            for (int i = 0; i < words && found.Count < 12; i++)
            {
                long slot = sp + (i * 4);
                uint value;
                try
                {
                    value = ReadUInt32(slot);
                }
                catch (UnicornEngineException)
                {
                    break;
                }

                if (IsExecutableCode(value))
                {
                    found.Add($"                    sp+0x{i * 4:X2} = 0x{value & ~1u:X8}");
                }
            }

            return found.Count == 0 ? "                    (none)" : string.Join(Newline, found);
        }

        /// <summary>
        /// Unwinds the stack from wherever the CPU is now, using the .pdata tables.
        /// </summary>
        /// <remarks>
        /// The same walk the throw path does, available anywhere. A loop that is destroying
        /// the wrong object says nothing about who told it to; the frame above it does.
        /// </remarks>
        public string WalkHere()
        {
            int[] core =
            [
                Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
                Arm.UC_ARM_REG_R4, Arm.UC_ARM_REG_R5, Arm.UC_ARM_REG_R6, Arm.UC_ARM_REG_R7,
                Arm.UC_ARM_REG_R8, Arm.UC_ARM_REG_R9, Arm.UC_ARM_REG_R10, Arm.UC_ARM_REG_R11,
                Arm.UC_ARM_REG_R12, Arm.UC_ARM_REG_SP, Arm.UC_ARM_REG_LR, Arm.UC_ARM_REG_PC,
            ];

            try
            {
                List<UnwoundFrame> frames = Unwinder.Walk(
                    pc: _uc.RegRead(Arm.UC_ARM_REG_PC) & ~1L,
                    liveRegisters: core.Select(ReadRegister).ToArray(),
                    maxFrames: 12);

                return string.Join(Newline, frames.Select(f => "                    " + f));
            }
            catch (Exception ex)
            {
                return $"                    walk failed: {ex.Message}";
            }
        }

        private readonly List<(long Start, long End)> _executableHeap = new();

        /// <summary>
        /// Allocates a heap block that is allowed to hold code, exempt from the guard below.
        /// </summary>
        /// <remarks>
        /// The self-tests hand-assemble Thumb-2 into the heap and run it - that is the whole
        /// point of them, since a bridge proved with a host-side call proves nothing about
        /// the bridge. Everything else on this heap is objects and buffers, so the exemption
        /// has to be asked for explicitly rather than weakening the rule.
        /// </remarks>
        public long AllocateCode(long size)
        {
            long block = AllocateHeap(size);
            _executableHeap.Add((block, block + size));
            return block;
        }

        private void OnHeapExecution(Unicorn uc, long address, int size, object? userData)
        {
            foreach ((long start, long end) in _executableHeap)
            {
                if (address >= start && address < end)
                {
                    return;
                }
            }

            HeapExecution ??= $"jumped into the heap at 0x{address:X8} from 0x{_uc.RegRead(Arm.UC_ARM_REG_LR) & ~1L:X8}" +
                              Newline + "                " + DescribeAllocation(address) +
                              Newline + "                last trap entered: " + (_lastTrap ?? "(none)");
            Stop($"jumped into the heap at 0x{address:X8} - that is data, not code");
        }

        /// <summary>Where the image jumped into data, if it did.</summary>
        public string? HeapExecution { get; private set; }

        /// <summary>The write into the emulator's own trap page that ended the run.</summary>
        public string? TrapPageWrite { get; private set; }

        /// <summary>
        /// True when an address looks like a Thumb entry point inside the image's code.
        /// </summary>
        /// <remarks>
        /// Used to pick a function pointer out of an object whose layout is not published.
        /// The low bit is the Thumb flag, and every function in this image is Thumb, so a
        /// candidate without it is a data pointer that happens to fall in range.
        /// </remarks>
        public bool IsExecutableCode(long address)
        {
            if ((address & 1) == 0)
            {
                return false;
            }

            long target = address & ~1L;
            foreach (PeSection section in _image.Sections)
            {
                if (!section.Name.StartsWith(".text", StringComparison.Ordinal))
                {
                    continue;
                }

                long start = _image.ImageBase + section.VirtualAddress;
                if (target >= start && target < start + section.VirtualSize)
                {
                    return true;
                }
            }

            return false;
        }

        private bool OnUnmappedAccess(Unicorn uc, int eventType, long address, int size, long value, object? userData)
        {
            if (address >= 0 && address < NullPageLimit)
            {
                // Reading through a null pointer is survivable and the CRT startup does it
                // before anything else has gone wrong, so those are counted and zero-filled
                // as before. Executing from there is not survivable: it means the image
                // called through a null function pointer, and every instruction after it is
                // noise. Stop there, while lr still names the caller.
                // Not a pattern: the binding exposes these as properties, not constants.
                if (eventType == Common.UC_MEM_FETCH_UNMAPPED || eventType == Common.UC_MEM_FETCH_PROT)
                {
                    long from = _uc.RegRead(Arm.UC_ARM_REG_LR);
                    string registers = DescribeNullCall();

                    NullCall ??= (address, from);
                    NullCallRegisters ??= registers;

                    if (_domain is not null)
                    {
                        _threadDeaths.Add(
                            $"{_domain.Name} called through a null pointer at 0x{from:X8}" +
                            Newline + "                " + registers);
                        _deferredLog.Add($"   {_domain.Name} DIED at 0x{from:X8} - abandoned");
                        _abandoned = _domain;
                        _domain = null;
                    }
                    else
                    {
                        // Not inside a fault domain, so this is the run ending. Recording it
                        // as the stop reason matters more than it looks: once contained null
                        // calls existed, an uncontained one that happened later had nothing
                        // left to report it - NullCall was already taken by the contained
                        // one - and the run claimed its budget had run out while it was
                        // actually sitting at address zero.
                        UncontainedNullCall ??= $"called through a null pointer at 0x{from:X8}" +
                                                Newline + "                " + registers +
                                                Newline + "                code addresses on the stack:" +
                                                Newline + ScanStack(96);
                        StopReason ??= $"called through a null pointer at 0x{from:X8}";
                    }

                    _uc.EmuStop();
                    return true;
                }

                NullDataAccesses++;
            }

            // A write into the trap page now faults rather than landing. Whatever pointer the
            // image is writing through is wrong, and everything after it would be noise.
            if (address >= TrapBase && address < TrapBase + TrapSize
                && (eventType == Common.UC_MEM_WRITE_PROT || eventType == Common.UC_MEM_WRITE_UNMAPPED))
            {
                TrapPageWrite ??= $"emulated code at 0x{_uc.RegRead(Arm.UC_ARM_REG_PC) & ~1L:X8} tried to " +
                                  $"write {size} bytes to trap slot 0x{address:X8}" +
                                  Newline + "                " + DescribeNullCall();
                Stop($"emulated code wrote to the trap page at 0x{address:X8} - a pointer it was given is wrong");
                _uc.EmuStop();
                return true;
            }

            long page = address & ~(PageSize - 1);

            // The null page is mapped readable but never executable, so that tolerating a
            // null read does not quietly license a null call later: without this the page
            // is already mapped by the time anything jumps to it, the zeros decode as
            // Thumb no-ops, and the run spins to the end of its budget with nothing to say.
            int protection = page < NullPageLimit
                ? Common.UC_PROT_READ | Common.UC_PROT_WRITE
                : Common.UC_PROT_ALL;

            try
            {
                uc.MemMap(page, PageSize, protection);
                LazyPagesMapped++;
            }
            catch (UnicornEngineException)
            {
                // Already mapped, or unmappable. Either way returning true lets the CPU
                // retry rather than aborting the whole run.
            }

            return true;
        }

        /// <summary>
        /// A ring of the most recently entered basic blocks.
        /// </summary>
        /// <remarks>
        /// The only way to answer "where did it actually go" once a run ends somewhere
        /// unhelpful. A final PC of 0 says a null pointer was called but nothing about who
        /// called it; the trail leading up to it names the code that did.
        /// </remarks>
        private readonly long[] _recentBlocks = new long[64];
        private int _recentBlockCount;

        /// <summary>The last basic blocks entered, oldest first.</summary>
        public IReadOnlyList<long> RecentBlocks
        {
            get
            {
                int size = Math.Min(_recentBlockCount, _recentBlocks.Length);
                long[] ordered = new long[size];
                for (int i = 0; i < size; i++)
                {
                    ordered[i] = _recentBlocks[(_recentBlockCount - size + i) % _recentBlocks.Length];
                }

                return ordered;
            }
        }

        /// <summary>
        /// Fires when emulated code writes into the trap page - which it must never do,
        /// since that is where the emulator keeps its own instructions.
        /// </summary>
        private void OnTrapPageWritten(Unicorn uc, long address, int size, long value, object? userData)
        {
            RejectedWrites.Add(
                $"emulated code at 0x{uc.RegRead(Arm.UC_ARM_REG_PC):X8} wrote {size} bytes " +
                $"(0x{value:X}) to trap slot 0x{address:X8}" +
                (_lastTrap is null ? string.Empty : $" - last trap was {_lastTrap}"));
        }

        private void OnBlockEntered(Unicorn uc, long address, int size, object? userData)
        {
            BlocksExecuted++;
            CodeBytesExecuted += size;
            _recentBlocks[_recentBlockCount++ % _recentBlocks.Length] = address;

            if (++_blocksSinceTrap != RunawayBlockLimit)
            {
                return;
            }

            Runaway = $"{RunawayBlockLimit:N0} basic blocks with no call across the boundary, " +
                      $"looping at 0x{address:X8}" +
                      Newline + "                " + DescribeNullCall() +
                      Newline + "                last trap entered: " + (_lastTrap ?? "(none)") +
                      Newline + "                call stack:" + Newline + WalkHere() +
                      Newline + "                code addresses on the stack:" + Newline + ScanStack(64);
            Stop($"runaway loop at 0x{address:X8} - {RunawayBlockLimit:N0} blocks without a single call out");
        }

        private static long Align(long value, long alignment) => (value + alignment - 1) & ~(alignment - 1);

        public void Dispose() => _uc.Dispose();
    }
}
