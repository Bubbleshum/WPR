using System;
using System.Threading;
using WPR.Engine.Sensors;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;

namespace Microsoft.Devices.Sensors
{
    /// <summary>
    /// Shim for <c>Microsoft.Devices.Sensors.Accelerometer</c>.
    ///
    /// <para><b>Platform-free by construction.</b> Every reading comes from the
    /// <see cref="IAccelerometerProvider"/> the launcher registered into <see cref="SensorBackend"/> —
    /// real hardware on Android, the keyboard emulator on Windows. This class owns only the WP7
    /// contract: the event shape, the polled <see cref="CurrentValue"/>, and the start/stop
    /// bookkeeping games expect. Before 2026-08-30 both platform paths lived here behind
    /// <c>#if __ANDROID__</c>, which meant the desktop emulator shipped inside the Android APK
    /// and the Android head could not be changed without touching a shared framework assembly.</para>
    /// </summary>
    public class Accelerometer : SensorBase<AccelerometerReading>
    {
        private bool _Started = false;

        /// <summary>
        /// True if the running platform exposes an accelerometer. Both heads report true —
        /// Android has the hardware sensor, and on desktop the keyboard emulator stands in —
        /// so games that guard on <c>Accelerometer.IsSupported</c> wire up their sensor path.
        /// With no provider registered at all there is nothing to read, so it reports false.
        /// </summary>
        public static bool IsSupported => SensorBackend.Accelerometer?.IsSupported ?? false;

        public Accelerometer()
        {
            State = SensorState.Ready;
        }

        ~Accelerometer()
        {
            Stop();
        }

        /// <summary>
        /// Releasing the accelerometer just means stopping it — there is no unmanaged handle on
        /// either platform. <see cref="Stop"/> is already idempotent via <c>_Started</c>, so a
        /// double dispose is harmless.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            Stop();
            base.Dispose(disposing);
        }

        public event EventHandler<AccelerometerReadingEventArgs>? ReadingChanged;

        public SensorState State { get; private set; }

        // CurrentValue / IsDataValid / TimeBetweenUpdates used to be declared here. They are on
        // SensorBase<T> now, because that is where WP7 declares them and therefore what game IL
        // binds — see the note on that type. Do not move them back down.

        /// <summary>
        /// The provider this instance actually started against, held so <see cref="Stop"/>
        /// releases the same one. The provider counts its readers to decide when to power the
        /// sensor down, so every <see cref="Start"/> must be matched exactly once
        /// against the same object — resolving <see cref="SensorBackend.Accelerometer"/> again at
        /// stop time would break that if none was registered when the game called
        /// <see cref="Start"/>, or if the registration were ever replaced mid-session.
        /// </summary>
        private IAccelerometerProvider? _startedOn;

        public override void Start()
        {
            if (_Started)
            {
                return;
            }

            IAccelerometerProvider? provider = SensorBackend.Accelerometer;
            if (provider == null)
            {
                // No platform registered: there is nothing to start, so stay stopped and let
                // Stop() be a no-op. Degrading beats throwing — a game that guarded on
                // IsSupported never gets here, and one that didn't just sees a flat sensor.
                return;
            }

            _Started = true;
            _startedOn = provider;
            provider.ReadingChanged += OnProviderReading;
            provider.Start();
        }

        public override void Stop()
        {
            if (!_Started)
            {
                return;
            }

            _Started = false;
            IAccelerometerProvider? provider = _startedOn;
            _startedOn = null;

            if (provider != null)
            {
                provider.ReadingChanged -= OnProviderReading;
                provider.Stop();
            }
        }

        /// <summary>
        /// One sample from the platform, fanned out to every route a WP7 game can read it by.
        ///
        /// <para>All three routes matter and they used to be split: the desktop path published
        /// <see cref="CurrentValue"/>/<see cref="IsDataValid"/> and the Android path did not, so
        /// a game that polled <c>CurrentValue</c> each frame instead of subscribing read a
        /// permanently-zero sensor on Android. Unifying the two paths fixed that.</para>
        /// </summary>
        private void OnProviderReading(System.Numerics.Vector3 acceleration)
        {
            var reading = new AccelerometerReading
            {
                Acceleration = new XnaVector3(acceleration.X, acceleration.Y, acceleration.Z),
                Timestamp = DateTimeOffset.Now,
            };

            // Publish before raising, so a handler that reads CurrentValue sees this sample and
            // not the previous one. The single write also sets IsDataValid.
            SetCurrentValue(reading);

            ReadingChanged?.Invoke(this, new AccelerometerReadingEventArgs(
                acceleration.X, acceleration.Y, acceleration.Z, reading.Timestamp));

            OnCurrentValueChanged(new SensorReadingEventArgs<AccelerometerReading>
            {
                SensorReading = reading,
            });
        }
    }
}
