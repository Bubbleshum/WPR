using System;
using Microsoft.Xna.Framework;

namespace Microsoft.Devices.Sensors
{
    /// <summary>Shim for <c>Microsoft.Devices.Sensors.MotionReading</c> (Windows Phone 7.1).</summary>
    public struct MotionReading : ISensorReading
    {
        public DateTimeOffset Timestamp { get; internal set; }

        public AttitudeReading Attitude { get; internal set; }

        public Vector3 DeviceAcceleration { get; internal set; }

        public Vector3 DeviceRotationRate { get; internal set; }

        public Vector3 Gravity { get; internal set; }
    }
}
