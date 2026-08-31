using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using WPR.Abstractions.Sensors;

namespace WPR.Platform.Windows.Input
{
    /// <summary>
    /// This head's <see cref="ISensorProvider"/>: a desktop PC has no accelerometer, so the
    /// readings come from <see cref="KeyboardAccelerometerHost"/>, the keyboard emulator the
    /// launcher drives from the user's configured tilt keys.
    ///
    /// <para>Registered into <c>WPR.Sensors.SensorBackend</c> by <see cref="ServicesSetup"/>.
    /// It is the only thing in this head the WP7 sensors framework can see — everything else
    /// on <see cref="KeyboardAccelerometerHost"/> (key notification, sensitivity, orientation,
    /// the screen-relative reading the overlays draw) is Windows talking to Windows and stays
    /// off the seam.</para>
    ///
    /// <para>Stateless apart from the reader count and event bridging, so the launcher can
    /// register one instance for the whole process. Every game's <c>Accelerometer</c>
    /// subscribes to the same one.</para>
    /// </summary>
    internal sealed class WindowsSensorProvider : ISensorProvider
    {
        /// <summary>
        /// Guards <see cref="_consumers"/> and the <see cref="KeyboardAccelerometerHost.ReadingTick"/>
        /// subscription. Deliberately never held across a call that takes the emulator's own lock
        /// (<see cref="KeyboardAccelerometerHost.Acquire"/>/<see cref="KeyboardAccelerometerHost.Release"/>/
        /// <see cref="KeyboardAccelerometerHost.ResetForNewLaunch"/>) — those happen outside it, so
        /// there is exactly one lock ordering in the system and no cycle to deadlock on.
        /// </summary>
        private readonly object _gate = new object();

        /// <summary>
        /// How many <c>Accelerometer</c> shims are currently started against this provider.
        /// Counted rather than inferred from <see cref="AccelerometerChanged"/>'s invocation
        /// list, for the reasons the contract spells out — the shim unsubscribes before calling
        /// <see cref="StopAccelerometer"/>, so the list is not a synchronisable source of truth.
        ///
        /// <para>The 60Hz tick itself is separately refcounted inside
        /// <see cref="KeyboardAccelerometerHost"/>, and that count includes holders which are not
        /// games — <see cref="TiltOverlay"/> keeps it running so the Controls-page preview
        /// responds with no game in sight. This count is the narrower question of whether
        /// anything is still *reading*, which is what decides the fan-out subscription.</para>
        /// </summary>
        private int _consumers;

        /// <summary>Samples delivered since the first reader started; drives the sampled trace.</summary>
        private int _tickCount;

        /// <summary>
        /// True even though there is no hardware sensor: the emulator stands in for one, and WP7
        /// titles that guard on <c>Accelerometer.IsSupported</c> must take their sensor path or
        /// tilt-controlled games are unplayable here. Reporting false would be more literal and
        /// less useful.
        /// </summary>
        public bool IsAccelerometerSupported => true;

        public Vector3 CurrentAcceleration => KeyboardAccelerometerHost.CurrentAcceleration;

        /// <summary>
        /// Bridged onto the emulator's tick rather than exposed directly, so the refcounted
        /// <see cref="KeyboardAccelerometerHost.Acquire"/>/<see cref="KeyboardAccelerometerHost.Release"/>
        /// pairing stays owned by <see cref="StartAccelerometer"/>/<see cref="StopAccelerometer"/>.
        /// </summary>
        public event Action<Vector3>? AccelerometerChanged;

        /// <summary>
        /// <para><b>The subscribe is idempotent on purpose.</b> Every <c>Accelerometer</c> in a
        /// game shares this one provider, so a title holding two of them would otherwise attach
        /// <see cref="OnReadingTick"/> twice and deliver every sample twice to both. That could
        /// not happen before the split, when each <c>Accelerometer</c> subscribed its own
        /// instance method directly to the emulator. Fan-out now happens once, through
        /// <see cref="AccelerometerChanged"/>.</para>
        /// </summary>
        public void StartAccelerometer()
        {
            int consumers;
            lock (_gate)
            {
                KeyboardAccelerometerHost.ReadingTick -= OnReadingTick;
                KeyboardAccelerometerHost.ReadingTick += OnReadingTick;
                consumers = ++_consumers;
                if (consumers == 1)
                {
                    _tickCount = 0;
                }
            }

            KeyboardAccelerometerHost.Acquire();
            Trace.WriteLine($"[wpr-accel] start (keyboard emulator) — readers={consumers}");
        }

        /// <summary>
        /// Releases this consumer's hold on the 60Hz tick, and detaches the fan-out once the
        /// last reader goes — after which the emulator stops sampling entirely unless an overlay
        /// is holding it, and even then spends no cycles on a reading nobody wants. Guarded
        /// against an unbalanced call so a stray stop cannot drive the count negative and leave
        /// a live reader detached.
        /// </summary>
        public void StopAccelerometer()
        {
            KeyboardAccelerometerHost.Release();

            bool detached;
            int consumers;
            lock (_gate)
            {
                detached = _consumers > 0 && --_consumers == 0;
                consumers = _consumers;
                if (detached)
                {
                    KeyboardAccelerometerHost.ReadingTick -= OnReadingTick;
                }
            }

            Trace.WriteLine(
                $"[wpr-accel] stop — readers={consumers}, ticks={Volatile.Read(ref _tickCount)}"
                + (detached ? ", detached (nothing reading)" : string.Empty));
        }

        /// <summary>
        /// Drops this provider's subscribers and every scrap of emulator state — the tick
        /// timer, the refcount and any key still held down — whatever the consumer count says.
        /// A game that exited without stopping its sensor is exactly the case this exists for;
        /// see the contract's remarks for the ALC leak it prevents.
        /// </summary>
        public void ResetForNewLaunch()
        {
            lock (_gate)
            {
                AccelerometerChanged = null;
                _consumers = 0;
                _tickCount = 0;
                KeyboardAccelerometerHost.ReadingTick -= OnReadingTick;
            }

            // Outside the lock: this takes the emulator's own lock, and holding both would be
            // the only place in this file where two locks are nested.
            KeyboardAccelerometerHost.ResetForNewLaunch();
        }

        /// <summary>
        /// Fans one emulator sample out to every started <c>Accelerometer</c>, and drops a
        /// sampled line into the per-game <c>wpr_game_debug.log</c> — roughly one every two
        /// seconds at 60Hz. That trace is the first thing to look at when a user reports tilt
        /// not responding: it says whether readings are flowing at all, which orientation the
        /// intent is being rotated into, and how many readers are attached.
        /// </summary>
        private void OnReadingTick(object? sender, Vector3 acceleration)
        {
            // Drop anything that arrives once nothing is reading. The emulator raises its tick
            // OUTSIDE its own lock — deliberately, so a slow game handler can neither block the
            // 60Hz timer nor deadlock against it — so a tick already past that lock can still
            // land after the last Stop() detached us, or during ResetForNewLaunch. This makes
            // the post-Stop case exact and narrows the teardown one; it cannot be closed
            // entirely without invoking game code under a lock, which is the trade the emulator
            // deliberately refuses. Harmless either way: the subscriber list is already empty,
            // so no reference survives to pin the game's ALC, which is what the reset is for.
            if (Volatile.Read(ref _consumers) == 0)
            {
                return;
            }

            int n = Interlocked.Increment(ref _tickCount);
            if (n == 1 || n % 120 == 0)
            {
                Trace.WriteLine(
                    $"[wpr-accel] tick #{n} reading=({acceleration.X:F2},{acceleration.Y:F2},{acceleration.Z:F2}) " +
                    $"orient={KeyboardAccelerometerHost.Orientation} readers={Volatile.Read(ref _consumers)}");
            }

            AccelerometerChanged?.Invoke(acceleration);
        }
    }
}
