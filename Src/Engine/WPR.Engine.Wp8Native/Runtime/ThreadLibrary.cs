namespace WPR.Wp8Native
{
    /// <summary>
    /// MSVCP110/MSVCR110's thread layer - <c>std::thread</c> (<c>_Pad</c>), <c>_Thrd_*</c>,
    /// <c>_Mtx_*</c>, <c>_Cnd_*</c> - on the emulator's cooperative guest threads.
    /// </summary>
    /// <remarks>
    /// <para><b>std::thread.</b> The constructor builds a <c>_LaunchPad</c> on its own stack and
    /// calls <c>_Pad::_Launch(_Thrd_t*)</c>, which starts a thread running the pad's virtual
    /// <c>_Go</c> (slot 0: <c>_Pad</c>'s destructor is not virtual) and then waits until that
    /// thread has copied the callable out and called <c>_Pad::_Release</c>. The wait matters:
    /// the pad dies as soon as <c>_Launch</c> returns. So <c>_Launch</c> blocks the creator until
    /// <c>_Release</c>, and the new thread runs first.</para>
    /// <para><b>_Thrd_t</b> is <c>{ void* _Hnd; unsigned _Id; }</c>, passed by value in two
    /// registers and returned through a hidden pointer (AAPCS returns anything over four bytes
    /// in memory). Both fields hold the guest thread id here.</para>
    /// <para><b>Mutexes</b> are recursive-capable owner counts keyed by the <c>_Mtx_t</c> handle,
    /// which <c>_Mtx_init</c> allocates. With one thread they are never contended and behave as
    /// they always did; with several, a contended lock blocks and the owner runs.</para>
    /// </remarks>
    public sealed class ThreadLibrary
    {
        private const long ThrdSuccess = 0, ThrdTimedout = 2, ThrdBusy = 3;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Dictionary<long, (int Owner, int Count)> _mutexes = new();
        private readonly Dictionary<long, int> _conditionGeneration = new();
        private readonly HashSet<long> _releasedPads = [];
        private long _trylockPolls;

        /// <summary>_Cnd_register_at_thread_exit registrations: the thread, and what its exit must do.</summary>
        private readonly List<(int Thread, long Condition, long Mutex, long Flag)> _atThreadExit = new();

        public ThreadLibrary(ArmEmulator emulator, CallFrame frame)
        {
            // An abandoned thread's locks would otherwise stay held for ever: Angry Birds' network
            // thread dies holding a mutex its audio thread then waits on, and the game is silent.
            emulator.GuestThreadAbandoned += id =>
            {
                foreach (long mutex in _mutexes.Where(m => m.Value.Owner == id && m.Value.Count > 0).Select(m => m.Key).ToList())
                {
                    _mutexes[mutex] = (0, 0);
                }
            };

            _emulator = emulator;
            _frame = frame;
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // --- std::thread ---
            handlers["??0_Pad@std@@QAA@XZ"] = () => _frame.Return(_frame.Arg(0));
            handlers["??1_Pad@std@@QAA@XZ"] = () => _frame.Return(0);
            handlers["?_Launch@_Pad@std@@QAAXPAU_Thrd_imp_t@@@Z"] = Launch;
            handlers["?_Release@_Pad@std@@QAAXXZ"] = () =>
            {
                _releasedPads.Add(_frame.Arg(0));
                _frame.Return(0);
            };

            handlers["_Thrd_current"] = () =>
            {
                WriteThread(_frame.Arg(0), _emulator.CurrentThreadId);
                _frame.Return(_frame.Arg(0));
            };
            handlers["_Thrd_id"] = () => _frame.Return(_emulator.CurrentThreadId);
            handlers["_Thrd_equal"] = () => _frame.Return(_frame.Arg(1) == _frame.Arg(3) ? 1 : 0);
            handlers["_Thrd_lt"] = () => _frame.Return(_frame.Arg(1) < _frame.Arg(3) ? 1 : 0);
            handlers["_Thrd_detach"] = () => _frame.Return(ThrdSuccess);

            // int _Thrd_create(_Thrd_t*, int (*start)(void*), void* arg): the C11-style spelling
            // underneath std::thread, which Modern Combat 4 calls directly. Unimplemented, it
            // answered success and started nothing.
            handlers["_Thrd_create"] = () =>
            {
                int id = _emulator.CreateGuestThread("_Thrd_create", _frame.Arg(1), _frame.Arg(2));
                WriteThread(_frame.Arg(0), id);
                _frame.Return(ThrdSuccess);
            };

            // int _Thrd_join(_Thrd_t, int* result)
            handlers["_Thrd_join"] = () =>
            {
                int id = (int)_frame.Arg(1);
                long result = _frame.Arg(2);
                _frame.Return(ThrdSuccess);
                if (result != 0)
                {
                    _emulator.WriteUInt32(result, 0);
                }

                _emulator.BlockCurrentThread(() => _emulator.IsThreadFinished(id), why: $"join thread {id}");
            };

            handlers["_Thrd_sleep"] = Yield;
            handlers["_Thrd_yield"] = Yield;
            handlers["Sleep"] = Yield;
            handlers["SleepEx"] = Yield;
            handlers["GetCurrentThreadId"] = () => _frame.Return(0x1000 + _emulator.CurrentThreadId);

            // --- mutexes ---
            // int _Mtx_init(_Mtx_t*, int type)
            handlers["_Mtx_init"] = () =>
            {
                if (_frame.Arg(0) != 0)
                {
                    long mutex = _emulator.AllocateHeap(16);
                    _emulator.WriteUInt32(_frame.Arg(0), (uint)mutex);
                }

                _frame.Return(ThrdSuccess);
            };
            handlers["_Mtx_destroy"] = () =>
            {
                _mutexes.Remove(_frame.Arg(0));
                _frame.Return(0);
            };
            handlers["_Mtx_lock"] = Lock;
            handlers["_Mtx_trylock"] = () =>
            {
                long mutex = _frame.Arg(0);
                _frame.Return(TryTake(mutex) ? ThrdSuccess : ThrdBusy);

                // A thread polling a lock is waiting for another to change something, which on a
                // phone the scheduler's preemption would let happen. Threads here switch only
                // when one blocks, so Modern Combat 4's loader span on trylock/unlock 16 million
                // times every five seconds while the thread it waited for never ran. Every so
                // often a poll gives the others a turn; the answer is already in r0.
                if (++_trylockPolls % 64 == 0)
                {
                    _emulator.BlockCurrentThread(() => true);
                }
            };
            handlers["_Mtx_unlock"] = () =>
            {
                Unlock(_frame.Arg(0));
                _frame.Return(ThrdSuccess);
            };
            // --- Concurrency::critical_section and its scoped_lock ---
            // The same owner-count model as _Mtx, keyed by the critical_section's address. Titles
            // import only the four constructors and destructors (lock and unlock are inlined into
            // them), so these are the whole class as far as any image here is concerned. ConcRT's
            // lock is not recursive - re-locking from the owner throws improper_lock - but taking
            // it again is the lenient answer and matches what _Mtx does here.
            handlers["??0critical_section@Concurrency@@QAA@XZ"] = () =>
            {
                _mutexes.Remove(_frame.Arg(0));
                _frame.Return(_frame.Arg(0));
            };
            handlers["??1critical_section@Concurrency@@QAA@XZ"] = () =>
            {
                _mutexes.Remove(_frame.Arg(0));
                _frame.Return(0);
            };
            handlers["?lock@critical_section@Concurrency@@QAAXXZ"] = () => LockAndReturn(_frame.Arg(0), 0);
            handlers["?unlock@critical_section@Concurrency@@QAAXXZ"] = () =>
            {
                Unlock(_frame.Arg(0));
                _frame.Return(0);
            };
            handlers["?try_lock@critical_section@Concurrency@@QAA_NXZ"] = () => _frame.Return(TryTake(_frame.Arg(0)) ? 1 : 0);

            // scoped_lock(critical_section&): stores the reference at this+0 and locks it; the
            // destructor unlocks whatever this+0 names. Returns this, as a constructor does.
            handlers["??0scoped_lock@critical_section@Concurrency@@QAA@AAV12@@Z"] = () =>
            {
                long self = _frame.Arg(0), section = _frame.Arg(1);
                _emulator.WriteUInt32(self, (uint)section);
                LockAndReturn(section, self);
            };
            handlers["??1scoped_lock@critical_section@Concurrency@@QAA@XZ"] = () =>
            {
                Unlock(_emulator.ReadUInt32(_frame.Arg(0)));
                _frame.Return(0);
            };

            handlers["_Mtx_current_owns"] = () =>
                _frame.Return(_mutexes.TryGetValue(_frame.Arg(0), out var m) && m.Owner == _emulator.CurrentThreadId ? 1 : 0);

            // --- condition variables ---
            handlers["_Cnd_init"] = () =>
            {
                if (_frame.Arg(0) != 0)
                {
                    long condition = _emulator.AllocateHeap(16);
                    _emulator.WriteUInt32(_frame.Arg(0), (uint)condition);
                }

                _frame.Return(ThrdSuccess);
            };
            handlers["_Cnd_destroy"] = () => _frame.Return(0);
            handlers["_Cnd_signal"] = Signal;
            handlers["_Cnd_broadcast"] = Signal;
            handlers["_Cnd_wait"] = () => WaitCondition(_frame.Arg(0), _frame.Arg(1), deadline: null);

            // int _Cnd_timedwait(_Cnd_t, _Mtx_t, const xtime*): _Thrd_timedout once the absolute
            // xtime (UTC, as xtime_get reports it) passes without a signal.
            handlers["_Cnd_timedwait"] = () => WaitCondition(_frame.Arg(0), _frame.Arg(1), Deadline(_frame.Arg(2)));

            // int _Mtx_timedlock(_Mtx_t, const xtime*): the lock, or _Thrd_timedout at the deadline.
            handlers["_Mtx_timedlock"] = TimedLock;

            // void _Cnd_register_at_thread_exit(_Cnd_t, _Mtx_t, int* p): when the calling thread
            // exits, set *p to 1, unlock the mutex (which it keeps holding until then) and
            // broadcast - what promise::set_value_at_thread_exit and notify_all_at_thread_exit
            // are built on. Thread exit is noticed by the waiters (see FlushThreadExits).
            handlers["_Cnd_register_at_thread_exit"] = () =>
            {
                _atThreadExit.Add((_emulator.CurrentThreadId, _frame.Arg(0), _frame.Arg(1), _frame.Arg(2)));
                _frame.Return(0);
            };

            // void _Cnd_unregister_at_thread_exit(_Mtx_t): forgets every registration on that mutex.
            handlers["_Cnd_unregister_at_thread_exit"] = () =>
            {
                long mutex = _frame.Arg(0);
                _atThreadExit.RemoveAll(r => r.Mutex == mutex);
                _frame.Return(0);
            };

            // void _Cnd_do_broadcast_at_thread_exit(void): the calling thread's exit, now.
            handlers["_Cnd_do_broadcast_at_thread_exit"] = () =>
            {
                RunThreadExits(r => r.Thread == _emulator.CurrentThreadId);
                _frame.Return(0);
            };
        }

        private void WriteThread(long address, int id)
        {
            if (address != 0)
            {
                _emulator.WriteUInt32(address, (uint)id);
                _emulator.WriteUInt32(address + 4, (uint)id);
            }
        }

        /// <summary>void _Pad::_Launch(_Thrd_t*)</summary>
        private void Launch()
        {
            long pad = _frame.Arg(0);
            long handle = _frame.Arg(1);
            long vtable = _emulator.ReadUInt32(pad);
            long go = _emulator.ReadUInt32(vtable);

            int id = _emulator.CreateGuestThread("std::thread", go, pad);
            WriteThread(handle, id);
            _frame.Return(0);

            // Wait for the thread to copy its callable out of the pad, which lives on our stack.
            _emulator.BlockCurrentThread(() => _releasedPads.Contains(pad), () => _releasedPads.Remove(pad), why: "std::thread start");
        }

        private void Yield()
        {
            _frame.Return(0);
            if (!_emulator.BlockCurrentThread(() => true))
            {
                _emulator.YieldToDeferredWork();
            }
        }

        private bool TryTake(long mutex)
        {
            int me = _emulator.CurrentThreadId;
            if (!_mutexes.TryGetValue(mutex, out var state) || state.Count == 0 || state.Owner == me)
            {
                _mutexes[mutex] = (me, state.Owner == me ? state.Count + 1 : 1);
                return true;
            }

            return false;
        }

        private void Lock() => LockAndReturn(_frame.Arg(0), ThrdSuccess);

        /// <summary>Takes <paramref name="mutex"/>, blocking while another thread holds it, and leaves <paramref name="result"/> in r0.</summary>
        private void LockAndReturn(long mutex, long result)
        {
            _frame.Return(result);
            if (TryTake(mutex))
            {
                return;
            }

            bool switched = _emulator.BlockCurrentThread(
                () => !_mutexes.TryGetValue(mutex, out var s) || s.Count == 0,
                () => TryTake(mutex),
                why: $"mutex 0x{mutex:X8} held by thread {_mutexes[mutex].Owner}");
            if (!switched)
            {
                // Nobody else can run, so nobody will release it: take it anyway rather than hang.
                _mutexes[mutex] = (_emulator.CurrentThreadId, 1);
            }
        }

        private void Unlock(long mutex)
        {
            if (_mutexes.TryGetValue(mutex, out var state) && state.Count > 0)
            {
                _mutexes[mutex] = state.Count == 1 ? (0, 0) : (state.Owner, state.Count - 1);
            }
        }

        private void Signal()
        {
            long condition = _frame.Arg(0);
            _conditionGeneration[condition] = _conditionGeneration.GetValueOrDefault(condition) + 1;
            _frame.Return(ThrdSuccess);
        }

        /// <summary>
        /// _Cnd_wait / _Cnd_timedwait(cnd, mtx[, xtime]): release the mutex, wait for a signal
        /// (or the deadline), take the mutex back.
        /// </summary>
        private void WaitCondition(long condition, long mutex, long? deadline)
        {
            FlushThreadExits();
            int generation = _conditionGeneration.GetValueOrDefault(condition);
            var held = _mutexes.GetValueOrDefault(mutex);
            _mutexes[mutex] = (0, 0);
            _frame.Return(ThrdSuccess);

            bool Signalled()
            {
                FlushThreadExits();
                return _conditionGeneration.GetValueOrDefault(condition) != generation;
            }

            bool Free() => !_mutexes.TryGetValue(mutex, out var s) || s.Count == 0;

            void Resume()
            {
                _mutexes[mutex] = held;
                if (deadline is not null && !Signalled())
                {
                    _frame.Return(ThrdTimedout);
                }
            }

            bool switched = _emulator.BlockCurrentThread(
                () => (Signalled() || (deadline is { } at && Now() >= at)) && Free(),
                Resume,
                why: $"condition 0x{condition:X8}",
                timeout: deadline is { } due ? TimeSpan.FromTicks(Math.Max(0, due - Now())) : null);
            if (!switched)
            {
                // Nothing else can run, so nothing can signal. An untimed wait wakes spuriously,
                // which is legal (callers re-check their predicate); a timed one times out, which
                // is what waiting out its deadline would have come to.
                Resume();
            }
        }

        /// <summary>An absolute xtime <c>{__int64 sec; long nsec}</c> as 100ns ticks since 1970.</summary>
        private long? Deadline(long xtime)
        {
            if (xtime == 0)
            {
                return null;
            }

            long seconds = BitConverter.ToInt64(_emulator.ReadMemory(xtime, 8));
            int nanoseconds = BitConverter.ToInt32(_emulator.ReadMemory(xtime + 8, 4));
            return (seconds * TimeSpan.TicksPerSecond) + (nanoseconds / 100);
        }

        private static long Now() => DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;

        /// <summary>int _Mtx_timedlock(_Mtx_t, const xtime*)</summary>
        private void TimedLock()
        {
            long mutex = _frame.Arg(0);
            long deadline = Deadline(_frame.Arg(1)) ?? 0;
            FlushThreadExits();
            if (TryTake(mutex))
            {
                _frame.Return(ThrdSuccess);
                return;
            }

            // Already timed out unless the wait below ends with the lock taken. If nothing else can
            // run, nothing will release it, and that is the answer.
            _frame.Return(ThrdTimedout);
            _emulator.BlockCurrentThread(
                () => !_mutexes.TryGetValue(mutex, out var s) || s.Count == 0 || Now() >= deadline,
                () => _frame.Return(TryTake(mutex) ? ThrdSuccess : ThrdTimedout),
                why: $"timed lock 0x{mutex:X8} held by thread {_mutexes[mutex].Owner}",
                timeout: TimeSpan.FromTicks(Math.Max(0, deadline - Now())));
        }

        /// <summary>Carries out the at-thread-exit duties of every registered thread that has finished.</summary>
        private void FlushThreadExits()
        {
            if (_atThreadExit.Count > 0)
            {
                RunThreadExits(r => _emulator.IsThreadFinished(r.Thread));
            }
        }

        /// <summary>For each matching registration: *p = 1, unlock the mutex, broadcast.</summary>
        private void RunThreadExits(Func<(int Thread, long Condition, long Mutex, long Flag), bool> which)
        {
            foreach (var registration in _atThreadExit.Where(which).ToList())
            {
                _atThreadExit.Remove(registration);
                if (registration.Flag != 0)
                {
                    _emulator.WriteUInt32(registration.Flag, 1);
                }

                if (_mutexes.TryGetValue(registration.Mutex, out var state) && state.Owner == registration.Thread)
                {
                    _mutexes[registration.Mutex] = (0, 0);
                }

                _conditionGeneration[registration.Condition] = _conditionGeneration.GetValueOrDefault(registration.Condition) + 1;
            }
        }
    }
}
