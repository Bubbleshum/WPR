using System.Diagnostics;

namespace WPR.Wp8Native
{
    /// <summary>
    /// <c>Windows.System.Threading.Core.SignalNotifier</c>: a pool thread that waits on a named event
    /// or semaphore and calls a <c>SignalHandler</c> when it is signalled (or a timeout passes).
    /// </summary>
    /// <remarks>
    /// <para>FMOD's WP8 build drives its mixer with one (AttachToEvent with a timeout, AttachToSemaphore).
    /// The stand-in answered a placeholder notifier whose Enable did nothing, so its mixer never
    /// ran and the first call through what it should have set up was a null call
    /// (Where's My Perry, Star Wars: Tiny Death Star).</para>
    /// <para>As on Windows a notifier fires once per <c>Enable</c>; a handler that wants more calls
    /// Enable again, usually from inside the handler. <c>Terminate</c> stops it for good. The named
    /// object is opened, or created if nobody has made it yet, so either side may come first.</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        /// <summary>Statics: AttachToEvent, AttachToEvent+timeout, AttachToSemaphore, AttachToSemaphore+timeout.</summary>
        private void RegisterSignalNotifier()
        {
            _factories["Windows.System.Threading.Core.SignalNotifier"] = CreateDiscoveryObject(
                "ISignalNotifierStatics",
                slotCount: 4,
                known: new Dictionary<int, (string, Action)>
                {
                    // (name, handler, out notifier)
                    [InspectableSlots + 0] = ("AttachToEvent", () => AttachSignalNotifier(semaphore: false, timed: false)),
                    // (name, handler, TimeSpan, out notifier): the 64-bit TimeSpan cannot start in r3,
                    // so it is the first two stack words and the out pointer the third.
                    [InspectableSlots + 1] = ("AttachToEventWithTimeout", () => AttachSignalNotifier(semaphore: false, timed: true)),
                    [InspectableSlots + 2] = ("AttachToSemaphore", () => AttachSignalNotifier(semaphore: true, timed: false)),
                    [InspectableSlots + 3] = ("AttachToSemaphoreWithTimeout", () => AttachSignalNotifier(semaphore: true, timed: true)),
                });
        }

        private sealed class SignalNotifierState
        {
            public required string Name { get; init; }

            public long Handle { get; init; }

            public long Handler { get; init; }

            /// <summary>Stopwatch ticks to wait before a timed-out call; 0 for none.</summary>
            public long TimeoutTicks { get; init; }

            public long Self { get; set; }

            public bool Enabled { get; set; }

            public bool Terminated { get; set; }

            /// <summary>A pool thread is serving this notifier (waiting, or inside the handler).</summary>
            public bool Serving { get; set; }

            public long Deadline { get; set; }

            /// <summary>Times the handler was called, for the run summary.</summary>
            public int Fires { get; set; }

            /// <summary>The pool thread serving it while <see cref="Serving"/>.</summary>
            public SignalPoolThread? Worker { get; set; }
        }

        /// <summary>
        /// A guest thread of the notifiers' thread pool. Between notifiers it parks, idle, until
        /// <see cref="Next"/> is given one.
        /// </summary>
        private sealed class SignalPoolThread
        {
            public SignalNotifierState? Next { get; set; }
        }

        /// <summary>Pool threads parked with nothing to serve.</summary>
        private readonly List<SignalPoolThread> _idleSignalThreads = new();

        /// <summary>
        /// How many idle pool threads are kept. FMOD keeps two or three notifiers in flight; a
        /// thread past this many ends, as before the pool existed.
        /// </summary>
        private const int MaxIdleSignalThreads = 8;

        /// <summary>Calls made on notifiers and how they fired, for the run summary.</summary>
        public List<string> SignalNotifierLog { get; } = new();

        private readonly Stopwatch _signalClock = Stopwatch.StartNew();

        private void SignalNote(string line)
        {
            if (SignalNotifierLog.Count < 200)
            {
                SignalNotifierLog.Add($"{_signalClock.ElapsedMilliseconds,6} ms {line}");
            }
        }

        private void AttachSignalNotifier(bool semaphore, bool timed)
        {
            string name = _strings.ReadText(Arg(1));
            long handler = Arg(2);
            long timeout100ns = timed ? (Arg(4) | (Arg(5) << 32)) : 0;
            long outNotifier = timed ? Arg(6) : Arg(3);
            if (handler == 0 || outNotifier == 0)
            {
                Return(unchecked((int)0x80070057));   // E_INVALIDARG
                return;
            }

            long handle = _emulator.Sync.OpenOrCreateNamed(name, semaphore);
            var state = new SignalNotifierState
            {
                Name = name,
                Handle = handle,
                Handler = handler,
                TimeoutTicks = timed ? Math.Max(1, timeout100ns * Stopwatch.Frequency / 10_000_000) : 0,
            };

            state.Self = CreateDiscoveryObject(
                "ISignalNotifier",
                slotCount: 2,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("Enable", () => { EnableSignalNotifier(state); Return(HResultOk); }),
                    [InspectableSlots + 1] = ("Terminate", () =>
                    {
                        state.Terminated = true;
                        SignalNote($"{state.Name} (0x{state.Self:X8}): terminated{(state.Serving ? " while its thread is serving" : "")}");
                        Return(HResultOk);
                    }),
                });

            SignalNote($"attached to {(semaphore ? "semaphore" : "event")} '{name}' handler 0x{handler:X8} notifier 0x{state.Self:X8}" +
                       (timed ? $" with a {timeout100ns / 10_000} ms timeout" : ""));
            _emulator.WriteUInt32(outNotifier, (uint)state.Self);

            // The notifier holds its handler, as on Windows. KeepAlive runs the guest's AddRef and
            // returns S_OK itself, so it must come last: taken first, the Return below put 0 in r0
            // and AddRef ran on the zero page instead. A caller that passes a temporary delegate
            // (FMOD's non-blocking loader thread, Where's My Perry's settings screen) then freed it,
            // and the next signal called through freed memory (null call from the Invoke trap).
            if (!KeepAlive(handler))
            {
                Return(HResultOk);
            }
        }

        private void EnableSignalNotifier(SignalNotifierState state)
        {
            if (state.Terminated)
            {
                return;
            }

            state.Enabled = true;
            state.Deadline = state.TimeoutTicks == 0 ? 0 : Stopwatch.GetTimestamp() + state.TimeoutTicks;
            if (state.Serving)
            {
                // The serving thread loops on (usually: the handler re-enabled from inside itself).
                return;
            }

            state.Serving = true;

            // A parked pool thread takes it if there is one, as the WinRT thread pool reuses its
            // threads. FMOD's WP8 threads (Where's My Perry, Tiny Death Star) attach a NEW notifier
            // from inside every handler call, many times a second; a new guest thread each time
            // left a 1 MB stack behind per call (200 MB of guest heap every 5 s) and a scheduler
            // list hundreds of finished threads long.
            if (_idleSignalThreads.Count > 0)
            {
                SignalPoolThread idle = _idleSignalThreads[^1];
                _idleSignalThreads.RemoveAt(_idleSignalThreads.Count - 1);
                state.Worker = idle;
                idle.Next = state;
                return;
            }

            var worker = new SignalPoolThread();
            state.Worker = worker;
            long gate = _emulator.RegisterVtableMethod("SignalNotifier pool thread", () => SignalNotifierTurn(state));
            _emulator.CreateGuestThread($"signal notifier {state.Name}", ArmEmulator.ThumbEntry(gate), 0);
        }

        /// <summary>
        /// One turn of a notifier's pool thread: park until the object is signalled, the timeout
        /// passes or the notifier is terminated, then call the handler; loop while it re-enables.
        /// </summary>
        private void SignalNotifierTurn(SignalNotifierState state)
        {
            if (state.Terminated || !state.Enabled)
            {
                EndSignalNotifierThread(state);
                return;
            }

            bool Ready() =>
                state.Terminated ||
                _emulator.Sync.CouldWait(state.Handle) == true ||
                (state.Deadline != 0 && Stopwatch.GetTimestamp() >= state.Deadline);

            if (!Ready())
            {
                TimeSpan? remaining = state.Deadline == 0 ? null
                    : TimeSpan.FromSeconds(Math.Max(0, state.Deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                if (_emulator.BlockCurrentThread(Ready, onResume: () => FireSignalNotifier(state),
                        why: $"signal notifier '{state.Name}'", timeout: remaining))
                {
                    return;
                }
            }

            FireSignalNotifier(state);
        }

        private void FireSignalNotifier(SignalNotifierState state)
        {
            if (state.Terminated)
            {
                EndSignalNotifierThread(state);
                return;
            }

            bool signalled = _emulator.Sync.TryWait(state.Handle);
            state.Enabled = false;
            state.Fires++;
            long invoke = _emulator.ReadUInt32(_emulator.ReadUInt32(state.Handler) + (SlotHandlerInvoke * 4));

            // SignalHandler::Invoke(this, SignalNotifier^ notifier, bool timedOut)
            _emulator.CallEmulated($"SignalHandler::Invoke ({state.Name})", invoke, [state.Handler, state.Self, signalled ? 0 : 1], () =>
            {
                if (state.Enabled && !state.Terminated)
                {
                    SignalNotifierTurn(state);
                    return;
                }

                EndSignalNotifierThread(state);
            }, recycle: true);
        }

        private void EndSignalNotifierThread(SignalNotifierState state)
        {
            state.Serving = false;
            SignalNote($"{state.Name} (0x{state.Self:X8}): done after {state.Fires} call(s){(state.Terminated ? ", terminated" : "")}");

            // Park this thread for the next notifier rather than end it.
            if (state.Worker is { } worker && _idleSignalThreads.Count < MaxIdleSignalThreads)
            {
                state.Worker = null;
                worker.Next = null;
                _idleSignalThreads.Add(worker);
                if (_emulator.BlockCurrentThread(() => worker.Next is not null, onResume: () => ServePoolThread(worker),
                        why: "signal notifier pool thread, idle"))
                {
                    return;
                }

                // Nothing else could run: give the thread up as before.
                _idleSignalThreads.Remove(worker);
                if (worker.Next is not null)
                {
                    ServePoolThread(worker);
                    return;
                }
            }

            state.Worker = null;
            if (!_emulator.EndCurrentGuestThread($"signal notifier '{state.Name}' idle"))
            {
                _emulator.Stop($"the signal notifier '{state.Name}' thread ended and no thread is runnable");
            }
        }

        /// <summary>A parked pool thread was handed a notifier: serve it on this thread.</summary>
        private void ServePoolThread(SignalPoolThread worker)
        {
            SignalNotifierState state = worker.Next!;
            worker.Next = null;
            SignalNotifierTurn(state);
        }
    }
}
