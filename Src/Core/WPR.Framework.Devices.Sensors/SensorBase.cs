using System;

namespace Microsoft.Devices.Sensors
{
    /// <summary>
    /// Shim for <c>Microsoft.Devices.Sensors.SensorBase&lt;TSensorReading&gt;</c>.
    ///
    /// Upstream this is <see cref="IDisposable"/>, and games rely on that: releasing the sensor
    /// is how a WP7 title stops draining the battery when it leaves a tilt-controlled screen.
    /// Cro-Mag Rally's citizen12.XNA.AccelerometerHelper.Dispose forwards straight to it, so
    /// without the pattern here that teardown throws MissingMethodException.
    /// </summary>
    public abstract class SensorBase<TSensorReading> : IDisposable where TSensorReading : ISensorReading
    {
        public event EventHandler<SensorReadingEventArgs<TSensorReading>>? CurrentValueChanged;

        protected void OnCurrentValueChanged(SensorReadingEventArgs<TSensorReading> reading)
        {
            CurrentValueChanged?.Invoke(this, reading);
        }

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
