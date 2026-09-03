using System;
using System.Numerics;

namespace WPR.Engine.Sensors;

/// <summary>
/// The platform-side accelerometer. A platform head implements it from the OS
/// (<c>WPR.Platform.Android</c>) or from the desktop keyboard emulator
/// (<c>WPR.Platform.Windows</c>), and registers the implementation into
/// <c>WPR.Engine.Sensors.SensorBackend</c> at launcher start-up. <c>WPR.Framework.Devices.Sensors</c>
/// maps the readings onto the WP7 sensor API — <c>AccelerometerReading.Acceleration</c>,
/// which games bind to as <c>Microsoft.Xna.Framework.Vector3</c> by identity, so the
/// framework converts this <see cref="Vector3"/> to the owned XNA vector.
///
/// <para><b>Deliberately expressed in <see cref="System.Numerics.Vector3"/>.</b> The WP7
/// vocabulary (<c>AccelerometerReading</c>, the XNA <c>Vector3</c>) lives in
/// <c>Microsoft.Devices.Sensors</c> / <c>WPR.Framework.Xna</c>, which consume this contract —
/// using those types here would make this project depend on its own consumers.
/// This is the same cycle that put the XNA device seam in <c>WPR.Xna.Rhi</c> rather than here;
/// the difference is that a motion sample is three floats, so a neutral vector type costs one
/// conversion at the boundary instead of an entire vocabulary.</para>
///
/// <para><b>One device, and this interface never grows.</b> It is named for the accelerometer
/// rather than for sensors in general because every member here is an accelerometer member —
/// and because the previous name invited the opposite. A compass, gyroscope or motion source
/// gets its own interface beside this one and its own slot on <c>SensorBackend</c>, at the
/// point its WP7 shim is actually written; <c>WPR.Framework.Devices.Sensors</c> ships only
/// <c>Accelerometer</c> today, so there is nothing to model yet. Widening this instead would
/// give every implementation members it must stub, which is how a seam turns into a bucket.</para>
/// </summary>
public interface IAccelerometerProvider
{
    bool IsSupported { get; }

    /// <summary>Latest acceleration in g-units (x, y, z), in the WP7 device frame.</summary>
    Vector3 CurrentAcceleration { get; }

    /// <summary>Raised when a new accelerometer sample is available. May fire on a
    /// non-UI thread — the desktop emulator raises it from a timer.</summary>
    event Action<Vector3>? ReadingChanged;

    /// <summary>
    /// Registers one reader and powers the sensor up if it was idle.
    ///
    /// <para><b>Calls are counted, not idempotent.</b> Several WP7 <c>Accelerometer</c> shims
    /// can be live at once and they all share one provider, so an implementation must pair each
    /// start with its own stop and keep sampling until the last one goes. It must also tolerate
    /// being started twice without delivering a sample twice.</para>
    /// </summary>
    void Start();

    /// <summary>
    /// Releases one reader, and powers the sensor down once the last one has gone — the point
    /// of the counting above. On a phone this is battery: WP7 titles start and stop their
    /// sensor per screen precisely so it can be released, and a shared provider that kept
    /// sampling after the last reader left would quietly defeat that.
    ///
    /// <para>Must tolerate an unbalanced call rather than letting the count go negative.</para>
    /// </summary>
    void Stop();

    /// <summary>
    /// Drops every subscriber and all sampling state left behind by a game that has exited.
    /// The host MUST call this on game teardown; see
    /// <c>ApplicationLaunch.ResetWprSingletons</c>.
    ///
    /// <para><b>Why it is on the contract and not left to the framework.</b> The WP7
    /// <c>Accelerometer</c> shim subscribes to <see cref="ReadingChanged"/> on a
    /// provider that lives in the launcher's default ALC, and WP7 games routinely exit
    /// without calling <c>Accelerometer.Stop()</c>. The subscription then keeps the shim
    /// alive, which keeps the game's own reading handlers alive, which pins the game's
    /// collectible <c>AssemblyLoadContext</c> so it can never unload — and any sampling
    /// timer keeps running against an ever-growing handler list, so cost climbs with every
    /// game launched in a session. Only the provider can clear its own event, and nothing
    /// inside the framework knows when a game has ended, so the reset has to be reachable
    /// from the host through this interface. Implementations must be idempotent, and must
    /// stop sampling and zero the reader count unconditionally — a game that exited without
    /// calling <see cref="Stop"/> is the whole reason this exists, so honouring
    /// the count here would leave the sensor running forever.</para>
    /// </summary>
    void ResetForNewLaunch();
}
