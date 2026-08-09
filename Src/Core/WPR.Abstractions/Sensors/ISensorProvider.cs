using System;
using System.Numerics;

namespace WPR.Abstractions.Sensors;

/// <summary>
/// Platform-side motion sensors. A <see cref="WPR.Platform"/> provider implements it
/// from the OS (or the desktop keyboard emulator). WPR.Framework.Devices maps the
/// readings onto the WP7 sensor API — e.g. <c>AccelerometerReading.Acceleration</c>,
/// which games bind to as <c>Microsoft.Xna.Framework.Vector3</c> by identity, so the
/// framework converts this <see cref="Vector3"/> to the owned XNA vector (Stage 5d).
///
/// Only the accelerometer is modelled now — compass/gyro/motion are added when their
/// WP7 shims are implemented.
/// </summary>
public interface ISensorProvider
{
    bool IsAccelerometerSupported { get; }

    /// <summary>Latest acceleration in g-units (x, y, z).</summary>
    Vector3 CurrentAcceleration { get; }

    /// <summary>Raised when a new accelerometer sample is available.</summary>
    event Action<Vector3>? AccelerometerChanged;

    void StartAccelerometer();
    void StopAccelerometer();
}
