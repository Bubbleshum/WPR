namespace WPR.Wp8Native
{
    /// <summary>
    /// api-ms-win-core-synch to its documented contract on the cooperative guest-thread model:
    /// named events, mutexes and semaphores with their errors, single and multiple waits that
    /// really block (with a timeout that really elapses), critical sections, SRW locks, condition
    /// variables and one-time initialisation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Threads switch only when one blocks, so a lock is contended only when its holder blocked
    /// while holding it. That is exactly when a no-op lock lets a second thread into the middle of
    /// the first one's update, so locks are real: a contended acquire blocks the caller and the
    /// holder runs. If nothing else can run, nobody could ever release it; the acquire then takes
    /// the lock rather than hang, which is ThreadLibrary's rule for <c>_Mtx</c> too.
    /// </para>
    /// <para>
    /// Critical sections and SRW locks keep their state in the guest's own structure, so a lock in
    /// memory that is reused (zeroed by its new owner's initialiser) is a new lock. CRT lock
    /// numbers (<c>_lock(int)</c>, which reaches EnterCriticalSection with a small integer) are kept
    /// on the host side.
    /// </para>
    /// </remarks>
    public sealed partial class SyncLibrary
    {
        private const long WaitAbandoned0 = 0x80;
        private const long WaitFailed = 0xFFFFFFFF;
        private const long Infinite = 0xFFFFFFFF;

        /// <summary>GetCurrentThread's (HANDLE)-2.</summary>
        private const long CurrentThreadPseudoHandle = 0xFFFFFFFE;

        private const int ErrorInvalidHandle = 6;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorFileNotFound = 2;
        private const int ErrorNotOwner = 288;
        private const int ErrorTooManyPosts = 298;
        private const int ErrorTimeout = 1460;
        private const int ErrorGenFailure = 31;

        /// <summary>The thread id GetCurrentThreadId reports, which is what OwningThread holds.</summary>
        private int Me => 0x1000 + _emulator.CurrentThreadId;

        private int LastError
        {
            set => _emulator.Stubs.Files.LastError = value;
        }

        private bool _abandonHooked;

        private void RegisterWin32(Dictionary<string, Action> handlers)
        {
            if (!_abandonHooked)
            {
                _abandonHooked = true;
                _emulator.GuestThreadAbandoned += OnThreadAbandoned;
            }

            // --- events, mutexes, semaphores ---
            // HANDLE CreateEventExW(SECURITY_ATTRIBUTES*, LPCWSTR name, DWORD flags, DWORD access)
            handlers["CreateEventExW"] = () => CreateEvent(WideName(1), (_frame.Arg(2) & FlagManualReset) != 0, (_frame.Arg(2) & FlagInitialSet) != 0);
            handlers["CreateEventExA"] = () => CreateEvent(NarrowName(1), (_frame.Arg(2) & FlagManualReset) != 0, (_frame.Arg(2) & FlagInitialSet) != 0);

            // The pre-Windows-8 spelling: bManualReset and bInitialState are separate BOOLs.
            handlers["CreateEventW"] = () => CreateEvent(WideName(3), _frame.Arg(1) != 0, _frame.Arg(2) != 0);

            // HANDLE OpenEventW(DWORD access, BOOL inherit, LPCWSTR name)
            handlers["OpenEventW"] = () => OpenNamed(WideName(2), Kind.Event);
            handlers["OpenEventA"] = () => OpenNamed(NarrowName(2), Kind.Event);
            handlers["OpenMutexW"] = () => OpenNamed(WideName(2), Kind.Mutex);
            handlers["OpenSemaphoreW"] = () => OpenNamed(WideName(2), Kind.Semaphore);

            // HANDLE CreateMutexExW(SECURITY_ATTRIBUTES*, LPCWSTR name, DWORD flags, DWORD access):
            // CREATE_MUTEX_INITIAL_OWNER (1) makes the caller the owner.
            handlers["CreateMutexExW"] = () => CreateMutex(WideName(1), (_frame.Arg(2) & 1) != 0);
            handlers["CreateMutexExA"] = () => CreateMutex(NarrowName(1), (_frame.Arg(2) & 1) != 0);
            handlers["CreateMutexW"] = () => CreateMutex(WideName(2), _frame.Arg(1) != 0);

            // BOOL ReleaseMutex(HANDLE): only the owner may; anyone else is ERROR_NOT_OWNER.
            handlers["ReleaseMutex"] = () =>
            {
                if (!_events.TryGetValue(_frame.Arg(0), out Event? e) || e.Kind != Kind.Mutex)
                {
                    FailBool(ErrorInvalidHandle);
                    return;
                }

                if (e.Owner != _emulator.CurrentThreadId || e.Recursion == 0)
                {
                    FailBool(ErrorNotOwner);
                    return;
                }

                if (--e.Recursion == 0)
                {
                    e.Owner = 0;
                }

                _frame.Return(1);
            };

            // HANDLE CreateSemaphoreExW(SECURITY_ATTRIBUTES*, LONG initial, LONG maximum, LPCWSTR
            // name, DWORD flags, DWORD access): 0 <= initial <= maximum, maximum > 0.
            handlers["CreateSemaphoreExW"] = () => CreateSemaphore(_frame.SignedArg(1), _frame.SignedArg(2), WideName(3));
            handlers["CreateSemaphoreW"] = handlers["CreateSemaphoreExW"];

            // BOOL ReleaseSemaphore(HANDLE, LONG count, LONG* previous): a release that would pass
            // the maximum is ERROR_TOO_MANY_POSTS and changes nothing.
            handlers["ReleaseSemaphore"] = () =>
            {
                if (!_events.TryGetValue(_frame.Arg(0), out Event? e) || e.Kind != Kind.Semaphore)
                {
                    FailBool(ErrorInvalidHandle);
                    return;
                }

                long count = _frame.SignedArg(1);
                if (count <= 0)
                {
                    FailBool(ErrorInvalidParameter);
                    return;
                }

                if (e.Count + count > e.Maximum)
                {
                    FailBool(ErrorTooManyPosts);
                    return;
                }

                if (_frame.Arg(2) != 0)
                {
                    _emulator.WriteUInt32(_frame.Arg(2), (uint)e.Count);
                }

                e.Count += count;
                _frame.Return(1);
            };

            handlers["SetEvent"] = () => SetEventState(true);
            handlers["ResetEvent"] = () => SetEventState(false);

            // DWORD WaitForSingleObject(Ex)(HANDLE, DWORD milliseconds[, BOOL alertable])
            handlers["WaitForSingleObjectEx"] = () => WaitAny([_frame.Arg(0)], waitAll: false, _frame.Arg(1));
            handlers["WaitForSingleObject"] = handlers["WaitForSingleObjectEx"];

            // DWORD WaitForMultipleObjects(Ex)(DWORD count, const HANDLE*, BOOL waitAll, DWORD ms[, BOOL alertable])
            handlers["WaitForMultipleObjectsEx"] = WaitForMultipleObjects;
            handlers["WaitForMultipleObjects"] = WaitForMultipleObjects;

            // CloseHandle on one of these drops that handle; the object goes with its last handle.
            Action closeOther = handlers.TryGetValue("CloseHandle", out Action? existing) ? existing : () => _frame.Return(1);
            handlers["CloseHandle"] = () =>
            {
                if (_events.Remove(_frame.Arg(0)))
                {
                    _frame.Return(1);
                    return;
                }

                closeOther();
            };

            // --- critical sections ---
            handlers["InitializeCriticalSectionEx"] = () => { InitialiseSection(_frame.Arg(0), _frame.Arg(1)); _frame.Return(1); };
            handlers["InitializeCriticalSectionAndSpinCount"] = handlers["InitializeCriticalSectionEx"];
            handlers["InitializeCriticalSection"] = () => { InitialiseSection(_frame.Arg(0), 0); _frame.Return(0); };
            handlers["EnterCriticalSection"] = () => { long section = _frame.Arg(0); _frame.Return(0); EnterSection(section); };
            handlers["TryEnterCriticalSection"] = () => _frame.Return(TryEnterSection(_frame.Arg(0)) ? 1 : 0);
            handlers["LeaveCriticalSection"] = () => { LeaveSection(_frame.Arg(0), all: false); _frame.Return(0); _emulator.TimeSlice(); };
            handlers["DeleteCriticalSection"] = () =>
            {
                _hostSections.Remove(_frame.Arg(0));
                _heldSections.Remove(_frame.Arg(0));
                _frame.Return(0);
            };

            // --- SRW locks ---
            handlers["InitializeSRWLock"] = () => { WriteWord(_frame.Arg(0), 0); _frame.Return(0); };
            handlers["AcquireSRWLockExclusive"] = () => { long srw = _frame.Arg(0); _frame.Return(0); AcquireSrw(srw, exclusive: true); };
            handlers["AcquireSRWLockShared"] = () => { long srw = _frame.Arg(0); _frame.Return(0); AcquireSrw(srw, exclusive: false); };
            handlers["TryAcquireSRWLockExclusive"] = () => _frame.Return(TryAcquireSrw(_frame.Arg(0), exclusive: true) ? 1 : 0);
            handlers["TryAcquireSRWLockShared"] = () => _frame.Return(TryAcquireSrw(_frame.Arg(0), exclusive: false) ? 1 : 0);
            handlers["ReleaseSRWLockExclusive"] = () => { ReleaseSrw(_frame.Arg(0), exclusive: true); _frame.Return(0); };
            handlers["ReleaseSRWLockShared"] = () => { ReleaseSrw(_frame.Arg(0), exclusive: false); _frame.Return(0); };

            // --- condition variables ---
            handlers["InitializeConditionVariable"] = () =>
            {
                WriteWord(_frame.Arg(0), 0);
                _conditions.Remove(_frame.Arg(0));
                _frame.Return(0);
            };
            handlers["WakeConditionVariable"] = () => { Wake(_frame.Arg(0), all: false); _frame.Return(0); };
            handlers["WakeAllConditionVariable"] = () => { Wake(_frame.Arg(0), all: true); _frame.Return(0); };

            // BOOL SleepConditionVariableCS(CONDITION_VARIABLE*, CRITICAL_SECTION*, DWORD ms)
            handlers["SleepConditionVariableCS"] = () => SleepCondition(_frame.Arg(0), _frame.Arg(1), _frame.Arg(2), srw: false, shared: false);

            // BOOL SleepConditionVariableSRW(CONDITION_VARIABLE*, SRWLOCK*, DWORD ms, ULONG flags):
            // CONDITION_VARIABLE_LOCKMODE_SHARED (1) says the lock is held shared.
            handlers["SleepConditionVariableSRW"] = () => SleepCondition(_frame.Arg(0), _frame.Arg(1), _frame.Arg(2), srw: true, shared: (_frame.Arg(3) & 1) != 0);

            // --- one-time initialisation ---
            handlers["InitOnceInitialize"] = () => { WriteWord(_frame.Arg(0), 0); _frame.Return(0); };
            handlers["InitOnceExecuteOnce"] = () => ExecuteOnce(_frame.Arg(0), _frame.Arg(1), _frame.Arg(2), _frame.Arg(3));
            handlers["InitOnceBeginInitialize"] = BeginInitialize;
            handlers["InitOnceComplete"] = CompleteInitialize;
        }

        private void FailBool(int error)
        {
            LastError = error;
            _frame.Return(0);
        }

        private string WideName(int argument) => _frame.Arg(argument) == 0 ? string.Empty : _emulator.ReadUtf16String(_frame.Arg(argument), 260);

        private string NarrowName(int argument) => _frame.Arg(argument) == 0 ? string.Empty : _frame.ReadNarrowString(_frame.Arg(argument), 260);

        // ---------------------------------------------------------------------------
        // kernel objects
        // ---------------------------------------------------------------------------

        private long NewHandle(Event e)
        {
            long handle = _nextHandle++;
            _events[handle] = e;
            return handle;
        }

        /// <summary>
        /// The object a create names, if one exists: then the create opens it and reports
        /// ERROR_ALREADY_EXISTS, and a name held by an object of another kind is ERROR_INVALID_HANDLE.
        /// </summary>
        private bool TryOpenExisting(string name, Kind kind)
        {
            if (name.Length == 0 || _events.Values.FirstOrDefault(e => e.Name == name) is not { } existing)
            {
                LastError = 0;
                return false;
            }

            if (existing.Kind != kind)
            {
                LastError = ErrorInvalidHandle;
                _frame.Return(0);
                return true;
            }

            long handle = NewHandle(existing);
            LastError = ErrorAlreadyExists;
            _frame.Return(handle);
            return true;
        }

        private void CreateEvent(string name, bool manualReset, bool initialState)
        {
            if (TryOpenExisting(name, Kind.Event))
            {
                return;
            }

            _frame.Return(NewHandle(new Event { ManualReset = manualReset, Signalled = initialState, Name = name }));
        }

        private void CreateMutex(string name, bool initialOwner)
        {
            if (TryOpenExisting(name, Kind.Mutex))
            {
                return;
            }

            _frame.Return(NewHandle(new Event
            {
                Kind = Kind.Mutex,
                Name = name,
                Owner = initialOwner ? _emulator.CurrentThreadId : 0,
                Recursion = initialOwner ? 1 : 0,
            }));
        }

        private void CreateSemaphore(long initial, long maximum, string name)
        {
            if (maximum <= 0 || initial < 0 || initial > maximum)
            {
                LastError = ErrorInvalidParameter;
                _frame.Return(0);
                return;
            }

            if (TryOpenExisting(name, Kind.Semaphore))
            {
                return;
            }

            _frame.Return(NewHandle(new Event { Kind = Kind.Semaphore, Count = initial, Maximum = maximum, Name = name }));
        }

        /// <summary>Open*: a new handle on a named object, or NULL with ERROR_FILE_NOT_FOUND.</summary>
        private void OpenNamed(string name, Kind kind)
        {
            if (name.Length == 0)
            {
                LastError = ErrorInvalidParameter;
                _frame.Return(0);
                return;
            }

            Event? existing = _events.Values.FirstOrDefault(e => e.Name == name);
            if (existing is null)
            {
                LastError = ErrorFileNotFound;
                _frame.Return(0);
                return;
            }

            if (existing.Kind != kind)
            {
                LastError = ErrorInvalidHandle;
                _frame.Return(0);
                return;
            }

            _frame.Return(NewHandle(existing));
        }

        private void SetEventState(bool signalled)
        {
            if (!_events.TryGetValue(_frame.Arg(0), out Event? e) || e.Kind != Kind.Event)
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            e.Signalled = signalled;
            _frame.Return(1);
        }

        /// <summary>Sets an event from the host, as completed I/O does to an OVERLAPPED's hEvent. False if it is not an event.</summary>
        public bool SignalEvent(long handle)
        {
            if (_events.TryGetValue(handle, out Event? e) && e.Kind == Kind.Event)
            {
                e.Signalled = true;
                return true;
            }

            return false;
        }

        /// <summary>Whether <paramref name="handle"/> is one of this library's objects.</summary>
        public bool OwnsHandle(long handle) => _events.ContainsKey(handle);

        /// <summary>A second handle on the same object (DuplicateHandle), or 0 if the handle is not ours.</summary>
        public long DuplicateObjectHandle(long handle) => _events.TryGetValue(handle, out Event? e) ? NewHandle(e) : 0;

        /// <summary>A real handle on a guest thread: waitable, signalled once the thread has ended.</summary>
        public long CreateThreadHandle(int threadId) => NewHandle(new Event { Kind = Kind.Thread, ThreadId = threadId });

        /// <summary>Whether a wait on <paramref name="e"/> by guest thread <paramref name="thread"/> would succeed now.</summary>
        private bool CanTake(Event e, int thread) => e.Kind switch
        {
            Kind.Mutex => e.Owner == 0 || e.Owner == thread || _emulator.IsThreadFinished(e.Owner),
            Kind.Semaphore => e.Count > 0,
            Kind.Thread => _emulator.IsThreadFinished(e.ThreadId),
            _ => e.Signalled,
        };

        /// <summary>What a satisfied wait does: resets, takes ownership or decrements. True when it took an abandoned mutex.</summary>
        private bool Take(Event e, int thread)
        {
            switch (e.Kind)
            {
                case Kind.Mutex:
                    bool abandoned = e.Abandoned || (e.Owner != 0 && e.Owner != thread && _emulator.IsThreadFinished(e.Owner));
                    if (e.Owner != thread)
                    {
                        e.Recursion = 0;
                    }

                    e.Owner = thread;
                    e.Recursion++;
                    e.Abandoned = false;
                    return abandoned;
                case Kind.Semaphore:
                    e.Count--;
                    return false;
                case Kind.Event when !e.ManualReset:
                    e.Signalled = false;
                    return false;
                default:
                    return false;
            }
        }

        private void OnThreadAbandoned(int id)
        {
            foreach (Event e in _events.Values.Where(e => e.Kind == Kind.Mutex && e.Owner == id))
            {
                e.Owner = 0;
                e.Recursion = 0;
                e.Abandoned = true;
            }

            int owner = 0x1000 + id;
            foreach (long section in _heldSections.Where(s => s.Value == owner).Select(s => s.Key).ToList())
            {
                WriteSection(section, 0, 0);
                _heldSections.Remove(section);
            }
        }

        // ---------------------------------------------------------------------------
        // waits
        // ---------------------------------------------------------------------------

        private void WaitForMultipleObjects()
        {
            long count = _frame.Arg(0);
            long array = _frame.Arg(1);
            if (count is 0 or > 64 || array == 0)
            {
                LastError = ErrorInvalidParameter;
                _frame.Return(WaitFailed);
                return;
            }

            long[] handles = new long[count];
            for (int i = 0; i < count; i++)
            {
                handles[i] = _emulator.ReadUInt32(array + (i * 4));
            }

            WaitAny(handles, waitAll: _frame.Arg(2) != 0, _frame.Arg(3));
        }

        /// <summary>
        /// The wait every WaitFor* is: WAIT_OBJECT_0 + i (or WAIT_ABANDONED_0 + i for a mutex whose
        /// owner died), WAIT_TIMEOUT once the time has elapsed. An infinite or timed wait blocks the
        /// thread, so whoever will signal runs. A handle that is not a synchronisation object here
        /// (a file, the process pseudo-handle) is taken as signalled, as a completed file handle is.
        /// The current-thread pseudo-handle is the exception: a thread is signalled when it ends, so
        /// waiting on yourself can only time out. That is WP8's Sleep - the store API has none, and
        /// SDL's WinRT port (2020: My Country) delays with
        /// <c>WaitForSingleObjectEx(GetCurrentThread(), ms, FALSE)</c>. Answering it at once as
        /// signalled never gave the CPU away, so a worker that polls with it starved every other
        /// thread, the main one included, for ever.
        /// </summary>
        /// <remarks>
        /// A wait that is not satisfied at once, with nothing else able to run, cannot be satisfied
        /// at all: it times out and yields to queued deferred work - which is very often the thing
        /// that would have signalled it - the behaviour this library has always had.
        /// </remarks>
        private void WaitAny(long[] handles, bool waitAll, long milliseconds)
        {
            int me = _emulator.CurrentThreadId;
            Event?[] objects = new Event?[handles.Length];
            for (int i = 0; i < handles.Length; i++)
            {
                objects[i] = handles[i] == CurrentThreadPseudoHandle
                    ? new Event { Kind = Kind.Thread, ThreadId = me, Name = "itself (a sleep)" }
                    : _events.GetValueOrDefault(handles[i]);
            }

            if (TrySatisfy(objects, waitAll, me) is { } now)
            {
                _satisfied++;
                _frame.Return(now);
                return;
            }

            // Separate method so the satisfied-at-once path allocates no closures.
            WaitAnyBlocking(handles, objects, waitAll, milliseconds, me);
        }

        private void WaitAnyBlocking(long[] handles, Event?[] objects, bool waitAll, long milliseconds, int me)
        {
            if (milliseconds != 0)
            {
                long deadline = milliseconds == Infinite ? long.MaxValue : _wallClock.ElapsedMilliseconds + milliseconds;
                _frame.Return(WaitTimeout);
                bool switched = _emulator.BlockCurrentThread(
                    () => Ready(objects, waitAll, me) || _wallClock.ElapsedMilliseconds >= deadline,
                    () =>
                    {
                        if (TrySatisfy(objects, waitAll, me) is { } result)
                        {
                            _satisfied++;
                            _frame.Return(result);
                        }
                        else
                        {
                            _timedOut++;
                            _frame.Return(WaitTimeout);
                        }
                    },
                    why: string.Join(", ", objects.Select((o, i) => o is null ? $"0x{handles[i]:X8}" : $"{o.Kind.ToString().ToLowerInvariant()} {(o.Name.Length > 0 ? o.Name : $"0x{handles[i]:X8}")}")),
                    timeout: milliseconds == Infinite ? null : TimeSpan.FromMilliseconds(milliseconds));
                if (switched)
                {
                    return;
                }
            }

            _timedOut++;
            if (Log.Count < 40)
            {
                Log.Add($"wait on {string.Join(", ", objects.Select((o, i) => o is { Name.Length: > 0 } ? o.Name : $"handle 0x{handles[i]:X8}"))} " +
                        $"({milliseconds} ms) - not signalled");
            }

            _frame.Return(WaitTimeout);
            _emulator.YieldToDeferredWork();
        }

        private bool Ready(Event?[] objects, bool waitAll, int me) =>
            waitAll ? objects.All(o => o is null || CanTake(o, me)) : objects.Any(o => o is null || CanTake(o, me));

        /// <summary>The wait's result if it can be satisfied now (taking the objects), else null.</summary>
        private long? TrySatisfy(Event?[] objects, bool waitAll, int me)
        {
            if (!waitAll)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (objects[i] is not { } o)
                    {
                        return WaitObject0 + i;
                    }

                    if (CanTake(o, me))
                    {
                        return (Take(o, me) ? WaitAbandoned0 : WaitObject0) + i;
                    }
                }

                return null;
            }

            if (!Ready(objects, waitAll: true, me))
            {
                return null;
            }

            int abandoned = -1;
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] is { } o && Take(o, me) && abandoned < 0)
                {
                    abandoned = i;
                }
            }

            return abandoned >= 0 ? WaitAbandoned0 + abandoned : WaitObject0;
        }

        private readonly System.Diagnostics.Stopwatch _wallClock = System.Diagnostics.Stopwatch.StartNew();

        // ---------------------------------------------------------------------------
        // guest words
        // ---------------------------------------------------------------------------

        private uint ReadWord(long address)
        {
            try
            {
                return _emulator.ReadUInt32(address);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void WriteWord(long address, uint value)
        {
            if (address == 0)
            {
                return;
            }

            try
            {
                _emulator.WriteUInt32(address, value);
            }
            catch (Exception)
            {
                // An unmapped lock: there is nothing to keep, and the caller's next access faults.
            }
        }

        // ---------------------------------------------------------------------------
        // critical sections
        // ---------------------------------------------------------------------------

        // ARM CRITICAL_SECTION (RTL_CRITICAL_SECTION): DebugInfo 0, LockCount 4, RecursionCount 8,
        // OwningThread 12, LockSemaphore 16, SpinCount 20.
        private const int SectionLockCount = 4, SectionRecursion = 8, SectionOwner = 12, SectionSpin = 20;

        /// <summary>Below this an "address" is a CRT lock number, never guest memory.</summary>
        private const long LowestAddress = 0x10000;

        private readonly Dictionary<long, (int Owner, int Recursion)> _hostSections = new();

        /// <summary>Sections currently held, by owner, so a thread that dies holding one releases it.</summary>
        private readonly Dictionary<long, int> _heldSections = new();

        private void InitialiseSection(long section, long spin)
        {
            if (section < LowestAddress)
            {
                _hostSections.Remove(section);
                return;
            }

            WriteWord(section, 0xFFFFFFFF);                 // DebugInfo: none
            WriteWord(section + SectionLockCount, 0xFFFFFFFF);
            WriteWord(section + SectionRecursion, 0);
            WriteWord(section + SectionOwner, 0);
            WriteWord(section + 16, 0);
            WriteWord(section + SectionSpin, (uint)spin);
            _heldSections.Remove(section);
        }

        private (int Owner, int Recursion) ReadSection(long section) =>
            section < LowestAddress
                ? _hostSections.GetValueOrDefault(section)
                : ((int)ReadWord(section + SectionOwner), (int)ReadWord(section + SectionRecursion));

        private void WriteSection(long section, int owner, int recursion)
        {
            if (section < LowestAddress)
            {
                _hostSections[section] = (owner, recursion);
            }
            else
            {
                WriteWord(section + SectionOwner, (uint)owner);
                WriteWord(section + SectionRecursion, (uint)recursion);
                WriteWord(section + SectionLockCount, owner == 0 ? 0xFFFFFFFF : 0xFFFFFFFE);
            }

            if (owner == 0)
            {
                _heldSections.Remove(section);
            }
            else
            {
                _heldSections[section] = owner;
            }
        }

        private bool SectionFree(long section)
        {
            (int owner, int recursion) = ReadSection(section);
            return owner == 0 || recursion <= 0 || _emulator.IsThreadFinished(owner - 0x1000);
        }

        private bool TryEnterSection(long section)
        {
            (int owner, int recursion) = ReadSection(section);
            if (owner == Me && recursion > 0)
            {
                WriteSection(section, Me, recursion + 1);
                return true;
            }

            if (SectionFree(section))
            {
                WriteSection(section, Me, 1);
                return true;
            }

            return false;
        }

        /// <summary>Enters, blocking while another thread holds it. r0 must already be set.</summary>
        private void EnterSection(long section)
        {
            if (TryEnterSection(section))
            {
                return;
            }

            // Separate method: the closures below would otherwise be allocated on every enter,
            // contended or not (C# creates a method's closure class at its top) - a steady few
            // MB/s while a WP8 title loads.
            EnterSectionContended(section);
        }

        private void EnterSectionContended(long section)
        {
            int me = Me;
            bool switched = _emulator.BlockCurrentThread(
                () => SectionFree(section),
                () => WriteSection(section, me, 1),
                why: $"critical section 0x{section:X8} held by thread 0x{ReadSection(section).Owner:X}");
            if (!switched)
            {
                // Nobody else can run, so nobody will leave it: take it rather than hang.
                WriteSection(section, me, 1);
            }
        }

        /// <summary>Leaves once (or entirely), returning the recursion it held. A non-owner's leave changes nothing.</summary>
        private int LeaveSection(long section, bool all)
        {
            (int owner, int recursion) = ReadSection(section);
            if (owner != Me || recursion <= 0)
            {
                return 0;
            }

            int remaining = all ? 0 : recursion - 1;
            WriteSection(section, remaining == 0 ? 0 : owner, remaining);
            return recursion;
        }

        // ---------------------------------------------------------------------------
        // SRW locks
        // ---------------------------------------------------------------------------

        // The SRWLOCK word: 0 free, ExclusiveBit | thread id when held exclusively, otherwise the
        // number of shared holders. Not recursive in either mode, as on Windows.
        private const uint ExclusiveBit = 0x80000000;

        private bool TryAcquireSrw(long lockAddress, bool exclusive)
        {
            uint word = ReadWord(lockAddress);
            if (exclusive)
            {
                if (word != 0 && !SrwOwnerDead(word))
                {
                    return false;
                }

                WriteWord(lockAddress, ExclusiveBit | (uint)Me);
                return true;
            }

            if ((word & ExclusiveBit) != 0 && !SrwOwnerDead(word))
            {
                return false;
            }

            WriteWord(lockAddress, (word & ExclusiveBit) != 0 ? 1 : word + 1);
            return true;
        }

        private bool SrwOwnerDead(uint word) => (word & ExclusiveBit) != 0 && _emulator.IsThreadFinished((int)(word & ~ExclusiveBit) - 0x1000);

        private bool SrwAvailable(long lockAddress, bool exclusive)
        {
            uint word = ReadWord(lockAddress);
            return exclusive ? word == 0 || SrwOwnerDead(word) : (word & ExclusiveBit) == 0 || SrwOwnerDead(word);
        }

        private void AcquireSrw(long lockAddress, bool exclusive)
        {
            if (TryAcquireSrw(lockAddress, exclusive))
            {
                return;
            }

            bool switched = _emulator.BlockCurrentThread(
                () => SrwAvailable(lockAddress, exclusive),
                () => TryAcquireSrw(lockAddress, exclusive),
                why: $"SRW lock 0x{lockAddress:X8} ({(exclusive ? "exclusive" : "shared")})");
            if (!switched)
            {
                // A deadlock (including a thread re-acquiring its own lock, which hangs on Windows):
                // nothing can release it, so carry on holding it.
                WriteWord(lockAddress, exclusive ? ExclusiveBit | (uint)Me : 1);
            }
        }

        private void ReleaseSrw(long lockAddress, bool exclusive)
        {
            uint word = ReadWord(lockAddress);
            if (exclusive)
            {
                if ((word & ExclusiveBit) != 0)
                {
                    WriteWord(lockAddress, 0);
                }
            }
            else if ((word & ExclusiveBit) == 0 && word > 0)
            {
                WriteWord(lockAddress, word - 1);
            }
        }

        // ---------------------------------------------------------------------------
        // condition variables
        // ---------------------------------------------------------------------------

        private sealed class Waiter
        {
            public bool Woken { get; set; }
        }

        private readonly Dictionary<long, List<Waiter>> _conditions = new();

        private void Wake(long condition, bool all)
        {
            if (!_conditions.TryGetValue(condition, out List<Waiter>? waiters))
            {
                return;
            }

            foreach (Waiter waiter in waiters.Where(w => !w.Woken))
            {
                waiter.Woken = true;
                if (!all)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Releases the lock, waits for a wake (or the timeout), takes the lock back. TRUE when
        /// woken; FALSE with ERROR_TIMEOUT when the time ran out. A wait that nothing else could ever
        /// end returns TRUE - a spurious wake-up, which the contract allows and callers re-check for.
        /// </summary>
        private void SleepCondition(long condition, long lockAddress, long milliseconds, bool srw, bool shared)
        {
            if (!_conditions.TryGetValue(condition, out List<Waiter>? waiters))
            {
                waiters = new List<Waiter>();
                _conditions[condition] = waiters;
            }

            var waiter = new Waiter();
            waiters.Add(waiter);

            int heldRecursion = 0;
            if (srw)
            {
                ReleaseSrw(lockAddress, exclusive: !shared);
            }
            else
            {
                heldRecursion = Math.Max(1, LeaveSection(lockAddress, all: true));
            }

            int me = Me;
            long deadline = milliseconds == Infinite ? long.MaxValue : _wallClock.ElapsedMilliseconds + milliseconds;
            bool LockAvailable() => srw ? SrwAvailable(lockAddress, !shared) : SectionFree(lockAddress);
            void Reacquire()
            {
                waiters.Remove(waiter);
                if (srw)
                {
                    if (!TryAcquireSrw(lockAddress, !shared))
                    {
                        WriteWord(lockAddress, shared ? 1 : ExclusiveBit | (uint)me);
                    }
                }
                else
                {
                    WriteSection(lockAddress, me, heldRecursion);
                }
            }

            void Answer(bool woken)
            {
                if (woken)
                {
                    _frame.Return(1);
                }
                else
                {
                    LastError = ErrorTimeout;
                    _frame.Return(0);
                }
            }

            _frame.Return(1);
            bool switched = milliseconds != 0 && _emulator.BlockCurrentThread(
                () => (waiter.Woken || _wallClock.ElapsedMilliseconds >= deadline) && LockAvailable(),
                () =>
                {
                    Reacquire();
                    Answer(waiter.Woken);
                },
                why: $"condition variable 0x{condition:X8}",
                timeout: milliseconds == Infinite ? null : TimeSpan.FromMilliseconds(milliseconds));
            if (switched)
            {
                return;
            }

            Reacquire();
            Answer(waiter.Woken || milliseconds == Infinite);
        }

        // ---------------------------------------------------------------------------
        // one-time initialisation
        // ---------------------------------------------------------------------------

        // The INIT_ONCE word: 0 not started, 1 in progress, (context & ~3) | 2 complete. The two
        // low bits are INIT_ONCE_CTX_RESERVED_BITS, which is why a context must be 4-aligned.
        private const uint OnceInProgress = 1, OnceDone = 2, OnceStateMask = 3;

        /// <summary>
        /// BOOL InitOnceExecuteOnce(INIT_ONCE*, PINIT_ONCE_FN, void* parameter, void** context):
        /// runs fn(initOnce, parameter, &amp;context) once; a FALSE from it leaves the object
        /// uninitialised for the next caller. A caller arriving while another thread runs it
        /// blocks until that thread is done.
        /// </summary>
        private void ExecuteOnce(long initOnce, long function, long parameter, long contextOut)
        {
            uint word = ReadWord(initOnce);
            if ((word & OnceStateMask) == OnceDone)
            {
                if (contextOut != 0)
                {
                    WriteWord(contextOut, word & ~OnceStateMask);
                }

                _frame.Return(1);
                return;
            }

            if ((word & OnceStateMask) == OnceInProgress)
            {
                _frame.Return(0);
                bool switched = _emulator.BlockCurrentThread(
                    () => (ReadWord(initOnce) & OnceStateMask) != OnceInProgress,
                    () => ExecuteOnce(initOnce, function, parameter, contextOut),
                    why: $"InitOnce 0x{initOnce:X8}");
                if (!switched)
                {
                    LastError = ErrorGenFailure;
                }

                return;
            }

            WriteWord(initOnce, OnceInProgress);
            long scratch = _emulator.AllocateHeap(4);
            _emulator.WriteUInt32(scratch, 0);
            long resume = _emulator.ReturnAddress;
            _emulator.CallEmulated(
                "InitOnceExecuteOnce callback",
                function,
                [initOnce, parameter, scratch],
                () =>
                {
                    bool ok = (_emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_R0) & 0xFFFFFFFF) != 0;
                    uint context = _emulator.ReadUInt32(scratch) & ~OnceStateMask;
                    _emulator.FreeHeap(scratch);
                    WriteWord(initOnce, ok ? context | OnceDone : 0);
                    if (ok && contextOut != 0)
                    {
                        WriteWord(contextOut, context);
                    }

                    _frame.Return(ok ? 1 : 0);
                    _emulator.ContinueAt(resume);
                },
                recycle: true);
        }

        private const long OnceCheckOnly = 1, OnceAsync = 2, OnceInitFailed = 4;

        /// <summary>
        /// BOOL InitOnceBeginInitialize(INIT_ONCE*, DWORD flags, BOOL* pending, void** context):
        /// pending TRUE hands the caller the initialisation; FALSE means it is done and context
        /// holds its result. INIT_ONCE_CHECK_ONLY on an object not yet done is ERROR_GEN_FAILURE.
        /// </summary>
        private void BeginInitialize()
        {
            long initOnce = _frame.Arg(0), flags = _frame.Arg(1), pending = _frame.Arg(2), contextOut = _frame.Arg(3);
            if ((flags & ~(OnceCheckOnly | OnceAsync)) != 0 || pending == 0)
            {
                FailBool(ErrorInvalidParameter);
                return;
            }

            Begin();

            void Begin()
            {
                uint word = ReadWord(initOnce);
                if ((word & OnceStateMask) == OnceDone)
                {
                    WriteWord(pending, 0);
                    if (contextOut != 0)
                    {
                        WriteWord(contextOut, word & ~OnceStateMask);
                    }

                    _frame.Return(1);
                    return;
                }

                if ((flags & OnceCheckOnly) != 0)
                {
                    FailBool(ErrorGenFailure);
                    return;
                }

                if ((word & OnceStateMask) == OnceInProgress && (flags & OnceAsync) == 0)
                {
                    _frame.Return(0);
                    if (!_emulator.BlockCurrentThread(() => (ReadWord(initOnce) & OnceStateMask) != OnceInProgress, Begin, why: $"InitOnce 0x{initOnce:X8}"))
                    {
                        LastError = ErrorGenFailure;
                    }

                    return;
                }

                WriteWord(initOnce, OnceInProgress);
                WriteWord(pending, 1);
                _frame.Return(1);
            }
        }

        /// <summary>BOOL InitOnceComplete(INIT_ONCE*, DWORD flags, void* context): done with context, or reset on INIT_ONCE_INIT_FAILED.</summary>
        private void CompleteInitialize()
        {
            long initOnce = _frame.Arg(0), flags = _frame.Arg(1), context = _frame.Arg(2);
            if ((ReadWord(initOnce) & OnceStateMask) != OnceInProgress)
            {
                FailBool(ErrorGenFailure);
                return;
            }

            if ((flags & OnceInitFailed) != 0)
            {
                WriteWord(initOnce, 0);
            }
            else if ((context & OnceStateMask) != 0)
            {
                FailBool(ErrorInvalidParameter);
                return;
            }
            else
            {
                WriteWord(initOnce, (uint)context | OnceDone);
            }

            _frame.Return(1);
        }
    }
}
