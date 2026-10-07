using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Guest threads, scheduled cooperatively on the one emulated CPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deferred calls (<see cref="QueueDeferredCall"/>) stand in for thread-pool work: they run
    /// later, to completion. That cannot hold a thread that loops - a loader that waits on a
    /// queue, a game-logic thread that sleeps between ticks - and a Direct3D/XAML title starts
    /// exactly those with <c>std::thread</c>. Amazing Alex drew 80,000 blue frames because its
    /// two threads were never started.
    /// </para>
    /// <para>
    /// A guest thread is a register file, a set of VFP registers and a stack. Switching happens
    /// only inside a trap - a blocking import (sleep, mutex, join, condition wait) or the host's
    /// frame boundary - and is nothing more than saving one register file and loading another:
    /// the trap ends with <c>bx r12</c>, so setting r12 to the other thread's resume address is
    /// the context switch. Threads run until they block, which is what a single core with no
    /// preemption would do; a thread that computes for a long time without blocking holds the
    /// CPU until it does.
    /// </para>
    /// </remarks>
    public sealed partial class ArmEmulator
    {
        private sealed class GuestThread(int id, string name)
        {
            public int Id { get; } = id;
            public string Name { get; } = name;
            public long[]? Registers { get; set; }
            public uint[]? Vfp { get; set; }

            /// <summary>Null when runnable; otherwise asked before each switch-in.</summary>
            public Func<bool>? Ready { get; set; }

            /// <summary>Run as the thread is switched back in - e.g. taking the mutex it waited for.</summary>
            public Action? OnResume { get; set; }

            public bool Finished { get; set; }
            public long ExitCode { get; set; }

            /// <summary>What it is blocked on, when it is.</summary>
            public string? Why { get; set; }
        }

        private const int GuestThreadStackBytes = 1024 * 1024;
        private readonly List<GuestThread> _threads = [];
        private GuestThread? _currentThread;
        private int _nextThreadId = 1;
        private long _threadExitTrap;
        private int _roundRobin;
        private readonly List<string> _threadLog = [];

        /// <summary>Thread starts, exits and deadlocks, for the report.</summary>
        public IReadOnlyList<string> ThreadLog => _threadLog;

        public int GuestThreadsStarted => _threads.Count - 1;

        public int ThreadSwitches { get; private set; }

        private GuestThread CurrentThread
        {
            get
            {
                if (_currentThread is null)
                {
                    _currentThread = new GuestThread(_nextThreadId++, "main");
                    _threads.Add(_currentThread);
                }

                return _currentThread;
            }
        }

        public int CurrentThreadId => CurrentThread.Id;

        public bool IsThreadFinished(int id) => _threads.FirstOrDefault(t => t.Id == id) is not { } t || t.Finished;

        private void ThreadNote(string line)
        {
            if (_threadLog.Count < 200)
            {
                _threadLog.Add(line);
            }
        }

        /// <summary>
        /// Creates a runnable guest thread that will call <paramref name="entry"/>(<paramref name="argument"/>)
        /// the next time the scheduler picks it.
        /// </summary>
        public int CreateGuestThread(string name, long entry, long argument)
        {
            GuestThread creator = CurrentThread;
            GuestThread thread = new(_nextThreadId++, name);

            long stack = AllocateHeap(GuestThreadStackBytes);
            long top = (stack + GuestThreadStackBytes - 64) & ~7L;

            long[] registers = CaptureRegisters();
            for (int i = 0; i < registers.Length; i++)
            {
                registers[i] = 0;
            }

            registers[Index(Arm.UC_ARM_REG_R0)] = argument;
            registers[Index(Arm.UC_ARM_REG_SP)] = top;
            registers[Index(Arm.UC_ARM_REG_LR)] = ThumbEntry(ThreadExitTrap());
            registers[Index(Arm.UC_ARM_REG_R12)] = entry;
            registers[Index(Arm.UC_ARM_REG_CPSR)] = _cpu.RegRead(Arm.UC_ARM_REG_CPSR);
            thread.Registers = registers;
            thread.Vfp = new uint[64];

            _threads.Add(thread);
            ThreadNote($"thread {thread.Id} '{name}' created by {creator.Id} at 0x{entry:X8}({argument:X8}), stack 0x{stack:X8}");
            return thread.Id;
        }

        private static int Index(int register) => Array.IndexOf(CoreRegisters, register);

        private long ThreadExitTrap()
        {
            if (_threadExitTrap == 0)
            {
                _threadExitTrap = AllocateTrapSlot("guest thread exit", TrapKind.Return, OnThreadExit);
            }

            return _threadExitTrap;
        }

        /// <summary>
        /// Raised when a thread is ended from outside (an exception nothing caught) rather than
        /// returning. Real unwinding would have run its destructors - every lock_guard among them -
        /// so whoever tracks locks releases the ones it held.
        /// </summary>
        public event Action<int>? GuestThreadAbandoned;

        /// <summary>
        /// Ends the current thread if it is a background one, switching to another; false on the
        /// main thread, which has no one to hand the CPU to and whose death ends the run.
        /// </summary>
        public bool EndCurrentGuestThread(string why)
        {
            GuestThread me = CurrentThread;
            if (me.Id == 1)
            {
                return false;
            }

            me.Finished = true;
            ThreadNote($"thread {me.Id} '{me.Name}' ended: {why}");
            GuestThreadAbandoned?.Invoke(me.Id);
            if (PickNext(me) is not { } next)
            {
                return false;
            }

            SwitchTo(next);
            return true;
        }

        private void OnThreadExit()
        {
            GuestThread me = CurrentThread;
            me.Finished = true;
            me.ExitCode = _cpu.RegRead(Arm.UC_ARM_REG_R0);
            ThreadNote($"thread {me.Id} '{me.Name}' exited with {me.ExitCode}");

            GuestThread? next = PickNext(me);
            if (next is null)
            {
                Stop($"guest thread {me.Id} exited and no thread is runnable");
                return;
            }

            SwitchTo(next);
        }

        /// <summary>
        /// Blocks the calling thread - which must be inside an import - until <paramref name="ready"/>
        /// says so, running other threads meanwhile. Set the import's return value first.
        /// </summary>
        /// <returns>
        /// True if the CPU was handed to another thread (the caller must do nothing more), false if
        /// no switch happened: either nothing else could run and the wait is already satisfied, or
        /// it never will be (a deadlock, logged) and the caller should carry on as best it can.
        /// </returns>
        public bool BlockCurrentThread(Func<bool> ready, Action? onResume = null, string? why = null)
        {
            GuestThread me = CurrentThread;
            GuestThread? next = PickNext(me);
            if (next is null)
            {
                if (!ready())
                {
                    ThreadNote($"thread {me.Id} would wait for ever{(why is null ? "" : $" ({why})")}; carrying on");
                    return false;
                }

                onResume?.Invoke();
                return false;
            }

            me.Ready = ready;
            me.OnResume = onResume;
            me.Why = why;
            Save(me);
            SwitchTo(next);
            return true;
        }

        /// <summary>One line per guest thread: running, blocked (on what, from where) or finished.</summary>
        public IEnumerable<string> DescribeThreads()
        {
            foreach (GuestThread t in _threads)
            {
                string state = t.Finished ? "finished"
                    : t == _currentThread ? "running"
                    : t.Ready is null ? "runnable"
                    : $"blocked{(t.Why is null ? "" : $" on {t.Why}")}";
                string where = t.Registers is { } r && t != _currentThread
                    ? $" resume 0x{r[Index(Arm.UC_ARM_REG_R12)]:X8} lr 0x{r[Index(Arm.UC_ARM_REG_LR)]:X8}"
                    : "";
                yield return $"thread {t.Id} '{t.Name}': {state}{where}";
            }
        }

        /// <summary>
        /// Gives every other runnable thread a turn, then runs <paramref name="continueWith"/> on this
        /// one. For the host's frame boundary, which is not inside any import of its own.
        /// </summary>
        public void RunOtherThreads(Action continueWith)
        {
            GuestThread me = CurrentThread;
            if (PickNext(me) is not { } next)
            {
                continueWith();
                return;
            }

            // The caller continues from a one-shot trap: when the scheduler comes back to this
            // thread, r12 points at it and the trap runs the continuation.
            long resume = _freeReturnTraps.Count > 0
                ? ReuseReturnTrap("thread resume", continueWith)
                : AllocateTrapSlot("thread resume", TrapKind.RecycledReturn, continueWith);
            me.Ready = null;
            me.OnResume = null;
            Save(me);
            me.Registers![Index(Arm.UC_ARM_REG_R12)] = ThumbEntry(resume);
            SwitchTo(next);
        }

        private void Save(GuestThread thread)
        {
            thread.Registers = CaptureRegisters();
            uint[] vfp = thread.Vfp ?? new uint[64];
            for (int i = 0; i < 64; i++)
            {
                vfp[i] = _cpu.VfpRead(i);
            }

            thread.Vfp = vfp;
        }

        private GuestThread? PickNext(GuestThread except)
        {
            for (int n = 0; n < _threads.Count; n++)
            {
                GuestThread candidate = _threads[(_roundRobin + n) % _threads.Count];
                if (candidate == except || candidate.Finished || candidate.Registers is null)
                {
                    continue;
                }

                if (candidate.Ready is null || candidate.Ready())
                {
                    _roundRobin = (_threads.IndexOf(candidate) + 1) % _threads.Count;
                    return candidate;
                }
            }

            return null;
        }

        private void SwitchTo(GuestThread thread)
        {
            RestoreRegisters(thread.Registers!);
            for (int i = 0; i < 64; i++)
            {
                _cpu.VfpWrite(i, thread.Vfp![i]);
            }

            // RestoreRegisters put the thread's own r12 - its resume address - back, which is
            // what the trap's bx r12 jumps to.
            thread.Ready = null;
            Action? resumed = thread.OnResume;
            thread.OnResume = null;
            _currentThread = thread;
            ThreadSwitches++;
            resumed?.Invoke();
        }
    }
}
