using System;

namespace WPR.Engine.Vibration;

/// <summary>
/// One thing that can buzz. A platform head implements it over the device's vibration motor
/// (<c>WPR.Vibration.AndroidVibrator</c>) and registers it into <see cref="VibrationBackend"/> at
/// launcher start-up; <c>Microsoft.Devices.VibrateController</c> is the WP7 API that consumes it.
///
/// <para><b>Deliberately expressed in <see cref="TimeSpan"/> and a scalar.</b> Same reasoning that
/// kept <c>IAccelerometerProvider</c> in <see cref="System.Numerics.Vector3"/>: the WP7 vocabulary
/// (<c>VibrateController</c>) lives in <c>Microsoft.Phone</c>, which <em>consumes</em> this
/// contract, so naming it here would make this project depend on its own consumer. A vibration
/// request is a duration and a strength, so the neutral form costs nothing.</para>
///
/// <para><b>Shaped so a controller can fill it too.</b> That is why <see cref="Vibrate"/> takes an
/// <paramref name="intensity"/> the WP7 API has no way to supply — a handset motor is on or off,
/// but a pad's rumble is fundamentally amplitude-based, and a contract without amplitude would have
/// to be widened the day the second implementation arrived. A pad implementation drives both of its
/// motors from the single scalar and runs a timer for the duration; see the remarks on
/// <see cref="VibrationBackend.Device"/> for where it plugs in.</para>
///
/// <para><b>This is not the XNA gamepad rumble API.</b> <c>GamePad.SetVibration</c> already exists
/// and already works, over <c>WPR.Xna.Rhi.IInputBackend.SetGamePadVibration</c> into SDL — it is
/// per-pad, per-motor and level-based (it runs until changed, with no duration). Do not route it
/// through here and do not grow this interface towards it: they are different APIs with different
/// lifetimes, and a game calling one is not asking for the other. What this seam adds is the
/// ability for a game that only knows the WP7 handset API to reach whatever the player is
/// actually holding.</para>
/// </summary>
public interface IVibrationProvider
{
    /// <summary>
    /// Whether this provider has hardware behind it. False means "registered, but this device has
    /// no motor" — an Android tablet without one, say. Both that and no provider at all degrade to
    /// silence rather than an error, matching how GamerServices degrades with no achievement store.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Buzzes for <paramref name="duration"/> at <paramref name="intensity"/>.
    ///
    /// <para><b>Replaces, never queues.</b> A second call cancels whatever is running and starts
    /// afresh — the behaviour of both <c>android.os.Vibrator.vibrate</c> and a pad's rumble
    /// registers, and what a WP7 title expects when it re-triggers a buzz on a rapid event.</para>
    ///
    /// <para>Implementations must not throw: a game must never die because the motor was busy,
    /// missing, or refused by the OS. A non-positive <paramref name="duration"/> does nothing.
    /// <paramref name="intensity"/> is 0..1 and is clamped by the implementation, which may also
    /// ignore it outright — most handset motors before API 26 have no amplitude control and can
    /// only run flat out.</para>
    /// </summary>
    /// <param name="duration">How long to buzz for. Non-positive is a no-op.</param>
    /// <param name="intensity">Strength, 0 (off) to 1 (full). Clamped by the implementation.</param>
    void Vibrate(TimeSpan duration, float intensity);

    /// <summary>
    /// Stops immediately, whether or not anything is running.
    ///
    /// <para>Called by <c>VibrateController.Stop()</c>, by <c>GameActivity.OnPause</c> so a game
    /// backgrounded mid-buzz does not keep the phone shaking on the home screen, and by
    /// <c>ApplicationLaunch.ResetWprSingletons</c> on teardown — a WP7 title that exits during a
    /// vibration never gets to stop it itself. Must be idempotent and must not throw.</para>
    /// </summary>
    void Stop();
}
