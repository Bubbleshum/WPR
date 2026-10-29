using System;
using Microsoft.Xna.Framework;

namespace Microsoft.Devices.Sensors
{
    /// <summary>Shim for <c>Microsoft.Devices.Sensors.AttitudeReading</c> (Windows Phone 7.1).</summary>
    public struct AttitudeReading : ISensorReading
    {
        public DateTimeOffset Timestamp { get; internal set; }

        public float Pitch { get; internal set; }

        public float Roll { get; internal set; }

        public float Yaw { get; internal set; }

        public Quaternion Quaternion { get; internal set; }

        public Matrix RotationMatrix { get; internal set; }
    }
}
