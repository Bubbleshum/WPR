using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Android.Hardware;
using WPR.Engine.Sensors;

namespace WPR.Input.AndroidSensor
{
    /// <summary>
    /// This head's <see cref="IGyroscopeProvider"/>: the device's gyroscope, read straight from
    /// <see cref="SensorManager"/>. The counterpart of <see cref="AndroidAccelerometerProvider"/>,
    /// with the same lifetime rules, and the same rule for reading a <see cref="SensorEvent"/>:
    /// <c>Values</c> ONCE per event, copied out and disposed (see that class for why).
    ///
    /// <para>Android reports radians per second about the device's own axes, right-handed, which
    /// is exactly WP7's convention, so samples pass through unconverted.</para>
    /// </summary>
    public sealed class AndroidGyroscopeProvider : IGyroscopeProvider
    {
        private readonly object _gate = new object();
        private int _consumers;
        private int _tickCount;
        private SensorManager? _sensorManager;
        private Sensor? _sensor;
        private bool _resolved;
        private SampleListener? _listener;

        public bool IsSupported
        {
            get
            {
                try
                {
                    return ResolveSensor() != null;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[wpr-gyro] IsSupported probe failed: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
        }

        public Vector3 CurrentRotationRate { get; private set; }

        public event Action<Vector3>? ReadingChanged;

        /// <summary>Adds one reader, registering the listener on the first. Like the accelerometer,
        /// the listener then stays registered until <see cref="ResetForNewLaunch"/>.</summary>
        public void Start()
        {
            bool register;
            int consumers;
            lock (_gate)
            {
                consumers = ++_consumers;
                if (consumers == 1)
                {
                    _tickCount = 0;
                }

                register = _listener == null;
                if (register)
                {
                    _listener = new SampleListener(this);
                }
            }

            if (register)
            {
                try
                {
                    Sensor? sensor = ResolveSensor();
                    if (_sensorManager is { } manager && sensor != null)
                    {
                        manager.RegisterListener(_listener, sensor, SensorDelay.Game);
                    }
                    else
                    {
                        Trace.WriteLine("[wpr-gyro] no gyroscope on this device - readings will not flow");
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _listener = null;
                    }

                    Trace.WriteLine($"[wpr-gyro] hardware start failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Trace.WriteLine($"[wpr-gyro] start - readers={consumers}"
                + (register ? ", listener registered" : ", listener already registered"));
        }

        public void Stop()
        {
            int consumers;
            lock (_gate)
            {
                if (_consumers > 0)
                {
                    _consumers--;
                }

                consumers = _consumers;
            }

            Trace.WriteLine($"[wpr-gyro] stop - readers={consumers}, ticks={Volatile.Read(ref _tickCount)}");
        }

        public void ResetForNewLaunch()
        {
            SampleListener? listener;
            lock (_gate)
            {
                ReadingChanged = null;
                _consumers = 0;
                _tickCount = 0;
                listener = _listener;
                _listener = null;
            }

            if (listener == null)
            {
                return;
            }

            try
            {
                _sensorManager?.UnregisterListener(listener);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-gyro] hardware stop failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                listener.Dispose();
            }
        }

        private Sensor? ResolveSensor()
        {
            if (_resolved)
            {
                return _sensor;
            }

            _sensorManager ??= global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.SensorService) as SensorManager;
            _sensor = _sensorManager?.GetDefaultSensor(SensorType.Gyroscope);
            _resolved = _sensorManager != null;
            return _sensor;
        }

        private void OnSample(float x, float y, float z)
        {
            var rate = new Vector3(x, y, z);
            CurrentRotationRate = rate;

            if (Volatile.Read(ref _consumers) == 0)
            {
                return;
            }

            int n = Interlocked.Increment(ref _tickCount);
            if (n == 1 || n % 300 == 0)
            {
                Trace.WriteLine($"[wpr-gyro] tick #{n} rate=({x:F2},{y:F2},{z:F2}) rad/s readers={Volatile.Read(ref _consumers)}");
            }

            ReadingChanged?.Invoke(rate);
        }

        private sealed class SampleListener : Java.Lang.Object, ISensorEventListener
        {
            private readonly AndroidGyroscopeProvider _owner;

            internal SampleListener(AndroidGyroscopeProvider owner) => _owner = owner;

            public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy)
            {
            }

            public void OnSensorChanged(SensorEvent? e)
            {
                if (e == null)
                {
                    return;
                }

                float x, y, z;
                IList<float>? values = e.Values;
                try
                {
                    if (values == null || values.Count < 3)
                    {
                        return;
                    }

                    x = values[0];
                    y = values[1];
                    z = values[2];
                }
                finally
                {
                    (values as IDisposable)?.Dispose();
                }

                _owner.OnSample(x, y, z);
            }
        }
    }
}
