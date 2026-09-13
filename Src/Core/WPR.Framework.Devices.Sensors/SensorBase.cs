using System;
using System.Threading;

namespace Microsoft.Devices.Sensors
{
    /// <summary>
    /// Shim for <c>Microsoft.Devices.Sensors.SensorBase&lt;TSensorReading&gt;</c>.
    ///
    /// <para><b>Everything WP7 declares on THIS type has to be declared on this type, not on the
    /// concrete sensor.</b> A game's IL records the type that declared the member it called, so
    /// <c>accelerometer.CurrentValue</c> compiles to
    /// <c>callvirt !0 SensorBase`1&lt;AccelerometerReading&gt;::get_CurrentValue()</c> — a memberref
    /// against the base, which resolves against the base and nothing else. Declaring the same
    /// property on <see cref="Accelerometer"/> instead looks right, compiles, and still throws
    /// <see cref="MissingMethodException"/> at runtime. That is exactly what happened here:
    /// <c>CurrentValue</c>, <c>IsDataValid</c> and <c>TimeBetweenUpdates</c> all sat on
    /// <c>Accelerometer</c>, so Contre Jour's tilt-controlled menu
    /// (<c>Default.Namespace.AccelerometerMenu.Update</c>) threw on every single frame it ran.</para>
    ///
    /// <para>Upstream this is <see cref="IDisposable"/>, and games rely on that: releasing the sensor
    /// is how a WP7 title stops draining the battery when it leaves a tilt-controlled screen.
    /// Cro-Mag Rally's citizen12.XNA.AccelerometerHelper.Dispose forwards straight to it, so
    /// without the pattern here that teardown throws MissingMethodException.</para>
    /// </summary>
    public abstract class SensorBase<TSensorReading> : IDisposable where TSensorReading : ISensorReading
    {
        public event EventHandler<SensorReadingEventArgs<TSensorReading>>? CurrentValueChanged;

        /// <summary>
        /// Boxed <typeparamref name="TSensorReading"/>, published with a volatile reference write.
        ///
        /// <para><b>Why boxed rather than a plain auto-property.</b> Samples arrive on the
        /// platform's sampling thread while the game reads <see cref="CurrentValue"/> from its
        /// update thread, and a reading is a ~28-byte struct (a <see cref="DateTimeOffset"/>
        /// plus a vector) — big enough that a struct-copy write can be observed half-updated,
        /// which would surface as a one-frame garbage acceleration that never reproduces.
        /// Swapping a reference is atomic, so the reader either sees the previous sample whole
        /// or the new one whole. The cost is one small gen0 allocation per sample, and only
        /// while a game is actually reading.</para>
        /// </summary>
        private object? _currentValueBox;

        /// <summary>
        /// Last reading produced by the platform provider. WP7 games that poll each frame instead
        /// of subscribing to <see cref="CurrentValueChanged"/> read this.
        /// </summary>
        public TSensorReading CurrentValue
            => Volatile.Read(ref _currentValueBox) is TSensorReading reading
                ? reading
                : default!;

        /// <summary>True once at least one reading has been produced since <see cref="Start"/>.</summary>
        public bool IsDataValid => Volatile.Read(ref _currentValueBox) != null;

        /// <summary>
        /// WP7 throttle hint — how often the game wants updates. No provider honours it today
        /// (the desktop emulator ticks at 60Hz, Android samples at its Game speed); the property
        /// exists so games that set it don't blow up.
        /// </summary>
        public TimeSpan TimeBetweenUpdates { get; set; } = TimeSpan.FromMilliseconds(20);

        /// <summary>
        /// Publishes a sample, which also satisfies <see cref="IsDataValid"/>. Deliberately
        /// separate from <see cref="OnCurrentValueChanged"/> so a derived sensor can store the
        /// value before raising any event — a handler that reads <see cref="CurrentValue"/> must
        /// see the sample it was just told about, not the previous one.
        /// </summary>
        protected void SetCurrentValue(TSensorReading reading)
        {
            Volatile.Write(ref _currentValueBox, reading);
        }

        protected void OnCurrentValueChanged(SensorReadingEventArgs<TSensorReading> reading)
        {
            CurrentValueChanged?.Invoke(this, reading);
        }

        /// <summary>Abstract on WP7, so a game may call it through a <c>SensorBase</c>-typed reference.</summary>
        public abstract void Start();

        /// <summary>Abstract on WP7, so a game may call it through a <c>SensorBase</c>-typed reference.</summary>
        public abstract void Stop();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Derived sensors override this to release whatever they acquired in Start().
        /// Must tolerate being called more than once — games dispose in both a screen-teardown
        /// path and a finalizer.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
        }
    }
}
