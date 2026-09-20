using System;
using System.Collections.Generic;

namespace WPR.Engine.GameLoop
{
    /// <summary>
    /// Per-game deviations from the WP7 application lifecycle <see cref="ApplicationLaunch"/>
    /// synthesises at launch. Keyed by ProductId, consulted once per launch.
    ///
    /// <para><b>Why a table and not a fix.</b> WPR raises <c>Activated</c> at cold start, which
    /// real WP7 never does — there, a fresh launch raises <c>Launching</c> only and
    /// <c>Activated</c> means "resumed from dormant/tombstoned". It is raised here because
    /// several titles key their level/HUD setup off the activation signal and show nothing
    /// without it (Star Wars: The Battle for Hoth, Battlewagon — see the remarks on
    /// <see cref="Microsoft.Phone.Shell.PhoneApplicationService.HandleApplicationStart"/>).
    /// A minority of titles do their own cold-start init <em>and</em> treat <c>Activated</c> as
    /// "re-initialise everything", so the synthetic one makes them run that init twice. Both
    /// groups subscribe to <c>Launching</c> and <c>Activated</c>, so nothing about a game's
    /// subscriptions tells them apart — hence a list of names rather than a rule.</para>
    /// </summary>
    internal static class GameLifecycleQuirks
    {
        /// <summary>
        /// Games for which the synthetic cold-start <c>Activated</c> is NOT raised. They still
        /// get <c>Launching</c> at boot and <c>Activated</c> on a genuine resume (the
        /// <c>MediaPlayerLauncher</c> round-trip), which is exactly WP7's own contract.
        /// </summary>
        private static readonly HashSet<string> SuppressBootActivatedProducts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Doodle God (34e0f2e7…). Its Activated handler is DoodleGame.ᜁ(), the full
                // asset + localisation init — which the game also runs itself, on its own
                // loading thread, once the two splash screens have shown. Every extra run
                // re-parses Content/data/loc/elements.txt into the same Dictionary, so the
                // second one throws "An item with the same key has already been added. Key:
                // Adventurers" out of Settings.LoadElementsLoc. Caught when it happens on the
                // game thread, fatal when it happens on the loading thread: the process aborts
                // ~18 frames in, on the second splash, on both heads.
                "34e0f2e7-7bc7-41c0-9431-399e7ceddd2f",

                // Chickens Can't Fly (bd6d46cf…). Unlike Doodle God this one is not about
                // running init twice — it is about WHICH init runs. FallingGame.pas_Activated
                // reads ActivatedEventArgs.IsApplicationInstancePreserved, which WPR reports as
                // true at boot (deliberately — see HandleApplicationStart), and concludes it is
                // resuming a fast-app-switch: it sets GameComponents.StartupMode to
                // FastAppSwitching. LogoLoadingScreen.LoadGame() then takes the branch that
                // skips GameComponents.LoadStateFromDisk(), so GameComponents.TombstonedState
                // is still null when LoadContent() -> ReactivateStaticData() ->
                // ValueItemId.OnActivated() dereferences it. The NRE unwinds
                // LoadComponentsAndScreens(), the loading screen never finishes, and its own
                // DrawingThread parks on a WaitHandle forever: a black window, a game loop
                // still ticking, and nothing in the log after the first 30 traced ticks.
                //
                // Suppressing the boot Activated leaves StartupMode at its NewInstance field
                // initialiser, which is the branch that loads the state this game then reads.
                // Note the preserved=true flag this game trips over is the very thing
                // Battlewagon needs, so the flag cannot be changed to !anew — the two titles
                // want opposite answers from one signal, which is what this table is for.
                "bd6d46cf-4177-4de0-93c3-610f450fc403",
            };

        /// <summary>
        /// True when <paramref name="productId"/> must not receive the synthetic cold-start
        /// <c>Activated</c>. Braces are tolerated on either side: the catalogue stores ids
        /// trimmed, a manifest carries them wrapped.
        /// </summary>
        public static bool SuppressesBootActivated(string? productId) =>
            productId != null && SuppressBootActivatedProducts.Contains(Normalise(productId));

        private static string Normalise(string productId) => productId.Trim().Trim('{', '}');
    }
}
