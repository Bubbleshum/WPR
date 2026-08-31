using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using WPR.Abstractions.Sensors;

namespace WPR.Platform.Android.Input
{
    /// <summary>
    /// This head's <see cref="ISensorProvider"/>: the device's real accelerometer, read through
    /// Xamarin.Essentials.
    ///
    /// <para>Registered into <c>WPR.Sensors.SensorBackend</c> by <see cref="ServicesSetup"/>,
    /// which runs in the launcher process and again in <c>GameActivity</c>'s <c>:game</c>
    /// process — the game process needs its own registration because nothing static is shared
    /// across that boundary.</para>
    ///
    /// <para>Before 2026-08-30 this code lived inside <c>Microsoft.Devices.Sensors.Accelerometer</c>
    /// behind <c>#if __MOBILE__ || __ANDROID__</c>, which is why the Xamarin.Essentials package
    /// reference sat on a shared framework project and resolved for the desktop leg as well.</para>
    /// </summary>
    internal sealed class AndroidSensorProvider : ISensorProvider
    {
        /// <summary>
        /// Guards <see cref="_consumers"/> and the Essentials subscription. The hardware
        /// transitions themselves happen <em>outside</em> it: those are JNI calls into
        /// <c>SensorManager</c>, and <see cref="StopAccelerometer"/> is reachable from a GC
        /// finalizer (a game that drops its <c>Accelerometer</c> without stopping it), so the
        /// lock is kept off the blocking call.
        /// </summary>
        private readonly object _gate = new object();

        /// <summary>
        /// How many <c>Accelerometer</c> shims are currently started against this provider.
        /// The hardware runs while this is above zero and stops when it returns to zero — a
        /// phone sensor left sampling is a battery drain, and WP7 titles start and stop theirs
        /// per screen precisely so it can be released.
        ///
        /// <para>Counted rather than inferred from <see cref="AccelerometerChanged"/>'s
        /// invocation list: the shim unsubscribes and then calls
        /// <see cref="StopAccelerometer"/>, so reading the list would work only while that
        /// ordering holds, and it could not be made atomic with the stop decision from in here.
        /// <see cref="StartAccelerometer"/>/<see cref="StopAccelerometer"/> are the paired
        /// lifecycle calls, so they are what gets counted.</para>
        /// </summary>
        private int _consumers;

        /// <summary>Samples delivered since the first reader started; drives the sampled trace.</summary>
        private int _tickCount;

        /// <summary>
        /// Every Windows Phone device shipped an accelerometer and every Android device WPR
        /// targets has one, so this reports true without probing. A probe would be
        /// <c>SensorManager.GetDefaultSensor(SensorType.Accelerometer) != null</c>, which is
        /// worth adding the day a sensorless device shows up.
        /// </summary>
        public bool IsAccelerometerSupported => true;

        /// <summary>
        /// Last sample seen, in the WP7 device frame. Cached from the event because
        /// Xamarin.Essentials exposes no poll — it is push-only. Deliberately not cleared on
        /// stop, so a reader that starts again gets the last known attitude rather than a zero
        /// vector while the sensor spins back up.
        /// </summary>
        public Vector3 CurrentAcceleration { get; private set; }

        public event Action<Vector3>? AccelerometerChanged;

        /// <summary>
        /// <para><b>The subscribe is idempotent on purpose.</b> Every <c>Accelerometer</c> in a
        /// game shares this one provider, so a title holding two of them would otherwise attach
        /// <see cref="OnEssentialsReadingChanged"/> twice and deliver every sample twice to both.
        /// That could not happen before the split, when each <c>Accelerometer</c> subscribed its
        /// own instance method directly to Xamarin.Essentials. Fan-out now happens once, through
        /// <see cref="AccelerometerChanged"/>.</para>
        /// </summary>
        public void StartAccelerometer()
        {
            bool powerUp;
            int consumers;
            lock (_gate)
            {
                Xamarin.Essentials.Accelerometer.ReadingChanged -= OnEssentialsReadingChanged;
                Xamarin.Essentials.Accelerometer.ReadingChanged += OnEssentialsReadingChanged;
                consumers = ++_consumers;
                // Exactly one caller can observe the 0 -> 1 transition, so exactly one issues
                // the start even if two threads race here. Essentials throws if asked to start
                // while already monitoring, which is what that guarantee is protecting.
                powerUp = consumers == 1;
                if (powerUp)
                {
                    _tickCount = 0;
                }
            }

            if (powerUp)
            {
                try
                {
                    if (!Xamarin.Essentials.Accelerometer.IsMonitoring)
                    {
                        Xamarin.Essentials.Accelerometer.Start(Xamarin.Essentials.SensorSpeed.Game);
                    }
                }
                catch (Exception ex)
                {
                    // A sensor that refuses to start must not take the game down with it; the
                    // reader simply sees no samples, the same as an unregistered provider.
                    Trace.WriteLine($"[wpr-accel] hardware start failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Trace.WriteLine($"[wpr-accel] start (hardware) — readers={consumers}"
                + (powerUp ? ", sensor powered up" : string.Empty));
        }

        /// <summary>
        /// Releases one consumer's hold, and powers the sensor down once the last one goes.
        /// Guarded against an unbalanced call so a stray stop can never drive the count
        /// negative and wedge the sensor off.
        /// </summary>
        public void StopAccelerometer()
        {
            bool powerDown;
            int consumers;
            lock (_gate)
            {
                powerDown = _consumers > 0 && --_consumers == 0;
                consumers = _consumers;
                if (powerDown)
                {
                    Xamarin.Essentials.Accelerometer.ReadingChanged -= OnEssentialsReadingChanged;
                }
            }

            if (powerDown)
            {
                StopHardware();
            }

            Trace.WriteLine(
                $"[wpr-accel] stop — readers={consumers}, ticks={Volatile.Read(ref _tickCount)}"
                + (powerDown ? ", sensor powered down" : string.Empty));
        }

        /// <summary>
        /// Drops this provider's subscribers and stops the hardware sensor unconditionally,
        /// whatever the consumer count says — a game that exited without stopping its sensor is
        /// exactly the case this exists for. See the contract's remarks for the ALC leak it
        /// prevents; it matters less here than on the desktop head, since a finished game takes
        /// its whole process with it, but the sensor must still stop if the process outlives
        /// the run.
        /// </summary>
        public void ResetForNewLaunch()
        {
            lock (_gate)
            {
                AccelerometerChanged = null;
                _consumers = 0;
                _tickCount = 0;
                Xamarin.Essentials.Accelerometer.ReadingChanged -= OnEssentialsReadingChanged;
            }

            StopHardware();
        }

        /// <summary>Powers the sensor down. Never called with <see cref="_gate"/> held.</summary>
        private void StopHardware()
        {
            try
            {
                if (Xamarin.Essentials.Accelerometer.IsMonitoring)
                {
                    Xamarin.Essentials.Accelerometer.Stop();
                }
            }
            catch (Exception ex)
            {
                // Best-effort: neither a screen change nor teardown may fail because a sensor
                // refused to stop.
                Trace.WriteLine($"[wpr-accel] hardware stop failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Fans one hardware sample out to every started <c>Accelerometer</c>, and drops a
        /// sampled line into the per-game <c>wpr_game_debug.log</c> — roughly one every two
        /// seconds at the Game sampling speed. The desktop head has had this trace for a long
        /// time and it is the first thing to look at when tilt is reported as unresponsive;
        /// Android never had an equivalent until the providers split.
        /// </summary>
        private void OnEssentialsReadingChanged(
            object? sender, Xamarin.Essentials.AccelerometerChangedEventArgs args)
        {
            // Rotate a quarter turn: Android reports the opposite axis direction to the Windows
            // Phone default that games are written against.
            var acceleration = new Vector3(
                -args.Reading.Acceleration.X,
                -args.Reading.Acceleration.Y,
                -args.Reading.Acceleration.Z);

            CurrentAcceleration = acceleration;

            // Drop anything that arrives once nothing is reading. Essentials raises readings
            // from its own thread, so a callback already in flight can land after the last
            // StopAccelerometer unsubscribed us, or during ResetForNewLaunch. Same guard, and
            // the same reasoning, as the Windows provider.
            if (Volatile.Read(ref _consumers) == 0)
            {
                return;
            }

            int n = Interlocked.Increment(ref _tickCount);
            if (n == 1 || n % 120 == 0)
            {
                Trace.WriteLine(
                    $"[wpr-accel] tick #{n} reading=({acceleration.X:F2},{acceleration.Y:F2},{acceleration.Z:F2}) " +
                    $"readers={Volatile.Read(ref _consumers)}");
            }

            AccelerometerChanged?.Invoke(acceleration);
        }
    }
}
