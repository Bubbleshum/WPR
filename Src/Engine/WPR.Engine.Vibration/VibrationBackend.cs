namespace WPR.Engine.Vibration
{
    /// <summary>
    /// Backend registry for haptics — the direct counterpart of
    /// <c>WPR.Engine.Sensors.SensorBackend</c>, and filled the same way: the composition root
    /// (<c>PlatformComposition</c>, from a head's <c>caps.Vibration(...)</c>) sets the platform
    /// implementation before any app is loaded.
    ///
    /// <para>A settable static rather than constructor injection because the consumer is a WP7
    /// shim — <c>Microsoft.Devices.VibrateController</c>, whose <c>Default</c> singleton
    /// <em>games</em> reach for. WPR never gets to pass it anything. That is the same constraint
    /// which produced every other registry here.</para>
    ///
    /// <para><b>One slot per device, not one interface per subsystem.</b> The subsystem is
    /// vibration; the device is the handset's motor. This is the <c>AudioBackendRegistry</c>
    /// shape (<c>Sound</c> / <c>Xact</c> / <c>Media</c> are three slots on one registry), and it
    /// is the answer to "where does controller rumble go": a <c>Controller</c> slot beside
    /// <see cref="Device"/>, filled by a <c>WPR.Vibration.Gamepad</c> module implementing the same
    /// <see cref="IVibrationProvider"/> — never a second project, and never extra members on this
    /// one. <c>VibrateController</c> then picks between them (a pad in the player's hands should
    /// win over the slab on the table), which is a policy decision worth making with a real pad
    /// in front of you rather than now.</para>
    ///
    /// <para><b>Unlike <c>XnaBackend</c>, this is not cleared at teardown.</b> The registered
    /// provider is owned by the launcher and lives in its default ALC, so it cannot pin a per-game
    /// ALC; clearing it would leave the second game of a session unable to vibrate. There is no
    /// per-game subscriber list to drop either — this seam is push-only, which is why it needs no
    /// <c>ResetForNewLaunch</c> counterpart to the sensor one. What teardown does need is a
    /// <see cref="IVibrationProvider.Stop"/>, so a game that exited mid-buzz does not leave the
    /// phone shaking; <c>ApplicationLaunch.ResetWprSingletons</c> makes that call.</para>
    ///
    /// <para>Null is the supported default, and is what Windows gets — a desktop PC has no motor,
    /// so <c>WindowsPlatform</c> declares no vibration and <c>VibrateController</c> stays the no-op
    /// it has always been there. Absent means "this platform does not have it", never an error.</para>
    /// </summary>
    public static class VibrationBackend
    {
        /// <summary>
        /// The registered handset vibration motor, or null when none is composed in.
        /// Set by the composition root; see <c>WPR.Platform.Android.AndroidPlatform</c>.
        /// </summary>
        public static IVibrationProvider? Device { get; private set; }

        /// <summary>
        /// Whether the user wants games to vibrate at all — the global on/off switch, persisted
        /// as <c>Configuration.VibrationEnabled</c> and surfaced on the Android settings page.
        /// Defaults to true, including for a config.json written before the setting existed.
        ///
        /// <para><b>Read live, not captured.</b> Same shape as
        /// <c>KeyboardEmulationHost.IsOverlayEnabled</c>. In practice the value cannot change
        /// under a running game — settings live in the launcher process and
        /// <c>GameActivity.OnDestroy</c> kills the <c>:game</c> process, so every launch reads
        /// config.json afresh — but reading live means that stays correct if the process is ever
        /// kept alive between games, which a captured copy would not.</para>
        ///
        /// <para><b>This is the gate for EVERY vibration path, not just <see cref="Device"/>.</b>
        /// It also mutes XNA gamepad rumble (<c>GamePad.SetVibration</c>), which does not go
        /// through this registry at all — it goes to SDL through
        /// <c>IInputBackend.SetGamePadVibration</c>. A switch labelled "vibration" that left a
        /// connected pad rumbling would be a bug, so the two paths deliberately share this one
        /// answer, and it lives here because this is the assembly whose subject is vibration.
        /// A future <c>Controller</c> slot needs no extra work: anything that reads this is
        /// already covered.</para>
        ///
        /// <para><b>Only calls that START a vibration should consult it.</b>
        /// <see cref="IVibrationProvider.Stop"/> must run regardless — teardown and
        /// <c>GameActivity.OnPause</c> silence the motor unconditionally, and a stop that
        /// honoured the preference could strand a buzz that began while it was on.</para>
        /// </summary>
        public static bool IsEnabled => WPR.Common.Configuration.Current?.VibrationEnabled != false;

        /// <summary>
        /// Installs the platform provider. Idempotent by assignment — a head that re-runs its
        /// composition (Android recreates the process straight into any activity, and
        /// <c>GameActivity</c>'s <c>:game</c> process composes again) replaces rather than
        /// accumulates.
        ///
        /// <para><b>The outgoing provider is stopped on the way out.</b> Nothing else would ever
        /// reach it again — teardown only stops whatever is registered <em>now</em> — so a
        /// displaced provider that happened to be mid-buzz would keep the motor running with no
        /// way left to silence it. Both heads compose once per process today, so this is closing a
        /// hole rather than fixing a live bug; it is here so that stays true if a head ever
        /// re-composes. Same reasoning as <c>SensorBackend.SetAccelerometer</c>.</para>
        /// </summary>
        public static void SetDevice(IVibrationProvider? provider)
        {
            IVibrationProvider? previous = Device;
            Device = provider;

            if (previous != null && !ReferenceEquals(previous, provider))
            {
                try
                {
                    previous.Stop();
                }
                catch
                {
                    // Composition must not fail because a retired provider objected to being
                    // silenced.
                }
            }
        }
    }
}
