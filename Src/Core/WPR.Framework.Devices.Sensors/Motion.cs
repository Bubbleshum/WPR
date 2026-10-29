using System;

namespace Microsoft.Devices.Sensors
{
    /// <summary>
    /// Shim for <c>Microsoft.Devices.Sensors.Motion</c> (Windows Phone 7.1), the fused
    /// attitude / acceleration / rotation-rate sensor.
    ///
    /// <para><b>Reports unsupported, honestly.</b> On a phone Motion fused the compass with the
    /// accelerometer (and the gyroscope where present), and no platform here registers a compass,
    /// so there is nothing to fuse. WP7.1 titles were written for devices without the API's
    /// sensors and check <see cref="IsSupported"/>; the ones that do not still get a sensor whose
    /// <see cref="Start"/> does nothing and whose <see cref="SensorBase{T}.IsDataValid"/> stays
    /// false, rather than a <see cref="TypeLoadException"/>.</para>
    ///
    /// <para>The type had to exist even so: a missing type is resolved when the method naming it is
    /// compiled, so PAC-MAN Kart Rally, whose <c>Game</c> constructor only declares a
    /// <c>Motion</c> field it never assigns, died before its first frame.</para>
    /// </summary>
    public sealed class Motion : SensorBase<MotionReading>
    {
        public static bool IsSupported => false;

        public Motion()
        {
        }

        public SensorState State => SensorState.NotSupported;

        /// <summary>Raised on a phone when the compass needs calibrating. Never raised here.</summary>
        public event EventHandler<CalibrationEventArgs>? Calibrate
        {
            add { }
            remove { }
        }

        public override void Start()
        {
        }

        public override void Stop()
        {
        }
    }
}
