
namespace WPR.Engine.Audio
{
    /// <summary>
    /// Backend registry for the install-time audio transcoder — the same shape as
    /// <c>WPR.Engine.Sensors.SensorBackend</c>, <c>WPR.SilverlightCompability.SilverlightBackend</c> and
    /// <c>WPR.Xna.Rhi.XnaBackend</c>: it sits beside its consumer
    /// (<see cref="AudioCompabilityConverter"/>) and the launcher — the composition root — installs
    /// the platform implementation in <c>ServicesSetup.Start()</c> before anything is installed or
    /// launched.
    ///
    /// <para>A settable static rather than a parameter on
    /// <c>ApplicationInstaller.Install</c> because the installer is static and is called from
    /// several places in both heads (<c>XapInstallFlow</c>, <c>GamesActivity</c>, the desktop
    /// listing page, the <c>--reinstall-all</c> CLI path). Threading a transcoder through all of
    /// them to reach one leaf would be churn for no gain; the existing registries were introduced
    /// under the same reasoning.</para>
    ///
    /// <para><b>Not cleared at teardown.</b> The transcoder is launcher-lifetime and stateless, and
    /// unlike <c>XnaBackend</c> it is never handed to game code, so it cannot pin a per-game
    /// AssemblyLoadContext.</para>
    ///
    /// <para><b>Null is not a supported steady state here</b> — and that is the deliberate
    /// difference from <c>SensorBackend</c>, where "no provider" degrades to "no readings". A
    /// missing transcoder means every <c>.wma</c> soundtrack stays unplayable, which is not
    /// degradation the user can see or diagnose: the game installs cleanly and is simply mute.
    /// <see cref="AudioCompabilityConverter.ScanWmaAndConvert"/> therefore throws rather than
    /// skipping, and the install surfaces
    /// <c>ApplicationInstallError.ConvertFailed</c>.</para>
    /// </summary>
    public static class AudioTranscoderBackend
    {
        /// <summary>
        /// The registered platform transcoder, or null when none is composed in.
        /// Set by the launcher; see <c>WPR.Platform.Windows.ServicesSetup</c> and
        /// <c>WPR.Platform.Android.ServicesSetup</c>.
        /// </summary>
        public static IAudioTranscoder? Transcoder { get; private set; }

        /// <summary>
        /// Installs the platform transcoder. Idempotent by assignment — a head that re-runs
        /// <c>ServicesSetup.Start()</c> (Android recreates the process straight into any activity)
        /// replaces rather than accumulates.
        /// </summary>
        public static void SetTranscoder(IAudioTranscoder? transcoder) => Transcoder = transcoder;
    }
}
