using WPR.Abstractions.Sensors;

namespace WPR.Sensors
{
    /// <summary>
    /// Backend registry for the WP7 sensors framework — the direct counterpart of
    /// <c>WPR.SilverlightCompability.SilverlightBackend</c> and <c>WPR.Xna.Rhi.XnaBackend</c>,
    /// and registered the same way: the launcher (the composition root) sets the platform
    /// implementation before any app is loaded, in <c>ServicesSetup.Start()</c>.
    ///
    /// <para>A settable static rather than constructor injection because the consumer is a WP7
    /// shim (<see cref="Microsoft.Devices.Sensors.Accelerometer"/>) that <em>games</em>
    /// construct — WPR never gets to pass it anything. That is the same constraint which
    /// produced the other two registries.</para>
    ///
    /// <para><b>Namespace note.</b> This sits in <c>WPR.Sensors</c>, not
    /// <c>Microsoft.Devices.Sensors</c>, even though it ships inside that assembly: the
    /// <c>Microsoft.*</c> namespaces in this project are the WP7 contract games bind by
    /// identity, and WPR's own plumbing does not belong in them. Same split as
    /// <c>WPR.Xna.Rhi</c> living inside <c>WPR.Framework.Xna</c>.</para>
    ///
    /// <para><b>Unlike <c>XnaBackend</c>, this is not cleared at teardown.</b> The registered
    /// provider is owned by the launcher and lives in its default ALC, so it cannot pin a
    /// per-game ALC; clearing it would leave the second game of a session with no sensors.
    /// What <em>is</em> per-game is the subscriber list hanging off the provider's event, and
    /// that is what <see cref="ISensorProvider.ResetForNewLaunch"/> drops — see
    /// <c>ApplicationLaunch.ResetWprSingletons</c>.</para>
    ///
    /// <para>Null is the supported default. <see cref="Microsoft.Devices.Sensors.Accelerometer"/>
    /// treats "no provider registered" as "no readings" rather than throwing, matching how
    /// GamerServices degrades with no achievement store.</para>
    /// </summary>
    public static class SensorBackend
    {
        /// <summary>
        /// The registered platform sensor provider, or null when none is composed in.
        /// Set by the launcher; see <c>WPR.Platform.Windows.ServicesSetup</c> and
        /// <c>WPR.Platform.Android.ServicesSetup</c>.
        /// </summary>
        public static ISensorProvider? Provider { get; private set; }

        /// <summary>
        /// Installs the platform provider. Idempotent by assignment — a head that re-runs
        /// <c>ServicesSetup.Start()</c> (Android recreates the process straight into any
        /// activity) replaces rather than accumulates.
        ///
        /// <para><b>The outgoing provider is reset on the way out.</b> Nothing else would ever
        /// reach it again — teardown only resets whatever is registered <em>now</em> — so a
        /// displaced provider would keep its subscriber list, and a game handler on that list
        /// pins the game's collectible ALC exactly as if the reset had been skipped. Both heads
        /// call this once per process today, so this is closing a hole rather than fixing a
        /// live bug; it is here so that stays true if a head ever re-composes.</para>
        /// </summary>
        public static void SetProvider(ISensorProvider? provider)
        {
            ISensorProvider? previous = Provider;
            Provider = provider;

            if (previous != null && !ReferenceEquals(previous, provider))
            {
                try
                {
                    previous.ResetForNewLaunch();
                }
                catch
                {
                    // Composition must not fail because a retired provider objected to being
                    // shut down.
                }
            }
        }
    }
}
