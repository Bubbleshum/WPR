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
    /// This head's <see cref="IAccelerometerProvider"/>: the device's real accelerometer, read
    /// straight from <see cref="SensorManager"/>.
    ///
    /// <para>Registered into <c>WPR.Engine.Sensors.SensorBackend</c> by the head's
    /// <c>AndroidPlatform</c> descriptor, which runs in the launcher process and again in
    /// <c>GameActivity</c>'s <c>:game</c> process — the game process needs its own registration
    /// because nothing static is shared across that boundary.</para>
    ///
    /// <para><b>Why this does not use Xamarin.Essentials (2026-09-05).</b> It did until tilt was
    /// reported dying mid-game on a Galaxy S24. Measured: the sensor delivered ~1000 healthy
    /// samples (|a| ≈ 1g) and then every reading collapsed to a frozen
    /// <c>(-0.001, 0.000, 0.000)</c> for the rest of the process — events still arriving at full
    /// rate, listener still registered, nothing thrown. Twenty seconds of tilt, then nothing, with
    /// no signal anywhere that something had gone wrong.</para>
    ///
    /// <para>The cause is in the binding, not the hardware.
    /// <c>Android.Hardware.SensorEvent.Values</c> compiles to
    /// <c>JavaArray&lt;float&gt;.FromJniHandle(handle, JniHandleOwnership.TransferLocalRef)</c> — every
    /// call mints a fresh wrapper that <em>owns a JNI reference</em> and must be disposed.
    /// Essentials' <c>OnSensorChanged</c> calls <c>Values</c> three times (once per axis) and
    /// disposes none of them, so at 50 Hz it leaks 150 JNI references a second until reads start
    /// returning garbage. Nothing about that is fixable from outside Essentials, and the whole of
    /// its accelerometer support is the twenty lines reimplemented below.</para>
    ///
    /// <para><b>So the rule for anything reading a <see cref="SensorEvent"/>: call
    /// <c>Values</c> ONCE per event, copy the floats out, and dispose it.</b> Indexing it twice is
    /// not merely wasteful — it is the bug.</para>
    /// </summary>
    public sealed class AndroidAccelerometerProvider : IAccelerometerProvider
    {
        /// <summary>
        /// Gravity in m/s², to convert the platform's units into the G-normalised values WP7 games
        /// expect. Matches the constant Essentials used, so readings are unchanged by the switch.
        /// </summary>
        private const float StandardGravity = 9.80665f;

        /// <summary>
        /// Guards <see cref="_consumers"/> and the registration. The JNI calls into
        /// <c>SensorManager</c> happen <em>outside</em> it: <see cref="Stop"/> is reachable from a
        /// GC finalizer (a game that drops its <c>Accelerometer</c> without stopping it), so the
        /// lock is kept off the blocking call.
        /// </summary>
        private readonly object _gate = new object();

        /// <summary>
        /// How many <c>Accelerometer</c> shims are currently started against this provider. It
        /// gates fan-out: a sample arriving while this is zero is dropped rather than delivered.
        /// </summary>
        private int _consumers;

        /// <summary>Samples delivered since the first reader started; drives the sampled trace.</summary>
        private int _tickCount;

        /// <summary>
        /// Latches while the sensor is delivering a physically impossible magnitude, so the trace
        /// reports the transition once rather than every sample. Diagnostic only.
        /// </summary>
        private bool _readingImplausible;

        private SensorManager? _sensorManager;
        private Sensor? _sensor;
        private SampleListener? _listener;

        /// <summary>
        /// Every Windows Phone device shipped an accelerometer, and this reports whether the
        /// running device actually has one rather than assuming — it is one call, and a tablet or
        /// emulator image without the sensor should degrade to "no readings" rather than
        /// pretending.
        /// </summary>
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
                    Trace.WriteLine($"[wpr-accel] IsSupported probe failed: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Last sample seen, in the WP7 device frame. Deliberately not cleared on stop, so a reader
        /// that starts again gets the last known attitude rather than a zero vector while the
        /// sensor spins back up.
        /// </summary>
        public Vector3 CurrentAcceleration { get; private set; }

        public event Action<Vector3>? ReadingChanged;

        /// <summary>
        /// Adds one reader, registering the listener on the first.
        ///
        /// <para>Registration is keyed on whether the listener exists, not on the reader count, so
        /// that <see cref="Stop"/> is free to leave it registered — see its remarks.</para>
        /// </summary>
        public void Start()
        {
            bool register;
            int consumers;
            lock (_gate)
            {
                consumers = ++_consumers;
                if (consumers == 1)
                {
                    // Per-session counters, so each start/stop pair reads as its own block in the
                    // log even though the registration underneath is continuous.
                    _tickCount = 0;
                    _readingImplausible = false;
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
                    // ResolveSensor() is what assigns _sensorManager, so it has to run FIRST —
                    // reading the field into a local before this call left it null on the very
                    // first Start and reported "no accelerometer on this device" on a phone that
                    // plainly has one.
                    Sensor? sensor = ResolveSensor();
                    SensorManager? manager = _sensorManager;
                    if (manager != null && sensor != null)
                    {
                        // SensorDelay.Game is ~20ms, matching what WP7 titles expect and what the
                        // shim's default TimeBetweenUpdates advertises.
                        manager.RegisterListener(_listener, sensor, SensorDelay.Game);
                    }
                    else
                    {
                        Trace.WriteLine("[wpr-accel] no accelerometer on this device — readings will not flow");
                    }
                }
                catch (Exception ex)
                {
                    // A sensor that refuses to register must not take the game down with it; the
                    // reader simply sees no samples, the same as an unregistered provider.
                    lock (_gate)
                    {
                        _listener = null;
                    }
                    Trace.WriteLine($"[wpr-accel] hardware start failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Trace.WriteLine($"[wpr-accel] start (hardware) — readers={consumers}"
                + (register ? ", listener registered" : ", listener already registered"));
        }

        /// <summary>
        /// Releases one reader's hold. The count is exact — it is what gates fan-out, and
        /// <see cref="ResetForNewLaunch"/> still unregisters — but the last reader leaving
        /// deliberately does <b>not</b> unregister the listener.
        ///
        /// <para>WP7 titles stop and start their sensor per screen (Doodle Jump does it around
        /// every menu transition) purely to save power on 2010 hardware. Churning the platform
        /// registration that often buys nothing here and costs robustness — each cycle is a fresh
        /// chance for the listener and its Java peer to be torn down and rebuilt underneath a
        /// running game. The game process is short-lived and <c>GameActivity</c> kills it on exit,
        /// so the sensor is released promptly regardless.</para>
        /// </summary>
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

            Trace.WriteLine(
                $"[wpr-accel] stop — readers={consumers}, ticks={Volatile.Read(ref _tickCount)}"
                + (consumers == 0 ? ", listener left registered" : string.Empty));
        }

        /// <summary>
        /// Drops this provider's subscribers and unregisters the listener unconditionally, whatever
        /// the reader count says — a game that exited without stopping its sensor is exactly the
        /// case this exists for. See the contract's remarks for the ALC leak it prevents.
        /// </summary>
        public void ResetForNewLaunch()
        {
            SampleListener? listener;
            lock (_gate)
            {
                ReadingChanged = null;
                _consumers = 0;
                _tickCount = 0;
                _readingImplausible = false;
                listener = _listener;
                _listener = null;
            }

            UnregisterListener(listener);
        }

        /// <summary>Unregisters and disposes a listener. Never called with <see cref="_gate"/> held.</summary>
        private void UnregisterListener(SampleListener? listener)
        {
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
                // Best-effort: neither a screen change nor teardown may fail because a sensor
                // refused to stop.
                Trace.WriteLine($"[wpr-accel] hardware stop failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                listener.Dispose();
            }
        }

        /// <summary>
        /// Resolves (and caches) the default accelerometer. Uses the application context, never an
        /// activity — this provider outlives any one screen.
        /// </summary>
        private Sensor? ResolveSensor()
        {
            if (_sensor != null)
            {
                return _sensor;
            }

            _sensorManager ??= global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.SensorService) as SensorManager;
            _sensor = _sensorManager?.GetDefaultSensor(SensorType.Accelerometer);
            return _sensor;
        }

        /// <summary>
        /// One hardware sample, already converted out of the platform's units and axes, fanned out
        /// to every started <c>Accelerometer</c>.
        /// </summary>
        private void OnSample(float rawX, float rawY, float rawZ)
        {
            // Android measures the reaction force in m/s² along axes pointing the same way as WP7's,
            // so the conversion is a scale into G plus a sign flip on all three.
            var acceleration = new Vector3(
                -rawX / StandardGravity,
                -rawY / StandardGravity,
                -rawZ / StandardGravity);

            CurrentAcceleration = acceleration;

            // Drop anything that arrives once nothing is reading. Samples come from the sensor
            // thread, so a callback already in flight can land after the last Stop, or during
            // ResetForNewLaunch. Same guard, and the same reasoning, as the Windows provider.
            if (Volatile.Read(ref _consumers) == 0)
            {
                return;
            }

            int n = Interlocked.Increment(ref _tickCount);
            if (n == 1 || n % 120 == 0)
            {
                Trace.WriteLine(
                    $"[wpr-accel] tick #{n} reading=({acceleration.X:F2},{acceleration.Y:F2},{acceleration.Z:F2}) " +
                    $"|a|={acceleration.Length():F2} readers={Volatile.Read(ref _consumers)}");
            }

            // Report the EDGES of an implausible reading, not just whatever the every-120th sample
            // happens to catch. A device at rest or in normal motion always measures gravity, so
            // |a| sits near 1g; only genuine free-fall reads zero. A sustained near-zero magnitude
            // means the sensor has stopped reporting physics while still firing events — which is
            // what the Essentials JNI leak looked like from in here, and is invisible to the reader
            // count and the start/stop lines because none of them change.
            //
            // Hysteresis (0.25 in, 0.40 out) so a phone genuinely thrown in the air, or one
            // hovering at the threshold, cannot spam the log with transitions.
            float magnitude = acceleration.Length();
            bool flat = Volatile.Read(ref _readingImplausible);
            if (!flat && magnitude < 0.25f)
            {
                Volatile.Write(ref _readingImplausible, true);
                Trace.WriteLine(
                    $"[wpr-accel] READINGS WENT FLAT at tick #{n}: |a|={magnitude:F3} " +
                    $"raw=({rawX:F3},{rawY:F3},{rawZ:F3}) readers={Volatile.Read(ref _consumers)}");
            }
            else if (flat && magnitude > 0.40f)
            {
                Volatile.Write(ref _readingImplausible, false);
                Trace.WriteLine($"[wpr-accel] readings recovered at tick #{n}: |a|={magnitude:F2}");
            }

            ReadingChanged?.Invoke(acceleration);
        }

        /// <summary>
        /// The platform listener. Kept private and minimal on purpose: the only interesting thing
        /// it does is read <see cref="SensorEvent.Values"/> exactly once and dispose it.
        /// </summary>
        private sealed class SampleListener : Java.Lang.Object, ISensorEventListener
        {
            private readonly AndroidAccelerometerProvider _owner;

            internal SampleListener(AndroidAccelerometerProvider owner) => _owner = owner;

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

                // ONE call to Values, values copied out immediately, wrapper disposed. Each call
                // returns a JavaArray<float> that owns a JNI reference (FromJniHandle with
                // TransferLocalRef), so calling it per-axis and leaving the wrappers to the
                // finalizer is what poisoned the readings under Xamarin.Essentials — see the class
                // remarks. Disposing inside the callback keeps the reference count flat forever.
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
