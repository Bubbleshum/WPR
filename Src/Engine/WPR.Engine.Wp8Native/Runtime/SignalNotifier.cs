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
        }

        /// <summary>Calls made on notifiers and how they fired, for the run summary.</summary>
        public List<string> SignalNotifierLog { get; } = new();

        private void SignalNote(string line)
        {
            if (SignalNotifierLog.Count < 50)
            {
                SignalNotifierLog.Add(line);
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
            KeepAlive(handler);
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
                        SignalNote($"{state.Name}: terminated");
                        Return(HResultOk);
                    }),
                });

            SignalNote($"attached to {(semaphore ? "semaphore" : "event")} '{name}'" +
                       (timed ? $" with a {timeout100ns / 10_000} ms timeout" : ""));
            _emulator.WriteUInt32(outNotifier, (uint)state.Self);
            Return(HResultOk);
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
            long gate = _emulator.RegisterVtableMethod($"SignalNotifier '{state.Name}' (pool thread)", () => SignalNotifierTurn(state));
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
            if (!_emulator.EndCurrentGuestThread($"signal notifier '{state.Name}' idle"))
            {
                _emulator.Stop($"the signal notifier '{state.Name}' thread ended and no thread is runnable");
            }
        }
    }
}
