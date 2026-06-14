using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WPR.SilverlightCompability
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
        /// Drives the WP7 boot lifecycle from <c>ApplicationLaunch</c>. WP7 fires
        /// <see cref="Launching"/> on a fresh launch (<paramref name="anew"/>=true) and
        /// <see cref="Activated"/> on resume (anew=false, IsApplicationInstancePreserved=true).
        /// Firing only Activated leaves games that initialise in their Launching handler
        /// wedged on the splash forever (e.g. MonstaFish drawing Clear(Color.Black) and
        /// nothing else, because its scene system never gets built).
        /// </summary>
        public void HandleApplicationStart(bool anew)
        {
#if DEBUG
            Trace.WriteLine($"[wpr-trace] PhoneApplicationService.HandleApplicationStart(anew={anew}) firing Launching+Activated. " +
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

            try { _Activated?.Invoke(this, new ActivatedEventArgs { IsApplicationInstancePreserved = !anew }); }
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

                // If the app already booted past HandleApplicationStart by the time this
                // handler attaches, also invoke it once now so it doesn't miss the initial
                // signal it was waiting for.
                if (_AppActivated)
                {
                    value?.Invoke(this, new ActivatedEventArgs { IsApplicationInstancePreserved = false });
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
