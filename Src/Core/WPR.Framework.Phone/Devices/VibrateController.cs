using System;
using WPR.Engine.Vibration;

namespace Microsoft.Devices
{
    /// <summary>
    /// Shim for <c>Microsoft.Devices.VibrateController</c> — the WP7 handset vibration API, and the
    /// only way a Silverlight or XNA title on this platform could buzz the phone.
    ///
    /// <para>Both methods were empty until 2026-09-05, so every game that vibrated on a collision,
    /// a wrong answer or a menu tap did nothing at all. They now go through
    /// <see cref="VibrationBackend"/>, which a platform head fills by declaring
    /// <c>caps.Vibration(...)</c>. Android supplies the device's motor; Windows declares none, so
    /// the desktop behaviour is unchanged — null there means "this platform has no motor", not an
    /// error.</para>
    /// </summary>
    public class VibrateController
    {
        private static VibrateController? _Default;

        public static VibrateController Default
        {
            get
            {
                if (_Default == null)
                {
                    _Default = new VibrateController();
                }

                return _Default;
            }
        }

        /// <summary>
        /// Vibrates for <paramref name="duration"/>.
        ///
        /// <para><b>Out-of-range durations are clamped, not thrown on.</b> Real WP7 documented a
        /// 0-5 second range and raised <c>ArgumentOutOfRangeException</c> outside it, but this
        /// method has silently accepted anything for as long as it has existed — so a title that
        /// somehow passes a bad value has never crashed here, and making it start crashing now
        /// would be a regression dressed as fidelity. Titles actually written for the phone cannot
        /// hit the clamp: they would have crashed on the real device. The provider caps duration
        /// again on its own side, since it is what the OS call is made from.</para>
        ///
        /// <para>Full intensity: the WP7 API has no amplitude concept at all. The scalar exists on
        /// the seam for a controller-rumble implementation, which does.</para>
        /// </summary>
        public void Start(TimeSpan duration)
        {
            // The global switch, off by the user's choice on the settings page. Silently doing
            // nothing is right: WP7 gave a game no way to ask whether vibration was available, so
            // there is nothing to report to and every title already tolerates a buzz it cannot
            // feel.
            if (!VibrationBackend.IsEnabled) return;

            VibrationBackend.Device?.Vibrate(duration, 1f);
        }

        /// <summary>Stops a vibration early. A no-op when nothing is running, or when the platform
        /// registered no provider.</summary>
        public void Stop()
        {
            VibrationBackend.Device?.Stop();
        }
    }
}
