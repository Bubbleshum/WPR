using System;
// Aliased, not imported: Android.OS.Trace is also in scope here and CS0104s a bare `Trace`.
using Trace = System.Diagnostics.Trace;
using Android.Content;
using Android.OS;
using WPR.Engine.Vibration;

namespace WPR.Vibration.AndroidVibrator
{
    /// <summary>
    /// The handset's vibration motor, driven through <c>android.os.Vibrator</c>.
    ///
    /// <para>Fills <see cref="IVibrationProvider"/> and nothing else. Registered by
    /// <c>AndroidPlatform</c> declaring <c>caps.Vibration(...)</c>, which runs in the launcher
    /// process and again in <c>GameActivity</c>'s <c>:game</c> process — the game process needs
    /// its own registration because nothing static crosses that boundary, and it is the one that
    /// actually buzzes.</para>
    ///
    /// <para>Before this existed, <c>Microsoft.Devices.VibrateController.Start</c> was an empty
    /// method body, so every WP7 title that buzzed on a collision, a wrong answer or a menu tap
    /// did nothing at all — silently, on both platforms.</para>
    /// </summary>
    public sealed class AndroidVibratorProvider : IVibrationProvider
    {
        /// <summary>
        /// The longest single buzz this will issue. WP7's <c>VibrateController.Start</c> documented
        /// a 0-5 second range and threw outside it, so no title written for the platform asks for
        /// more; the cap is here so a corrupted or mis-decompiled duration cannot leave a phone
        /// running for minutes with the game already gone.
        /// </summary>
        private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(5);

        /// <summary>Guards the lazily-resolved service handle. The vibrate/cancel calls themselves
        /// are fast one-way binder calls and are made outside it.</summary>
        private readonly object _gate = new object();

        private readonly Context _context;

        private Vibrator? _vibrator;
        private bool _resolved;

        /// <param name="context">Application context, never an activity — this outlives any one
        /// screen, and in the <c>:game</c> process holding an activity would pin it for the whole
        /// game run.</param>
        public AndroidVibratorProvider(Context context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// True when the device actually has a motor. Tablets and some emulator images do not, and
        /// on those every call below is a no-op — which is the correct degradation, not an error.
        /// </summary>
        public bool IsSupported
        {
            get
            {
                try
                {
                    Vibrator? vibrator = Resolve();
                    return vibrator != null && vibrator.HasVibrator;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[wpr-vibrate] capability probe failed: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Issues one buzz, replacing anything already running.
        ///
        /// <para>Three API generations, and the differences are not cosmetic:</para>
        /// <list type="bullet">
        /// <item><b>API 26+</b> takes a <c>VibrationEffect</c>, and only there can amplitude be
        /// requested at all. Even then most motors are on/off — <c>HasAmplitudeControl</c> is what
        /// says whether the scalar means anything, and asking for a specific amplitude on a motor
        /// without it is rejected rather than rounded, hence <c>DefaultAmplitude</c> as the
        /// fallback.</item>
        /// <item><b>Below 26</b> there is only the deprecated millisecond overload and no amplitude
        /// concept whatsoever, so <paramref name="intensity"/> is honoured only as "zero means
        /// don't".</item>
        /// <item><b>API 31+</b> resolves the service differently — see <see cref="Resolve"/>.</item>
        /// </list>
        ///
        /// <para>No <c>AudioAttributes</c> / <c>VibrationAttributes</c> overload is used. Plain
        /// vibrate is what a game's own haptics want and is the widely-exercised path; declaring a
        /// usage would opt these buzzes into per-usage user settings (touch feedback, media
        /// haptics) that can silence them outright on some devices. If a device is ever reported as
        /// silent while this logs successful calls, an attributes overload is the first thing to
        /// try — but measure it there rather than adding it speculatively.</para>
        /// </summary>
        public void Vibrate(TimeSpan duration, float intensity)
        {
            if (duration <= TimeSpan.Zero || intensity <= 0f)
            {
                return;
            }

            if (duration > MaxDuration)
            {
                duration = MaxDuration;
            }

            long milliseconds = (long) duration.TotalMilliseconds;
            if (milliseconds <= 0)
            {
                return;
            }

            if (intensity > 1f)
            {
                intensity = 1f;
            }

            try
            {
                Vibrator? vibrator = Resolve();
                if (vibrator == null || !vibrator.HasVibrator)
                {
                    return;
                }

                // OperatingSystem.IsAndroidVersionAtLeast rather than the Build.VERSION.SdkInt
                // comparison used in WPR.Notifications.AndroidChannel: the two are equivalent at
                // runtime, but only this form is understood by the platform-compatibility analyzer,
                // so the newer-API calls inside need no CA1416 suppression. Deliberate divergence
                // from that file, not an oversight.
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    // 1..255. Scale from the 0..1 contract and floor at 1, so a small-but-positive
                    // intensity still buzzes rather than rounding down into silence.
                    int amplitude = vibrator.HasAmplitudeControl
                        ? Math.Max(1, Math.Min(255, (int) Math.Round(intensity * 255f)))
                        : VibrationEffect.DefaultAmplitude;

                    vibrator.Vibrate(VibrationEffect.CreateOneShot(milliseconds, amplitude));
                }
                else
                {
#pragma warning disable CA1422, CS0618 // Vibrate(long) is deprecated from API 26; below it there is nothing else.
                    vibrator.Vibrate(milliseconds);
#pragma warning restore CA1422, CS0618
                }
            }
            catch (Exception ex)
            {
                // Never let haptics take a game down. A missing VIBRATE permission surfaces here as
                // a SecurityException, which is worth seeing in the per-game log rather than
                // swallowing blind.
                Trace.WriteLine($"[wpr-vibrate] vibrate({milliseconds}ms, {intensity:F2}) failed: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancels whatever is running. Safe to call when nothing is, which is the common case —
        /// teardown and <c>OnPause</c> both call it unconditionally.
        /// </summary>
        public void Stop()
        {
            try
            {
                Resolve()?.Cancel();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-vibrate] cancel failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches the system service once and caches it, including a cached null.
        ///
        /// <para><b>API 31 moved it.</b> <c>VIBRATOR_SERVICE</c> is deprecated from S in favour of
        /// <c>VibratorManager</c>, whose <c>DefaultVibrator</c> is the handset motor (a device can
        /// now expose several). The old constant still resolves on S, but going through the manager
        /// is what keeps this correct on a device with more than one motor.</para>
        ///
        /// <para>Resolved lazily rather than in the constructor because a platform descriptor's
        /// <c>Describe</c> must be cheap and side-effect-free — it runs at composition time, more
        /// than once per process on Android.</para>
        /// </summary>
        private Vibrator? Resolve()
        {
            lock (_gate)
            {
                if (_resolved)
                {
                    return _vibrator;
                }

                _resolved = true;

                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    VibratorManager? manager =
                        _context.GetSystemService(Context.VibratorManagerService) as VibratorManager;
                    _vibrator = manager?.DefaultVibrator;
                }
                else
                {
#pragma warning disable CA1422, CS0618 // VIBRATOR_SERVICE is deprecated from API 31; the branch above covers 31+.
                    _vibrator = _context.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422, CS0618
                }

                if (_vibrator == null)
                {
                    Trace.WriteLine("[wpr-vibrate] no vibrator service on this device");
                }
                else
                {
                    string amplitude = OperatingSystem.IsAndroidVersionAtLeast(26)
                        ? _vibrator.HasAmplitudeControl.ToString()
                        : "n/a";
                    Trace.WriteLine($"[wpr-vibrate] vibrator resolved — hasVibrator={_vibrator.HasVibrator}, "
                        + $"amplitudeControl={amplitude}");
                }

                return _vibrator;
            }
        }
    }
}
