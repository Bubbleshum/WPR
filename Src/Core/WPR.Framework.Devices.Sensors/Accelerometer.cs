using System;
using System.Diagnostics;

namespace Microsoft.Devices.Sensors
{
    public class Accelerometer : SensorBase<AccelerometerReading>
    {
        private bool _Started = false;
        private static int _ReadingTickCount;

        /// <summary>
        /// True if the running platform exposes an accelerometer sensor. On mobile we hook
        /// the real hardware sensor via Xamarin.Essentials. On desktop we expose a keyboard
        /// simulator (<see cref="KeyboardAccelerometerHost"/>) and report true so games
        /// that guard on <c>Accelerometer.IsSupported</c> will wire up their sensor path.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
#if __MOBILE__
                return true;
#else
                return true;
#endif
            }
        }

        public Accelerometer()
        {
            State = SensorState.Ready;
        }

#if __MOBILE__ || __ANDROID__
        private void OnImplReadingChanged(object ?sender, Xamarin.Essentials.AccelerometerChangedEventArgs args)
        {
            // We always rotate it to the right... Which seems to be the opposite to Windows Phone default reported axis direction
            ReadingChanged?.Invoke(this, new AccelerometerReadingEventArgs(-args.Reading.Acceleration.X,
                -args.Reading.Acceleration.Y, -args.Reading.Acceleration.Z, DateTimeOffset.Now));

            OnCurrentValueChanged(new SensorReadingEventArgs<AccelerometerReading>()
            {
                SensorReading = new AccelerometerReading()
                {
                    Acceleration = new Microsoft.Xna.Framework.Vector3(-args.Reading.Acceleration.X,
                        -args.Reading.Acceleration.Y, -args.Reading.Acceleration.Z),
                    Timestamp = DateTimeOffset.Now
                }
            });
        }
#endif

#if !__MOBILE__
        private void OnDesktopReadingTick(object? sender, AccelerometerReading r)
        {
            // Periodic diagnostic so the per-game wpr_game_debug.log shows whether readings
            // are flowing through. Counting + sampling keeps the log noise sane (~one line
            // per 2 seconds at 60Hz).
            int n = System.Threading.Interlocked.Increment(ref _ReadingTickCount);
            if (n == 1 || n % 120 == 0)
            {
                Trace.WriteLine($"[wpr-accel] tick #{n} reading=({r.Acceleration.X:F2},{r.Acceleration.Y:F2},{r.Acceleration.Z:F2}) " +
                                $"orient={KeyboardAccelerometerHost.Orientation} " +
                                $"ReadingChanged-subs={ReadingChanged?.GetInvocationList().Length ?? 0} " +
                                $"CurrentValueChanged-subs=via-base");
            }

            ReadingChanged?.Invoke(this, new AccelerometerReadingEventArgs(
                r.Acceleration.X, r.Acceleration.Y, r.Acceleration.Z));

            OnCurrentValueChanged(new SensorReadingEventArgs<AccelerometerReading>
            {
                SensorReading = r,
            });

            // Update the polled value too — games that don't subscribe to events but
            // instead read accel.CurrentValue each frame need this.
            CurrentValue = r;
            IsDataValid = true;
        }
#endif

        ~Accelerometer()
        {
            Stop();
        }

        public event EventHandler<AccelerometerReadingEventArgs>? ReadingChanged;

        public SensorState State { get; private set; }

        /// <summary>
        /// Last reading produced by the simulator (or hardware on mobile). WP7 games that
        /// poll instead of subscribing to <see cref="ReadingChanged"/> read this each frame.
        /// </summary>
        public AccelerometerReading CurrentValue { get; private set; }

        /// <summary>True once at least one reading has been produced since <see cref="Start"/>.</summary>
        public bool IsDataValid { get; private set; }

        /// <summary>
        /// WP7 throttle hint — how often the game wants updates. Our 60Hz simulator
        /// ignores this; the property exists so games that set it don't blow up.
        /// </summary>
        public TimeSpan TimeBetweenUpdates { get; set; } = TimeSpan.FromMilliseconds(20);

        public void Start()
        {
            if (_Started)
            {
                return;
            }

            _Started = true;

#if __MOBILE__ || __ANDROID__
            Xamarin.Essentials.Accelerometer.ReadingChanged += OnImplReadingChanged;

            if (!Xamarin.Essentials.Accelerometer.IsMonitoring)
            {
                Xamarin.Essentials.Accelerometer.Start(Xamarin.Essentials.SensorSpeed.Game);
            }
#else
            Trace.WriteLine($"[wpr-accel] Start() called by game — ReadingChanged subs={ReadingChanged?.GetInvocationList().Length ?? 0}");
            _ReadingTickCount = 0;
            KeyboardAccelerometerHost.ReadingTick += OnDesktopReadingTick;
            KeyboardAccelerometerHost.Acquire();
#endif
        }

        public void Stop()
        {
            if (!_Started)
            {
                return;
            }

#if __MOBILE__ || __ANDROID__
            Xamarin.Essentials.Accelerometer.ReadingChanged -= OnImplReadingChanged;
#else
            Trace.WriteLine($"[wpr-accel] Stop() called by game — last tick #{_ReadingTickCount}");
            KeyboardAccelerometerHost.ReadingTick -= OnDesktopReadingTick;
            KeyboardAccelerometerHost.Release();
#endif

            _Started = false;
        }
    }
}
