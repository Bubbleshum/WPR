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

        private bool _Launched;

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
        /// Drives the phone's lifecycle from <c>ApplicationLaunch</c>, as Windows Phone raised it.
        /// A cold start (<paramref name="anew"/>=true) raises <see cref="Launching"/> and nothing
        /// else, once per service: a second cold start on the same service does nothing. A resume
        /// (anew=false: the app came back to the front, or a <c>MediaPlayerLauncher</c> movie
        /// ended) raises <see cref="Activated"/> with
        /// <see cref="ActivatedEventArgs.IsApplicationInstancePreserved"/>=<c>true</c>, a dormant
        /// resume: the process never died, so there is no tombstone to restore.
        /// <para>
        /// Until 2026-10-27 WPR also raised Activated at a cold start (preserved=true, to keep
        /// tombstone-restore handlers away) and raised the cold start twice, once before
        /// <c>Game.Run</c>. The phone did neither (MSDN jj206947, on 7.x and 8.0 alike), and several
        /// titles broke on it and were then listed by name: Doodle God ran its full initialisation
        /// a second time, Chickens Can't Fly took the boot Activated for a fast app switch and skipped
        /// loading its saved state, AE 3D Motor restored a tombstone it never saved, and iRunner's
        /// analytics started a second session. The titles that seemed to need the boot Activated did
        /// not: Star Wars: The Battle for Hoth's handler only completes a playing video (the
        /// launcher movie's resume does that), and Battlewagon's ignores a preserved activation.
        /// </para>
        /// </summary>
        public void HandleApplicationStart(bool anew)
        {
#if DEBUG
            Trace.WriteLine($"[wpr-trace] PhoneApplicationService.HandleApplicationStart(anew={anew}) " +
                (anew ? (_Launched ? "ignored: Launching was already raised" : "firing Launching") : "firing Activated (preserved=true)") +
                $". Subscribers: Launching={CountInvocations(_Launching)} Activated={CountInvocations(_Activated)}");
#endif

            if (anew)
            {
                if (_Launched)
                {
                    return;
                }

                _Launched = true;
                try { _Launching?.Invoke(this, new LaunchingEventArgs()); }
                catch (Exception ex)
                {
#if DEBUG
                    Trace.WriteLine("[wpr-ex] PhoneApplicationService.Launching handler threw: " + ex);
#else
                    _ = ex;
#endif
                }

                return;
            }

            try { _Activated?.Invoke(this, new ActivatedEventArgs { IsApplicationInstancePreserved = true }); }
            catch (Exception ex)
            {
#if DEBUG
                Trace.WriteLine("[wpr-ex] PhoneApplicationService.Activated handler threw: " + ex);
#else
                _ = ex;
#endif
            }
        }

        public void HandleApplicationExit()
        {
            _Deactivated?.Invoke(this, new DeactivatedEventArgs { Reason = DeactivationReason.UserAction });
            _Closing?.Invoke(this, new ClosingEventArgs());

            // Recycle so the next launch starts with an empty subscriber list and has not
            // launched yet. ApplicationLaunch.ResetWprSingletons does the same swap
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
                Trace.WriteLine("[wpr-trace] PhoneApplicationService.Activated += handler");
#endif
                // Retained for every FUTURE activation (a resume, e.g. a MediaPlayerLauncher movie
                // ending, which is what continues Star Wars: The Battle for Hoth into its level).
                // Nothing is replayed to a late subscriber: a cold start raises no Activated.
                _Activated += value;
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
