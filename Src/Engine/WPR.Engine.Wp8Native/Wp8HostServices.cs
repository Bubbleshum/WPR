using WPR.Engine.Launchers;
using WPR.Engine.Vibration;

namespace WPR.Wp8Native
{
    /// <summary>
    /// What a WP8 native title reaches outside itself, wired to the platform's registries:
    /// <c>Windows.System.Launcher.LaunchUriAsync</c> to <see cref="LauncherBackend.Uri"/>, and
    /// <c>VibrationDevice</c> to <see cref="VibrationBackend.Device"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Launcher.</b> The same rule as WP7's <c>WebBrowserTask</c>
    /// (<see cref="WebLinkPolicy"/>): only http and https leave the game, and <c>vnd.youtube:</c> is
    /// rewritten. A refused URI answers false, which is what <c>LaunchUriAsync</c> reports when no app
    /// could open it, and the refusal is recorded. A platform with no launcher answers false too.</para>
    /// <para><b>Vibration.</b> Starting a buzz honours the player's switch
    /// (<see cref="VibrationBackend.IsEnabled"/>); stopping one never does, so a buzz that began while
    /// it was on cannot be stranded. The host stops the motor itself on deactivation and teardown
    /// (<see cref="StopVibration"/>), as <c>GameActivity.OnPause</c> and <c>ResetWprSingletons</c> do
    /// for WP7 titles.</para>
    /// </remarks>
    public sealed class Wp8HostServices
    {
        private readonly Action<string>? _sink;
        private readonly List<string> _log = new();

        private Wp8HostServices(Action<string>? sink) => _sink = sink;

        /// <summary>What was asked of the host and what came of it, in order.</summary>
        public IReadOnlyList<string> Log
        {
            get
            {
                lock (_log)
                {
                    return _log.ToArray();
                }
            }
        }

        /// <summary>Sets the runtime's launcher and vibration hooks to go through the platform registries.</summary>
        public static Wp8HostServices Attach(WinRtRuntime runtime, Action<string>? sink = null)
        {
            var services = new Wp8HostServices(sink);
            runtime.UriLauncher = services.OpenUri;
            runtime.VibrateHost = services.Vibrate;
            runtime.CancelVibrationHost = StopVibration;
            return services;
        }

        /// <summary><c>LaunchUriAsync</c>: true only when an http(s) link was handed to a launcher that accepted it.</summary>
        public bool OpenUri(string uri)
        {
            Uri? target = WebLinkPolicy.Normalise(uri);
            if (target is null)
            {
                Record($"launch refused '{uri}' (only http/https are opened)");
                return false;
            }

            IUriLauncher? launcher = LauncherBackend.Uri;
            if (launcher is null)
            {
                Record($"launch '{target.AbsoluteUri}': no launcher on this platform");
                return false;
            }

            try
            {
                bool opened = launcher.TryOpen(target);
                Record($"launch '{target.AbsoluteUri}' -> {(opened ? "opened" : "not opened")}");
                return opened;
            }
            catch (Exception ex)
            {
                // TryOpen must not throw; this is the belt to that brace, since the caller is the game.
                Record($"launch '{target.AbsoluteUri}': launcher threw {ex.GetType().Name}");
                return false;
            }
        }

        /// <summary><c>VibrationDevice.Vibrate</c>: the handset motor at full strength, unless the player turned vibration off.</summary>
        public void Vibrate(TimeSpan duration)
        {
            IVibrationProvider? device = VibrationBackend.Device;
            if (device is null || !device.IsSupported)
            {
                return;
            }

            if (!VibrationBackend.IsEnabled)
            {
                Record($"vibrate {duration.TotalMilliseconds:0} ms: vibration is turned off");
                return;
            }

            try
            {
                device.Vibrate(duration, 1f);
            }
            catch (Exception ex)
            {
                Record($"vibrate: provider threw {ex.GetType().Name}");
            }
        }

        /// <summary>Stops the handset motor, whatever the switch says. Also the host's teardown and deactivation stop.</summary>
        public static void StopVibration()
        {
            try
            {
                VibrationBackend.Device?.Stop();
            }
            catch (Exception)
            {
                // A stop that fails leaves nothing to undo.
            }
        }

        private void Record(string line)
        {
            lock (_log)
            {
                _log.Add(line);
            }

            _sink?.Invoke(line);
        }
    }
}
