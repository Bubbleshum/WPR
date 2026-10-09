using System;
using System.Numerics;

namespace WPR.Engine.Sensors;

/// <summary>
/// The platform-side gyroscope: how fast the device is turning about each of its own axes.
/// The second device on <see cref="SensorBackend"/>, beside <see cref="IAccelerometerProvider"/>,
/// and the same shape for the same reasons (see that interface) — counted start/stop, a reset
/// at teardown, <see cref="System.Numerics.Vector3"/> so this project names no consumer's types.
///
/// <para><b>Units and axes are WP7's</b>: radians per second, right-handed about the device
/// frame (x to the right of the screen, y to its top, z out of it; positive is anticlockwise
/// looking down the axis). That is also Android's convention, so a phone's reading passes
/// through unchanged. WinRT's <c>Gyrometer</c> speaks degrees per second about the same axes;
/// the WP8 runtime converts at its boundary.</para>
///
/// <para>Only Android fills this. A PC has no gyroscope and the desktop declares none, so a
/// game asking finds none — which every WP8 game was written to handle, since many phones of
/// the time shipped without one.</para>
/// </summary>
public interface IGyroscopeProvider
{
    bool IsSupported { get; }

    /// <summary>Latest rotation rate in radians per second (x, y, z), in the WP7 device frame.</summary>
    Vector3 CurrentRotationRate { get; }

    /// <summary>Raised when a new sample is available, on the platform's sensor thread.</summary>
    event Action<Vector3>? ReadingChanged;

    /// <summary>Registers one reader and powers the sensor up if it was idle. Counted, as on
    /// <see cref="IAccelerometerProvider.Start"/>.</summary>
    void Start();

    /// <summary>Releases one reader. Must tolerate an unbalanced call.</summary>
    void Stop();

    /// <summary>Drops every subscriber and reader left by a game that has exited, unconditionally.
    /// The host calls it on game teardown; see <c>ApplicationLaunch.ResetWprSingletons</c>.</summary>
    void ResetForNewLaunch();
}
