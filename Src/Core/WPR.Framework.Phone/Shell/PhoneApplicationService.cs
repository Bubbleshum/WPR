using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Stub for <c>Microsoft.Phone.Shell.PhoneApplicationService</c>. Apps register one inside
    /// <c>&lt;Application.ApplicationLifetimeObjects&gt;</c> and wire its lifecycle events
    /// (<see cref="Launching"/>, <see cref="Closing"/>, <see cref="Activated"/>, <see cref="Deactivated"/>)
    /// to handlers on App.xaml.cs. Most members return safe defaults; the events are present so
    /// XAML hookup succeeds and so WPR can fire <see cref="Launching"/> at boot if it chooses.
    /// </summary>
    public sealed class PhoneApplicationService
    {
        private static PhoneApplicationService? _Current;
        public static PhoneApplicationService? Current => _Current;

        private bool _AppActivated = false;

        static PhoneApplicationService()
        {
            _Current = new PhoneApplicationService();
        }

        public PhoneApplicationService()
        {
            _Current = this;
        }

        public IDictionary<string, object> State { get; } = new Dictionary<string, object>();

        public TimeSpan ApplicationIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
        public TimeSpan UserIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public IdleDetectionMode UserIdleDetectionMode { get; set; } = IdleDetectionMode.Enabled;
        public IdleDetectionMode ApplicationIdleDetectionMode { get; set; } = IdleDetectionMode.Enabled;

        public StartupMode StartupMode { get; internal set; } = StartupMode.Launch;

        public Version ContractVersion { get; } = new Version(8, 0);

        /// <summary>
        /// Drives the WP7 boot lifecycle from <c>ApplicationLaunch</c>. A fresh launch
        /// (<paramref name="anew"/>=true) raises <see cref="Launching"/>; a resume (anew=false)
        /// does not. <see cref="Activated"/> is raised on BOTH paths unless the caller passes
        /// <paramref name="raiseActivated"/>=<c>false</c>, and always with
        /// <see cref="ActivatedEventArgs.IsApplicationInstancePreserved"/>=<c>true</c>.
        /// <para>
        /// <b>Launching on anew=true:</b> games that build their scene graph in the Launching
        /// handler (e.g. MonstaFish) otherwise sit on a black Clear(Color.Black) forever.
        /// </para>
        /// <para>
        /// <b>Activated on a fresh launch (kept):</b> several titles key level/HUD setup off the
        /// activation signal. Star Wars: The Battle for Hoth is the concrete case — its in-game
        /// HUD only unfolds when the post-boot Activated drives the flow; drop it and the
        /// coin / command-point icons never leave alpha 0 (HUD looks empty) on a cold start even
        /// though a later resume shows them. So Activated is raised at boot AND on resume.
        /// </para>
        /// <para>
        /// <b>IsApplicationInstancePreserved is always true (not <c>!anew</c>):</b> a fresh launch
        /// has nothing to restore, so handlers that treat Activated+preserved=<c>false</c> as a
        /// tombstone-restore signal must not run that path at boot. Battlewagon's
        /// <c>Current_Activated</c> does exactly that — with preserved=false it runs
        /// <c>SystemData.ContinueGame()</c> -> <c>MapData.FromFile()</c>, which on a fresh install
        /// overwrites the <c>MapData</c> just loaded in <c>LoadContent</c> with an empty
        /// <c>new MapData()</c> (null <c>MapSeasons</c>) and jumps to the title scene, whose
        /// <c>Initialise()</c> NREs on the null array (the game sat on its animated-bomb loading
        /// screen forever). Reporting the instance as preserved makes such handlers early-out
        /// (<c>if (e.IsApplicationInstancePreserved) return;</c>) while preserved-agnostic handlers
        /// (Hoth) are unaffected. On a genuine resume (anew=false, e.g. a MediaPlayerLauncher
        /// round-trip) preserved=true is also correct — the WPR process never died.
        /// </para>
        /// <para>
        /// <b><paramref name="raiseActivated"/>=false — the per-game opt-out:</b> the boot
        /// Activated above is a WPR invention (real WP7 raises Launching only at cold start), and
        /// a title that runs its own init AND treats Activated as "re-initialise" then does that
        /// work twice. Doodle God aborts on it — see <c>GameLifecycleQuirks</c>, which is the only
        /// thing that passes false and which names the games. Never passed on the resume path:
        /// there Activated is the genuine WP7 signal.
        /// </para>
        /// </summary>
        public void HandleApplicationStart(bool anew, bool raiseActivated = true)
        {
#if DEBUG
            string raising = (anew ? "Launching" : "") + (anew && raiseActivated ? "+" : "") + (raiseActivated ? "Activated" : "");
            Trace.WriteLine($"[wpr-trace] PhoneApplicationService.HandleApplicationStart(anew={anew}) firing {raising} (preserved=true)" +
                $"{(raiseActivated ? "" : " [boot Activated suppressed for this game]")}. " +
                $"Subscribers: Launching={CountInvocations(_Launching)} Activated={CountInvocations(_Activated)}");
#endif

            if (anew)
            {
                try { _Launching?.Invoke(this, new LaunchingEventArgs()); }
                catch (Exception ex)
                {
#if DEBUG
                    Trace.WriteLine("[wpr-ex] PhoneApplicationService.Launching handler threw: " + ex);
#else
                    _ = ex;
#endif
                }
            }

            if (!raiseActivated)
            {
                // Deliberately leaves _AppActivated false. It is what the Activated `add`
                // accessor replays to a late subscriber, so setting it here would re-deliver
                // exactly the activation this call was asked to suppress — the game subscribes
                // in its ctor today, but that is its choice, not a guarantee.
                return;
            }

            // Always raise Activated (boot AND resume), but as an instance-preserved
            // (fast-resume) signal so cold-start handlers skip tombstone-restore logic.
            // See the remarks above for why this satisfies both Hoth and Battlewagon.
            try { _Activated?.Invoke(this, new ActivatedEventArgs { IsApplicationInstancePreserved = true }); }
            catch (Exception ex)
            {
#if DEBUG
                Trace.WriteLine("[wpr-ex] PhoneApplicationService.Activated handler threw: " + ex);
#else
                _ = ex;
#endif
            }

            _AppActivated = true;
        }

        public void HandleApplicationExit()
        {
            _Deactivated?.Invoke(this, new DeactivatedEventArgs { Reason = DeactivationReason.UserAction });
            _Closing?.Invoke(this, new ClosingEventArgs());

            // Recycle so the next launch starts with an empty subscriber list and
            // _AppActivated=false. ApplicationLaunch.ResetWprSingletons does the same swap
            // via reflection for the ALC-unload path.
            _Current = new PhoneApplicationService();
        }

        private static int CountInvocations(Delegate? d) => d?.GetInvocationList().Length ?? 0;

        private event EventHandler<LaunchingEventArgs>? _Launching;
        public event EventHandler<LaunchingEventArgs>? Launching
        {
            add
            {
#if DEBUG
                Trace.WriteLine("[wpr-trace] PhoneApplicationService.Launching += handler");
#endif
                _Launching += value;
            }
            remove { _Launching -= value; }
        }

        private event EventHandler<ActivatedEventArgs>? _Activated;
        public event EventHandler<ActivatedEventArgs>? Activated
        {
            add
            {
#if DEBUG
                Trace.WriteLine($"[wpr-trace] PhoneApplicationService.Activated += handler (_AppActivated={_AppActivated})");
#endif
                // Always retain the subscriber so it receives FUTURE activations — e.g. a
                // MediaPlayerLauncher round-trip re-firing Activated so a game's
                // OnGameActivated runs and completes a video (CVideoPlayer.DoPlayVideoComplete
                // -> LoadLevel). The original code only invoked-and-dropped when _AppActivated
                // was already set, which is the COMMON case (games subscribe in Initialize,
                // after ApplicationLaunch primes HandleApplicationStart) — so their handler was
                // never stored and missed every later activation, hanging Star Wars: The Battle
                // for Hoth on a black screen at level start.
                _Activated += value;

                // If the app already booted past HandleApplicationStart by the time this handler
                // attaches (the common case — games subscribe in their ctor/Initialize, AFTER
                // ApplicationLaunch primes the cold-start signal), replay it once now so the
                // handler doesn't miss it. It MUST report IsApplicationInstancePreserved=true,
                // exactly like HandleApplicationStart: on this host a cold start has nothing to
                // restore, so a tombstone-restore handler (Battlewagon's Current_Activated ->
                // LoadingScene) must early-out instead of running the restore path and NRE-ing.
                // (This used to be false; it never bit while the game and ApplicationLaunch were
                // on separate PhoneApplicationService instances, but they share one now.)
                if (_AppActivated)
                {
                    value?.Invoke(this, new ActivatedEventArgs { IsApplicationInstancePreserved = true });
                }
            }
            remove { _Activated -= value; }
        }

        private event EventHandler<ClosingEventArgs>? _Closing;
        public event EventHandler<ClosingEventArgs>? Closing
        {
            add { _Closing += value; }
            remove { _Closing -= value; }
        }

        private event EventHandler<DeactivatedEventArgs>? _Deactivated;
        public event EventHandler<DeactivatedEventArgs>? Deactivated
        {
            add { _Deactivated += value; }
            remove { _Deactivated -= value; }
        }

        public event EventHandler<RunningInBackgroundEventArgs>? RunningInBackground;

        internal void RaiseLaunching() => _Launching?.Invoke(this, new LaunchingEventArgs());
        internal void RaiseClosing() => _Closing?.Invoke(this, new ClosingEventArgs());
        internal void RaiseActivated(bool preserved) =>
            _Activated?.Invoke(this, new ActivatedEventArgs { IsApplicationInstancePreserved = preserved });
        internal void RaiseDeactivated(DeactivationReason reason) =>
            _Deactivated?.Invoke(this, new DeactivatedEventArgs { Reason = reason });
        internal void RaiseRunningInBackground() =>
            RunningInBackground?.Invoke(this, new RunningInBackgroundEventArgs());
    }
}
