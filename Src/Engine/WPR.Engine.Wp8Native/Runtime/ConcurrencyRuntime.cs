namespace WPR.Wp8Native
{
    /// <summary>
    /// The Concurrency Runtime and PPL as the VC11 headers (concrt.h, pplcancellation_token.h,
    /// ppltasks.h) call into MSVCR110 and MSVCP110: cancellation tokens and their registrations, task
    /// collections (the unstructured one behind every <c>task</c>, the structured one behind
    /// <c>structured_task_group</c> and <c>parallel_for</c>), the scheduler's queue, contexts, beacons,
    /// and ConcRT's exceptions.
    /// </summary>
    /// <remarks>
    /// <para><b>Work runs on guest threads.</b> A scheduled chore goes on one queue that ConcRT worker
    /// threads drain - real guest threads (<see cref="ArmEmulator.CreateGuestThread"/>), created when
    /// there is queued work and no worker free to take it, which exit when the queue is empty. A thread
    /// waiting on a collection does what ConcRT's wait does: it runs that collection's unstarted chores
    /// itself (inline stealing), then blocks (<see cref="ArmEmulator.BlockCurrentThread"/>) until the
    /// chores other threads started have finished. Before this, chores went to the deferred queue and
    /// <c>_RunAndWait</c> answered "completed" without running or waiting for anything.</para>
    /// <para><b>Layouts the game's inline code shares with the runtime</b>, as VC11 declares them and as
    /// read off the images (Lumia.Imaging's parallel_for, Rio's ppltasks, Halo's registrations):</para>
    /// <code>
    /// _UnrealizedChore           +0 vptr  +4 m_pFunction (called with the chore)  +8 _M_pTaskCollection
    ///                            +C _M_pChoreFunction  +10 _M_fRuntimeOwnsLifetime (delete after it runs)
    /// _CancellationTokenState    +0 vptr [~, _Destroy]  +4 refcount  +8 _M_stateFlag (_IsCanceled, inline)
    /// _CancellationTokenRegistration (0x1C) +0 vptr [~, _Destroy, _Exec]  +4 refcount  +8 _M_state
    ///                            (_STATE_CLEAR 0 .. _STATE_CALLED 3)  +10 _M_pTokenState
    /// _AsyncTaskCollection       +0 vptr [~, _Destroy]  +4 refcount  +8 its _TaskCollection
    /// _Cancellation_beacon       +0 _Beacon_reference* { long _M_signals }
    /// </code>
    /// <para><c>_CancellationTokenState::_None()</c> is the pointer value 2; it and null are never
    /// canceled. A collection is canceled by its own <c>_Cancel</c> or by its token.</para>
    /// <para>Exceptions: an exception that leaves a chore is caught at the chore's return trap, as
    /// MSVCR110's chore wrapper catches it (<see cref="RunningChoreTraps"/>): the chore's frames are
    /// unwound, its collection is canceled and the worker carries on. The wait then reports
    /// <c>_Canceled</c> rather than rethrowing - the one place this falls short of ConcRT, which
    /// carries the exception to wait(); ppltasks has already given it to the task by then.</para>
    /// </remarks>
    public sealed class ConcurrencyRuntime
    {
        private const string C = "@Concurrency@@";
        private const string D = "@details@Concurrency@@";
        private const long TokenNone = 2;
        private const int StateClear = 0, StateSynchronize = 2, StateCalled = 3;
        private const int RegistrationState = 8, RegistrationToken = 0x10, RegistrationSize = 0x1C;
        private const int TokenStateFlag = 8;
        private const int ChoreFunction = 4, ChoreCollection = 8, ChoreRuntimeOwned = 0x10;
        private const int StatusCompleted = 1, StatusCanceled = 2;

        /// <summary>
        /// How many ConcRT workers may exist at once. They are created only for queued work no
        /// existing worker can take (all are blocked), which is ConcRT's oversubscription rule.
        /// </summary>
        private const int MaxWorkers = 8;

        private readonly ArmEmulator _e;
        private readonly CallFrame _f;
        private readonly Func<MsvcpCore> _msvcp;

        private sealed class Token(long address)
        {
            public long Address { get; } = address;

            public List<long> Registrations { get; } = new();
        }

        private sealed class Collection(long address, long token, bool structured)
        {
            public long Address { get; } = address;

            public long Token { get; set; } = token;

            public bool Structured { get; } = structured;

            public bool Canceled { get; set; }

            /// <summary>Chores scheduled to it and not yet finished (queued or running).</summary>
            public int Outstanding { get; set; }

            /// <summary>Chores of it running right now, on any thread.</summary>
            public int Running { get; set; }

            /// <summary>An _AsyncTaskCollection's allocation, freed when it is destroyed.</summary>
            public long Block { get; init; }

            public bool DestroyPending { get; set; }
        }

        private sealed class Chore(long address, Collection? owner, string name)
        {
            public long Address { get; } = address;

            public Collection? Owner { get; } = owner;

            public string Name { get; } = name;

            /// <summary>A raw <c>_ScheduleTask</c> proc: called with <see cref="Argument"/>, nothing to delete.</summary>
            public long RawFunction { get; init; }

            public long Argument { get; init; }

            public bool Started { get; set; }

            public bool Done { get; set; }

            public int Thread { get; set; }

            /// <summary>The trap the chore's function returns to while it runs; 0 before it starts.</summary>
            public long ReturnTrap { get; set; }
        }

        private readonly List<Chore> _running = new();

        private sealed class Worker(int id)
        {
            public int Id { get; } = id;

            /// <summary>Where the thread returns when it has no more work: its exit (via the loader, if any).</summary>
            public long Exit { get; set; }

            public Chore? Current { get; set; }
        }

        private sealed record Beacon(long Reference, Collection? Owner);

        private readonly Dictionary<long, Token> _tokens = new();
        private readonly Dictionary<long, Collection> _collections = new();
        private readonly List<Chore> _queue = new();
        private readonly Dictionary<int, Worker> _workers = new();
        private readonly Dictionary<int, List<Collection?>> _executing = new();
        private readonly Dictionary<long, int> _runningRegistrations = new();
        private readonly Dictionary<long, (long Proc, long Data)> _hostRegistrations = new();
        private readonly Dictionary<long, Beacon> _beacons = new();
        private readonly Dictionary<int, long> _inlineDepth = new();
        private readonly Dictionary<int, long> _contextOf = new();
        private readonly Dictionary<long, int> _threadOfContext = new();
        private long _tokenVtable, _registrationVtable, _collectionVtable, _workerEntry;
        private uint _asyncId;

        public ConcurrencyRuntime(ArmEmulator emulator, CallFrame frame, Func<MsvcpCore> msvcp)
        {
            _e = emulator;
            _f = frame;
            _msvcp = msvcp;
            _e.GuestThreadAbandoned += OnThreadAbandoned;
        }

        /// <summary>What the image asked the task machinery to do, bounded.</summary>
        public List<string> Log { get; } = new();

        /// <summary>Chores run to completion (inline or on a worker).</summary>
        public int ChoresRun { get; private set; }

        /// <summary>ConcRT worker threads started.</summary>
        public int WorkersStarted { get; private set; }

        private void Note(string line)
        {
            if (Log.Count < 400)
            {
                Log.Add(line);
            }
        }

        private long Arg(int i) => _f.Arg(i);

        private void Return(long value) => _f.Return(value);

        private long Read(long address) => _e.ReadUInt32(address, 0);

        private void Write(long address, long value) => _e.WriteUInt32(address, (uint)value);

        private int Tid => _e.CurrentThreadId;

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            RegisterAllocator(handlers);
            RegisterExceptions(handlers);
            RegisterTokens(handlers);
            RegisterCollections(handlers);
            RegisterScheduler(handlers);
        }

        // ------------------------------------------------------------------------------------
        // Concurrency::Alloc / Free and the small scheduler queries
        // ------------------------------------------------------------------------------------

        private void RegisterAllocator(Dictionary<string, Action> handlers)
        {
            // void* Alloc(size_t): ConcRT's sub-allocator; bad_alloc on failure. Free(void*) gives a
            // block back. Both are the one heap here.
            handlers[$"?Alloc{C}YAPAXI@Z"] = () =>
            {
                long block = _e.AllocateHeap(Math.Max(Arg(0), 1));
                if (block == 0)
                {
                    StdThrow(_msvcp().BadAlloc, "bad allocation");
                    return;
                }

                Return(block);
            };
            handlers[$"?Free{C}YAXPAX@Z"] = () =>
            {
                _e.FreeHeap(Arg(0));
                Return(0);
            };
        }

        private void StdThrow(MsvcpCore.StdClass type, string? message)
        {
            MsvcpCore msvcp = _msvcp();
            msvcp.Throw(type, msvcp.NewObject(type, message, owned: false));
        }

        // ------------------------------------------------------------------------------------
        // ConcRT's exceptions (concrt.h): each is a std::exception with a (const char*) and a () ctor
        // ------------------------------------------------------------------------------------

        private static readonly string[] ExceptionClasses =
        [
            "task_canceled", "invalid_operation", "missing_wait", "improper_lock", "improper_scheduler_attach",
            "improper_scheduler_detach", "improper_scheduler_reference", "scheduler_not_attached",
            "context_unblock_unbalanced", "context_self_unblock", "invalid_oversubscribe_operation",
            "nested_scheduler_missing_detach", "default_scheduler_exists", "operation_timed_out",
            "bad_target", "message_not_found", "invalid_link_target", "invalid_multiple_scheduling",
        ];

        private void RegisterExceptions(Dictionary<string, Action> handlers)
        {
            foreach (string name in ExceptionClasses)
            {
                // name(const char* message) throw(): exception(message), which copies a non-null message.
                handlers[$"??0{name}{C}QAA@PBD@Z"] = () =>
                {
                    long self = Arg(0);
                    _msvcp().InitObject(_msvcp().ConcurrencyException(name), self, Arg(1) == 0 ? null : _f.ReadNarrowString(Arg(1), 4096));
                    Return(self);
                };

                // name() throw(): exception() - no message, what() is "Unknown exception".
                handlers[$"??0{name}{C}QAA@XZ"] = () =>
                {
                    long self = Arg(0);
                    _msvcp().InitObject(_msvcp().ConcurrencyException(name), self, null);
                    Return(self);
                };
            }

            // void cancel_current_task() (MSVCP110): throw task_canceled(); - how a task body cancels itself.
            handlers[$"?cancel_current_task{C}YAXXZ"] = () =>
            {
                MsvcpCore.StdClass type = _msvcp().ConcurrencyException("task_canceled");
                _msvcp().Throw(type, _msvcp().NewObject(type, null));
            };
        }

        // ------------------------------------------------------------------------------------
        // Cancellation tokens and registrations
        // ------------------------------------------------------------------------------------

        private static bool IsToken(long token) => token != 0 && token != TokenNone;

        private bool TokenCanceled(long token) => IsToken(token) && Read(token + TokenStateFlag) != 0;

        private bool IsCanceled(Collection c) => c.Canceled || TokenCanceled(c.Token);

        private Token TokenAt(long address)
        {
            if (!_tokens.TryGetValue(address, out Token? token))
            {
                token = _tokens[address] = new Token(address);
            }

            return token;
        }

        private void RegisterTokens(Dictionary<string, Action> handlers)
        {
            _tokenVtable = HostVtable("_CancellationTokenState",
                ("`scalar deleting destructor'", () => DestroyToken(Arg(0), (Arg(1) & 1) != 0)),
                ("_Destroy", () => DestroyToken(Arg(0), free: true)));
            _registrationVtable = HostVtable("CancellationTokenRegistration_TaskProc",
                ("`scalar deleting destructor'", () =>
                {
                    long self = Arg(0);
                    if ((Arg(1) & 1) != 0)
                    {
                        _hostRegistrations.Remove(self);
                        _e.FreeHeap(self);
                    }

                    Return(self);
                }),
                ("_Destroy", () =>
                {
                    _hostRegistrations.Remove(Arg(0));
                    _e.FreeHeap(Arg(0));
                    Return(0);
                }),
                ("_Exec", () =>
                {
                    (long proc, long data) = _hostRegistrations.GetValueOrDefault(Arg(0));
                    long back = _e.ReturnAddress;
                    if (proc == 0)
                    {
                        Return(0);
                        return;
                    }

                    _e.CallEmulated("cancellation callback", proc, [data], () => { Return(0); _e.ContinueAt(back); }, recycle: true);
                }));

            // static _CancellationTokenState* _NewTokenState(): refcount 1, not canceled.
            handlers[$"?_NewTokenState@_CancellationTokenState{D}SAPAV123@XZ"] = () =>
            {
                long token = _e.AllocateHeap(0x20);
                _e.WriteMemory(token, new byte[0x20]);
                Write(token, _tokenVtable);
                Write(token + 4, 1);
                TokenAt(token);
                Return(token);
            };

            // void _Cancel(): sets the flag once, then runs every registered callback, in order, on
            // this thread, and drops the token's reference to each. A second cancel does nothing.
            handlers[$"?_Cancel@_CancellationTokenState{D}QAAXXZ"] = () =>
            {
                long address = Arg(0);
                long back = _e.ReturnAddress;
                if (!IsToken(address) || Read(address + TokenStateFlag) != 0)
                {
                    Return(0);
                    return;
                }

                Write(address + TokenStateFlag, 1);
                Token token = TokenAt(address);
                List<long> callbacks = token.Registrations.ToList();
                token.Registrations.Clear();
                Note($"token 0x{address:X8} canceled; {callbacks.Count} callback(s)");
                foreach (Collection c in _collections.Values.Where(c => c.Token == address))
                {
                    RaiseBeacons(c);
                }

                InvokeAll(callbacks, 0, () => Finish(back, 0));
            };

            // _CancellationTokenRegistration* _RegisterCallback(TaskProc, void* data, int initialRefs):
            // wraps the callback in a registration (initialRefs references for the caller) and registers it.
            handlers[$"?_RegisterCallback@_CancellationTokenState{D}QAAPAV_CancellationTokenRegistration@23@P6AXPAX@Z0H@Z"] = () =>
            {
                long token = Arg(0);
                long registration = _e.AllocateHeap(RegistrationSize + 8);
                _e.WriteMemory(registration, new byte[RegistrationSize + 8]);
                Write(registration, _registrationVtable);
                Write(registration + 4, Math.Max(_f.SignedArg(3), 0));
                Write(registration + RegistrationState, StateCalled);
                Write(registration + RegistrationSize, Arg(1));
                Write(registration + RegistrationSize + 4, Arg(2));
                _hostRegistrations[registration] = (Arg(1), Arg(2));
                Register(token, registration, _e.ReturnAddress, result: registration);
            };

            // void _RegisterCallback(_CancellationTokenRegistration*): state CLEAR, a reference for the
            // token, the token pointer; invoked at once if the token is already canceled.
            handlers[$"?_RegisterCallback@_CancellationTokenState{D}QAAXPAV_CancellationTokenRegistration@23@@Z"] = () =>
                Register(Arg(0), Arg(1), _e.ReturnAddress, result: 0);

            // void _DeregisterCallback(_CancellationTokenRegistration*): unlinks it and drops the token's
            // reference; if cancellation already took it and its callback is running on another thread,
            // waits for that callback to finish (a callback deregistering itself does not wait).
            handlers[$"?_DeregisterCallback@_CancellationTokenState{D}QAAXPAV_CancellationTokenRegistration@23@@Z"] = () =>
            {
                long address = Arg(0), registration = Arg(1);
                long back = _e.ReturnAddress;
                Return(0);
                if (registration == 0)
                {
                    return;
                }

                if (_tokens.TryGetValue(address, out Token? token) && token.Registrations.Remove(registration))
                {
                    Write(registration + RegistrationState, StateSynchronize);
                    Release(registration, () => Finish(back, 0));
                    return;
                }

                if (_runningRegistrations.TryGetValue(registration, out int thread) && thread != Tid)
                {
                    _e.BlockCurrentThread(() => !_runningRegistrations.ContainsKey(registration),
                        why: "_DeregisterCallback waiting for a running cancellation callback");
                }
            };
        }

        private void Register(long address, long registration, long back, long result)
        {
            Return(result);
            if (registration == 0 || !IsToken(address))
            {
                return;
            }

            Write(registration + RegistrationState, StateClear);
            Write(registration + 4, Read(registration + 4) + 1);
            Write(registration + RegistrationToken, address);
            if (Read(address + TokenStateFlag) != 0)
            {
                Invoke(registration, () => Finish(back, result));
                return;
            }

            TokenAt(address).Registrations.Add(registration);
        }

        private void InvokeAll(List<long> registrations, int index, Action then)
        {
            if (index >= registrations.Count)
            {
                then();
                return;
            }

            Invoke(registrations[index], () => InvokeAll(registrations, index + 1, then));
        }

        /// <summary>_CancellationTokenRegistration::_Invoke: CLEAR -> running, _Exec(), -> CALLED, _Release().</summary>
        private void Invoke(long registration, Action then)
        {
            if (Read(registration + RegistrationState) != StateClear)
            {
                Release(registration, then);
                return;
            }

            Write(registration + RegistrationState, 0x1000 + (Tid * 4));
            _runningRegistrations[registration] = Tid;
            long exec = _hostRegistrations.TryGetValue(registration, out var proc) ? proc.Proc : Read(Read(registration) + 8);
            long argument = _hostRegistrations.ContainsKey(registration) ? proc.Data : registration;
            _e.CallEmulated("cancellation callback", exec, [argument], () =>
            {
                Write(registration + RegistrationState, StateCalled);
                _runningRegistrations.Remove(registration);
                Release(registration, then);
            }, recycle: true);
        }

        /// <summary>_RefCounter::_Release: drops a reference, and at zero calls the object's _Destroy (slot 1).</summary>
        private void Release(long self, Action then)
        {
            long count = Read(self + 4) - 1;
            Write(self + 4, count);
            if (count != 0)
            {
                then();
                return;
            }

            _e.CallEmulated("_RefCounter::_Destroy", Read(Read(self) + 4), [self], then, recycle: true);
        }

        /// <summary>~_CancellationTokenState: releases the registrations still in its list, then frees it.</summary>
        private void DestroyToken(long address, bool free)
        {
            long back = _e.ReturnAddress;
            List<long> remaining = _tokens.Remove(address, out Token? token) ? token.Registrations.ToList() : [];
            ReleaseAll(remaining, 0, () =>
            {
                if (free)
                {
                    _e.FreeHeap(address);
                }

                Finish(back, address);
            });
        }

        private void ReleaseAll(List<long> objects, int index, Action then)
        {
            if (index >= objects.Count)
            {
                then();
                return;
            }

            Release(objects[index], () => ReleaseAll(objects, index + 1, then));
        }

        // ------------------------------------------------------------------------------------
        // Task collections
        // ------------------------------------------------------------------------------------

        private Collection CollectionAt(long address)
        {
            if (!_collections.TryGetValue(address, out Collection? c))
            {
                c = _collections[address] = new Collection(address, 0, structured: false);
            }

            return c;
        }

        private void RegisterCollections(Dictionary<string, Action> handlers)
        {
            _collectionVtable = HostVtable("_AsyncTaskCollection",
                ("`scalar deleting destructor'", () => DestroyAsync(Arg(0), (Arg(1) & 1) != 0)),
                ("_Destroy", () => DestroyAsync(Arg(0), free: true)));

            // static _AsyncTaskCollection* _NewCollection(_CancellationTokenState*): refcount 1, its
            // _TaskCollection at +8, canceled when the token is.
            handlers[$"?_NewCollection@_AsyncTaskCollection{D}SAPAV123@PAV_CancellationTokenState@23@@Z"] = () =>
            {
                long block = _e.AllocateHeap(0x40);
                _e.WriteMemory(block, new byte[0x40]);
                Write(block, _collectionVtable);
                Write(block + 4, 1);
                _collections[block + 8] = new Collection(block + 8, Arg(0), structured: false) { Block = block };
                Return(block);
            };

            // void _TaskCollection::_Schedule(_UnrealizedChore*) / _StructuredTaskCollection::_Schedule.
            handlers[$"?_Schedule@_TaskCollection{D}QAAXPAV_UnrealizedChore@23@@Z"] = () => ScheduleChore(CollectionAt(Arg(0)), Arg(1));
            handlers[$"?_Schedule@_StructuredTaskCollection{D}QAAXPAV_UnrealizedChore@23@@Z"] = () => ScheduleChore(CollectionAt(Arg(0)), Arg(1));

            // _TaskCollectionStatus _RunAndWait(_UnrealizedChore* = NULL): runs the chore (if any) here,
            // then everything else scheduled to the collection, and answers _Completed (1) or _Canceled (2).
            handlers[$"?_RunAndWait@_TaskCollection{D}QAA?AW4_TaskCollectionStatus@23@PAV_UnrealizedChore@23@@Z"] =
                () => RunAndWait(CollectionAt(Arg(0)), Arg(1));
            handlers[$"?_RunAndWait@_StructuredTaskCollection{D}QAA?AW4_TaskCollectionStatus@23@PAV_UnrealizedChore@23@@Z"] =
                () => RunAndWait(CollectionAt(Arg(0)), Arg(1));

            // void _TaskCollection::_Cancel(): unstarted chores never run; running ones can see it
            // (is_task_cancellation_requested, beacons); wait answers _Canceled.
            handlers[$"?_Cancel@_TaskCollection{D}QAAXXZ"] = () =>
            {
                Cancel(CollectionAt(Arg(0)));
                Return(0);
            };

            // _StructuredTaskCollection(_CancellationTokenState*): the object is the caller's; its
            // state is kept here.
            handlers[$"??0_StructuredTaskCollection{D}QAA@PAV_CancellationTokenState@12@@Z"] = () =>
            {
                _collections[Arg(0)] = new Collection(Arg(0), Arg(1), structured: true);
                Return(Arg(0));
            };

            // void _Abort(): the group is being destroyed without a wait (an exception is unwinding
            // through it) - cancel it and wait for the chores already running.
            handlers[$"?_Abort@_StructuredTaskCollection{D}AAAXXZ"] = () =>
            {
                Collection c = CollectionAt(Arg(0));
                Cancel(c);
                long back = _e.ReturnAddress;
                Wait(c, null, () => Finish(back, 0));
            };

            // void _CleanupToken(): the group lets go of its token (the destructor's last step).
            handlers[$"?_CleanupToken@_StructuredTaskCollection{D}AAAXXZ"] = () =>
            {
                if (_collections.TryGetValue(Arg(0), out Collection? c))
                {
                    c.Token = 0;
                    if (c.Outstanding == 0)
                    {
                        _collections.Remove(Arg(0));
                    }
                }

                Return(0);
            };

            // void _UnrealizedChore::_CheckTaskCollection(): a task_handle being destroyed. One still
            // queued would run after its storage is gone, so it leaves the queue.
            handlers[$"?_CheckTaskCollection@_UnrealizedChore{D}IAAXXZ"] = () =>
            {
                int index = _queue.FindIndex(c => c.Address == Arg(0) && !c.Started);
                if (index >= 0)
                {
                    Chore chore = _queue[index];
                    _queue.RemoveAt(index);
                    chore.Done = true;
                    if (chore.Owner is { } owner)
                    {
                        owner.Outstanding--;
                    }

                    Note($"task_handle 0x{Arg(0):X8} destroyed while still scheduled; withdrawn");
                }

                Return(0);
            };

            // MSVCP110: bool is_task_cancellation_requested(): the collection of the chore this thread
            // is running has been canceled (by _Cancel or through its token).
            handlers[$"?is_task_cancellation_requested{C}YA_NXZ"] = () =>
                Return(CurrentCollection() is { } c && IsCanceled(c) ? 1 : 0);

            // Called when a task's exception was never observed: recorded, as ConcRT's debug report would.
            handlers[$"?_ReportUnobservedException{D}YAXXZ"] = () =>
            {
                Note("_ReportUnobservedException - a task exception went unobserved");
                Return(0);
            };
        }

        private void DestroyAsync(long block, bool free)
        {
            if (_collections.TryGetValue(block + 8, out Collection? c))
            {
                if (c.Outstanding == 0)
                {
                    _collections.Remove(block + 8);
                    if (free)
                    {
                        _e.FreeHeap(block);
                    }
                }
                else
                {
                    c.DestroyPending = free;
                }
            }

            Return(block);
        }

        private void Cancel(Collection c)
        {
            if (!c.Canceled)
            {
                c.Canceled = true;
                Note($"collection 0x{c.Address:X8} canceled ({c.Outstanding} outstanding)");
                RaiseBeacons(c);
            }
        }

        private void ScheduleChore(Collection owner, long chore)
        {
            Return(0);
            if (chore == 0)
            {
                return;
            }

            Write(chore + ChoreCollection, owner.Address);
            owner.Outstanding++;
            Enqueue(new Chore(chore, owner, "_UnrealizedChore"));
        }

        private void Enqueue(Chore chore)
        {
            _queue.Add(chore);
            EnsureWorkers();
        }

        private void RunAndWait(Collection c, long chore)
        {
            long back = _e.ReturnAddress;
            Chore? first = null;
            if (chore != 0)
            {
                Write(chore + ChoreCollection, c.Address);
                c.Outstanding++;
                first = new Chore(chore, c, "_UnrealizedChore (inline)");
            }

            Wait(c, first, () => Finish(back, IsCanceled(c) ? StatusCanceled : StatusCompleted));
        }

        /// <summary>
        /// ConcRT's wait on a collection: run <paramref name="first"/> here, then steal its unstarted
        /// chores from the queue and run them here too, then block until those running elsewhere end.
        /// </summary>
        private void Wait(Collection c, Chore? first, Action then)
        {
            void Step()
            {
                if (first is { } mine)
                {
                    first = null;
                    RunChore(mine, Step);
                    return;
                }

                int queued = _queue.FindIndex(q => q.Owner == c && !q.Started);
                if (queued >= 0)
                {
                    Chore steal = _queue[queued];
                    _queue.RemoveAt(queued);
                    RunChore(steal, Step);
                    return;
                }

                if (c.Running > 0)
                {
                    bool resumed = false;
                    bool switched = _e.BlockCurrentThread(
                        () => c.Running == 0 || _queue.Any(q => q.Owner == c && !q.Started),
                        () => { resumed = true; Step(); },
                        $"waiting on task collection 0x{c.Address:X8}");
                    if (switched || resumed)
                    {
                        return;
                    }

                    // No other thread can run: pump the thread-pool stand-in, whose work may be what
                    // the running chore is waiting for.
                    if (_e.RunOneDeferredCall(Step))
                    {
                        return;
                    }

                    Note($"wait on 0x{c.Address:X8}: {c.Running} chore(s) running on threads that can never finish; returning");
                }

                then();
            }

            Step();
        }

        /// <summary>
        /// Runs one chore on the calling thread and then <paramref name="then"/> (which must end the
        /// import). A chore of a canceled collection is not run. A chore the runtime owns is deleted
        /// (its scalar deleting destructor, slot 0) once it has finished.
        /// </summary>
        private void RunChore(Chore chore, Action then)
        {
            Collection? owner = chore.Owner;
            long function = chore.RawFunction != 0 ? chore.RawFunction : Read(chore.Address + ChoreFunction);
            if ((owner is not null && IsCanceled(owner)) || function == 0)
            {
                Complete(chore, then);
                return;
            }

            chore.Started = true;
            chore.Thread = Tid;
            if (owner is not null)
            {
                owner.Running++;
            }

            Executing(Tid).Add(owner);
            long argument = chore.RawFunction != 0 ? chore.Argument : chore.Address;
            _running.Add(chore);
            chore.ReturnTrap = _e.CallEmulated(chore.Name, function, [argument], () =>
            {
                _running.Remove(chore);
                chore.ReturnTrap = 0;
                List<Collection?> stack = Executing(chore.Thread);
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                if (owner is not null)
                {
                    owner.Running--;
                }

                ChoresRun++;
                Complete(chore, then);
            }, recycle: true);
        }

        private void Complete(Chore chore, Action then)
        {
            chore.Done = true;
            if (chore.Owner is { } owner)
            {
                owner.Outstanding--;
                if (owner.Outstanding == 0 && owner.DestroyPending)
                {
                    _collections.Remove(owner.Address);
                    _e.FreeHeap(owner.Block);
                }
            }

            if (chore.RawFunction == 0 && (_e.ReadMemory(chore.Address + ChoreRuntimeOwned, 1)[0] & 1) != 0)
            {
                _e.CallEmulated("_UnrealizedChore::`scalar deleting destructor'", Read(Read(chore.Address)), [chore.Address, 1], then, recycle: true);
                return;
            }

            then();
        }

        private List<Collection?> Executing(int thread)
        {
            if (!_executing.TryGetValue(thread, out List<Collection?>? stack))
            {
                stack = _executing[thread] = new List<Collection?>();
            }

            return stack;
        }

        /// <summary>The collection of the chore the calling thread is running, innermost first; null outside any.</summary>
        private Collection? CurrentCollection()
        {
            List<Collection?> stack = Executing(Tid);
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i] is { } c)
                {
                    return c;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------------------------
        // The scheduler: workers, contexts, yielding, beacons
        // ------------------------------------------------------------------------------------

        private void RegisterScheduler(Dictionary<string, Action> handlers)
        {
            _workerEntry = ArmEmulator.ThumbEntry(_e.RegisterVtableMethod("ConcRT worker", WorkerEntry));

            // static void _CurrentScheduler::_ScheduleTask(TaskProc, void* data): light-weight task.
            handlers[$"?_ScheduleTask@_CurrentScheduler{D}SAXP6AXPAX@Z0@Z"] = () =>
            {
                long proc = Arg(0), data = Arg(1);
                Return(0);
                if (proc != 0)
                {
                    Enqueue(new Chore(0, null, "Concurrency::ScheduledTask") { RawFunction = proc, Argument = data });
                }
            };

            // One emulated CPU: one virtual processor, the true answer to both.
            handlers[$"?_GetConcurrency{D}YAIXZ"] = () => Return(1);
            handlers[$"?_GetNumberOfVirtualProcessors@_CurrentScheduler{D}SAIXZ"] = () => Return(1);
            handlers[$"?_Id@_CurrentScheduler{D}SAIXZ"] = () => Return(1);

            // static unsigned& _StackGuard::_GetCurrentInlineDepth(): the context's inline depth, a
            // reference - so a cell per thread that stays put.
            handlers[$"?_GetCurrentInlineDepth@_StackGuard{D}CAAAIXZ"] = () =>
            {
                if (!_inlineDepth.TryGetValue(Tid, out long cell))
                {
                    cell = _inlineDepth[Tid] = _e.AllocateHeap(4);
                    Write(cell, 0);
                }

                Return(cell);
            };

            // MSVCP110: unsigned _GetNextAsyncId(): ppltasks' task ids, 1, 2, 3...
            handlers[$"?_GetNextAsyncId{D}YAIXZ"] = () => Return(++_asyncId);

            // void _Trace_ppl_function(const GUID&, unsigned char level, ConcRT_EventType): an ETW event; no
            // session listens here.
            handlers[$"?_Trace_ppl_function{C}YAXABU_GUID@@EW4ConcRT_EventType@1@@Z"] = () => Return(0);

            // void Concurrency::wait(unsigned ms): the context sleeps, letting others run.
            handlers[$"?wait{C}YAXI@Z"] = Yield;

            // static void _Context::_Yield() / details::_UnderlyingYield(): give up the CPU.
            handlers[$"?_Yield@_Context{D}SAXXZ"] = Yield;
            handlers[$"?_UnderlyingYield{D}YAXXZ"] = Yield;

            // bool _SpinWait<1>::_SpinOnce(): one turn of a spin; here every turn yields, so another
            // thread can make the awaited thing happen. True: keep spinning.
            handlers[$"?_SpinOnce@?$_SpinWait@$00{D}QAA_NXZ"] = () =>
            {
                Yield();
                Return(1);
            };

            // static void _Context::_Oversubscribe(bool begin): this context is about to block; ConcRT
            // adds a virtual processor so queued work does not stall behind it.
            handlers[$"?_Oversubscribe@_Context{D}SAX_N@Z"] = () =>
            {
                bool begin = (Arg(0) & 0xFF) != 0;
                Return(0);
                if (begin)
                {
                    EnsureWorkers(oversubscribed: true);
                }
            };

            // static _Context _Context::_CurrentContext(): a class returned through the hidden pointer in
            // r0 (Lumia.Imaging reads the Context* back through r0): one Context identity per thread.
            handlers[$"?_CurrentContext@_Context{D}SA?AV123@XZ"] = () =>
            {
                long result = Arg(0);
                if (!_contextOf.TryGetValue(Tid, out long context))
                {
                    context = _contextOf[Tid] = _e.AllocateHeap(16);
                    _e.WriteMemory(context, new byte[16]);
                    _threadOfContext[context] = Tid;
                }

                if (result != 0)
                {
                    Write(result, context);
                }

                Return(result);
            };

            // bool _Context::_IsSynchronouslyBlocked() const: that context's thread is blocked in a wait.
            handlers[$"?_IsSynchronouslyBlocked@_Context{D}QBA_NXZ"] = () =>
            {
                long context = Arg(0) == 0 ? 0 : Read(Arg(0));
                Return(_threadOfContext.TryGetValue(context, out int thread) && _e.IsThreadBlocked(thread) ? 1 : 0);
            };

            // _Cancellation_beacon(): a _Beacon_reference { long signals } tied to the collection this
            // thread is running a chore of; raised when that collection is canceled.
            handlers[$"??0_Cancellation_beacon{D}QAA@XZ"] = () =>
            {
                long self = Arg(0);
                Collection? owner = CurrentCollection();
                long reference = _e.AllocateHeap(4);
                Write(reference, owner is not null && IsCanceled(owner) ? 1 : 0);
                if (self != 0)
                {
                    Write(self, reference);
                    _beacons[self] = new Beacon(reference, owner);
                }

                Return(self);
            };
            handlers[$"??1_Cancellation_beacon{D}QAA@XZ"] = () =>
            {
                if (_beacons.Remove(Arg(0), out Beacon? beacon))
                {
                    _e.FreeHeap(beacon.Reference);
                }

                Return(0);
            };

            // bool _Confirm_cancel(): the signal was raised - is the collection really canceling? If
            // not, the signal is lowered again.
            handlers[$"?_Confirm_cancel@_Cancellation_beacon{D}QAA_NXZ"] = () =>
            {
                if (!_beacons.TryGetValue(Arg(0), out Beacon? beacon))
                {
                    Return(0);
                    return;
                }

                bool canceling = beacon.Owner is { } owner && IsCanceled(owner);
                long signals = Read(beacon.Reference);
                if (!canceling && signals > 0)
                {
                    Write(beacon.Reference, signals - 1);
                }

                Return(canceling ? 1 : 0);
            };
        }

        private void RaiseBeacons(Collection c)
        {
            foreach (Beacon beacon in _beacons.Values.Where(b => b.Owner == c))
            {
                Write(beacon.Reference, Read(beacon.Reference) + 1);
            }
        }

        private void Yield()
        {
            Return(0);
            if (!_e.BlockCurrentThread(() => true, why: "ConcRT yield"))
            {
                _e.YieldToDeferredWork();
            }
        }

        /// <summary>
        /// Starts a worker for queued work no existing worker can take: one that is not blocked will come
        /// back to the queue when its chore ends, one that is blocked will not.
        /// </summary>
        private void EnsureWorkers(bool oversubscribed = false)
        {
            int available = _workers.Values.Count(w => w.Current is null || !_e.IsThreadBlocked(w.Id));
            int wanted = _queue.Count(c => !c.Started) + (oversubscribed ? 1 : 0);
            while (available < wanted && _workers.Count < MaxWorkers)
            {
                int id = _e.CreateGuestThread("ConcRT worker", _workerEntry, 0);
                _workers[id] = new Worker(id);
                WorkersStarted++;
                available++;
            }
        }

        private void WorkerEntry()
        {
            if (!_workers.TryGetValue(Tid, out Worker? worker))
            {
                worker = _workers[Tid] = new Worker(Tid);
            }

            worker.Exit = _e.ReturnAddress;
            WorkerNext(worker);
        }

        private void WorkerNext(Worker worker)
        {
            int next = _queue.FindIndex(c => !c.Started);
            if (next < 0)
            {
                _workers.Remove(worker.Id);
                Return(0);
                _e.ContinueAt(worker.Exit);
                return;
            }

            Chore chore = _queue[next];
            _queue.RemoveAt(next);
            worker.Current = chore;
            RunChore(chore, () =>
            {
                worker.Current = null;
                WorkerNext(worker);
            });
        }

        /// <summary>
        /// The return traps of the chores running on the current thread, innermost first: where an
        /// exception nothing in a chore caught is caught by the runtime instead.
        /// </summary>
        /// <remarks>
        /// MSVCR110 calls every chore inside <c>catch (...)</c> (<c>_UnrealizedChore</c>'s wrapper):
        /// the collection records the exception (<c>_RaisedException</c>), is canceled, and the worker
        /// goes on to its next chore. VC11's ppltasks relies on it - its task handle catches a
        /// <c>Platform::Exception^</c>, hands it to the task (<c>_CancelWithException</c>) and then
        /// rethrows it into that wrapper. Ending the worker instead (the old policy) left the
        /// chore's own frames un-unwound and Halo: Spartan Assault waiting on its save for ever.
        /// </remarks>
        public IEnumerable<long> RunningChoreTraps()
        {
            int me = Tid;
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (_running[i].Thread == me && _running[i].ReturnTrap != 0)
                {
                    yield return _running[i].ReturnTrap;
                }
            }
        }

        /// <summary>
        /// An exception escaped the chore returning to <paramref name="returnTrap"/>: its collection is
        /// canceled, as ConcRT's <c>_RaisedException</c> does. The caller then returns through the trap.
        /// </summary>
        public void ChoreRaised(long returnTrap, string what)
        {
            Chore? chore = _running.Find(c => c.ReturnTrap == returnTrap);
            if (chore?.Owner is { } owner)
            {
                Cancel(owner);
            }

            ChoresRaised++;
            Note($"chore 0x{chore?.Address ?? 0:X8} raised {what}; caught by the runtime, its collection is canceled");
        }

        /// <summary>Chores an exception escaped from, caught by the runtime's chore wrapper.</summary>
        public int ChoresRaised { get; private set; }

        /// <summary>A worker ended by an exception nothing caught: its chore counts as finished, and its collection canceled.</summary>
        private void OnThreadAbandoned(int thread)
        {
            _running.RemoveAll(c => c.Thread == thread);
            if (!_workers.Remove(thread, out Worker? worker) || worker.Current is not { } chore)
            {
                return;
            }

            chore.Done = true;
            if (chore.Owner is { } owner)
            {
                owner.Running--;
                owner.Outstanding--;
                Cancel(owner);
            }

            _executing.Remove(thread);
            Note($"worker {thread} died running chore 0x{chore.Address:X8}; its collection is canceled");
        }

        // ------------------------------------------------------------------------------------

        private void Finish(long back, long result)
        {
            Return(result);
            _e.ContinueAt(back);
        }

        private long HostVtable(string name, params (string Slot, Action Body)[] slots)
        {
            long vtable = _e.AllocateHeap(slots.Length * 4);
            for (int i = 0; i < slots.Length; i++)
            {
                Write(vtable + (i * 4), ArmEmulator.ThumbEntry(_e.RegisterVtableMethod($"{name}::{slots[i].Slot}", slots[i].Body)));
            }

            return vtable;
        }
    }
}
