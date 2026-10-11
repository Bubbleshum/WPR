namespace WPR.Wp8Native
{
    /// <summary>
    /// The Win32 synchronisation primitives: events, critical sections and SRW locks.
    /// </summary>
    /// <remarks>
    /// There is one CPU here and one thread on it, so a lock can never be contended and
    /// taking one is a no-op that has to succeed. Events are different: they carry state a
    /// producer sets and a consumer reads, so they need to be real even when both are the
    /// same thread.
    /// <para>
    /// What this file is actually for is a specific failure. Every unimplemented import
    /// returns zero, which for <c>CreateEventExW</c> means NULL - a failure - and this image
    /// checks. It threw its own <c>lang::Exception</c> reading "lang::Signal:
    /// CreateEventExW: {0}", caught it three funclets deep, and carried on into a state
    /// where whatever the event was guarding never happened. The title screen then sat on
    /// LOADING for as long as it was given: ten thousand frames, with the game drawing
    /// happily and nothing wrong anywhere in the trace. A stub returning zero is not a
    /// neutral placeholder when zero is the failure value.
    /// </para>
    /// </remarks>
    public sealed partial class SyncLibrary
    {
        /// <summary>CREATE_EVENT_MANUAL_RESET.</summary>
        private const long FlagManualReset = 0x1;

        /// <summary>CREATE_EVENT_INITIAL_SET.</summary>
        private const long FlagInitialSet = 0x2;

        private const long WaitObject0 = 0x0;

        private const long WaitTimeout = 0x102;

        /// <summary>
        /// Handles start here rather than at 1, so a handle is recognisable in a register and
        /// cannot be confused with a small integer the image is carrying around.
        /// </summary>
        private const long HandleBase = 0x0E000000;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Dictionary<long, Event> _events = new();

        /// <summary>ConcRT events, keyed by the object's own address.</summary>
        private readonly Dictionary<long, bool> _concurrencyEvents = new();
        private long _nextHandle = HandleBase;

        public SyncLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        /// <summary>Waits that could not be satisfied, which are the interesting ones.</summary>
        public List<string> Log { get; } = new();

        /// <summary>How many events the image made, and how many are signalled now.</summary>
        public string Summary()
            => $"{_events.Count} event(s), {_events.Values.Count(e => e.Signalled)} signalled, " +
               $"{_satisfied} wait(s) satisfied, {_timedOut} timed out";

        private int _satisfied;
        private int _timedOut;

        private enum Kind
        {
            Event,
            Mutex,
            Semaphore,

            /// <summary>A real handle on a guest thread (DuplicateHandle of GetCurrentThread): signalled once it ends.</summary>
            Thread,
        }

        /// <summary>A waitable handle: an event, a mutex (owner + recursion) or a semaphore (count).</summary>
        private sealed class Event
        {
            public Kind Kind { get; init; }

            public bool ManualReset { get; init; }

            public bool Signalled { get; set; }

            public string Name { get; init; } = string.Empty;

            public int Owner { get; set; }

            public int Recursion { get; set; }

            public long Count { get; set; }

            public long Maximum { get; init; }

            /// <summary>For <see cref="Kind.Thread"/>: the guest thread.</summary>
            public int ThreadId { get; init; }

            /// <summary>A mutex whose owner ended without releasing it: the next wait reports WAIT_ABANDONED.</summary>
            public bool Abandoned { get; set; }
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // Concurrency::event - the ConcRT one, which is not a Win32 event and has its
            // own vocabulary: wait answers 0 when signalled and SIZE_MAX on timeout.
            handlers["??0event@Concurrency@@QAA@XZ"] = () =>
            {
                _concurrencyEvents[_frame.Arg(0)] = false;
                _frame.Return(_frame.Arg(0));
            };

            handlers["??1event@Concurrency@@QAA@XZ"] = () =>
            {
                _concurrencyEvents.Remove(_frame.Arg(0));
                _frame.Return(0);
            };

            handlers["?set@event@Concurrency@@QAAXXZ"] = () =>
            {
                _concurrencyEvents[_frame.Arg(0)] = true;
                _frame.Return(0);
            };

            handlers["?reset@event@Concurrency@@QAAXXZ"] = () =>
            {
                _concurrencyEvents[_frame.Arg(0)] = false;
                _frame.Return(0);
            };

            handlers["?wait@event@Concurrency@@QAAII@Z"] = WaitConcurrencyEvent;

            RegisterWin32(handlers);
        }

        /// <summary>
        /// Waits on a <c>Concurrency::event</c>, running queued work first if it is not set.
        /// </summary>
        /// <remarks>
        /// This used to answer 0 - signalled - immediately and unconditionally, and that is a
        /// worse lie than it sounds. The image's loading handshake is exactly the shape this
        /// breaks: queue a work item, wait on the event the work item will set, then read what
        /// it produced. Answering at once lets the main thread past the wait *before the work
        /// item has run at all*, so it reads a result nobody has written yet and concludes
        /// there is nothing to load.
        /// <para>
        /// The measurement that found it: both queued callbacks returned S_OK having made ten
        /// and eleven import calls, three of which were <c>event::set</c>. Ten calls is not a
        /// loader running, it is task plumbing signalling completion - and the main thread had
        /// already gone past.
        /// </para>
        /// <para>
        /// A wait is the best yield point an image can offer, so an unset event now drains the
        /// whole queue before answering. Draining rather than yielding once is deliberate: a
        /// blocked thread is blocked until someone sets the event, and the only candidate for
        /// "someone" here is the work already queued.
        /// </para>
        /// </remarks>
        private void WaitConcurrencyEvent()
        {
            long handle = _frame.Arg(0);
            bool signalled = _concurrencyEvents.GetValueOrDefault(handle);

            // An HTTP request whose failure is not due yet: its OnError will set this event from
            // the main loop. A background thread blocks until then, as it would on a device;
            // answering "signalled" now would tell it the request finished. The main thread
            // cannot block for it - it runs the loop that delivers it - so it gets the failure
            // at once instead, through the drain below.
            if (!signalled && _emulator.PendingDeferredCalls == 0 && _emulator.Stubs.Http.HasPending)
            {
                if (_emulator.CurrentThreadId != 1)
                {
                    _frame.Return(0);
                    if (_emulator.BlockCurrentThread(
                            () => _concurrencyEvents.GetValueOrDefault(handle),
                            why: $"concurrency event 0x{handle:X8} (HTTP request in flight)"))
                    {
                        _concurrencyWaits.Add($"event 0x{handle:X8} wait -> blocked until an HTTP request fails");
                        return;
                    }
                }

                _emulator.Stubs.Http.QueueAll();
            }

            if (signalled || _emulator.PendingDeferredCalls == 0)
            {
                _concurrencyWaits.Add(
                    $"event 0x{_frame.Arg(0):X8} wait -> {(signalled ? "already set" : "nothing queued")}");
                _frame.Return(0);
                return;
            }

            long resume = _emulator.ReturnAddress;
            int queued = _emulator.PendingDeferredCalls;
            _concurrencyWaits.Add($"event 0x{_frame.Arg(0):X8} wait -> draining {queued} queued callback(s)");

            _emulator.DrainDeferredCalls(() =>
            {
                _frame.Return(0);
                _emulator.ContinueAt(resume);
            });
        }

        /// <summary>What happened at each ConcRT wait, capped.</summary>
        private readonly List<string> _concurrencyWaits = new();

        /// <summary>ConcRT waits, for the report.</summary>
        public IReadOnlyList<string> ConcurrencyWaits => _concurrencyWaits;
    }
}
