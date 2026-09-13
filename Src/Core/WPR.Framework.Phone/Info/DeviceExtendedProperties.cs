using System;
using System.Diagnostics;

namespace Microsoft.Phone.Info
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Info.DeviceExtendedProperties</c>.
    ///
    /// <para><b>An unknown key throws <see cref="ArgumentOutOfRangeException"/>; it does NOT return
    /// null.</b> That is what real WP7 does, and games are written against it — they wrap
    /// <see cref="GetValue"/> in <c>catch (ArgumentOutOfRangeException)</c> and fall back. Returning
    /// null instead turns the game's own <c>unbox.any</c> into a NullReferenceException that sails
    /// straight past that catch, so a title which degrades gracefully on real hardware dies here
    /// instead. Contre Jour was the reference case: <c>Mokus2D.Util.HardwareCapabilities</c> reads
    /// <c>ApplicationWorkingSetLimit</c> inside exactly such a catch, and the NRE killed
    /// <c>ContreJourApplication..ctor</c> before the first frame.</para>
    ///
    /// <para><see cref="TryGetValue"/> is the safe variant and never throws — 56 call sites across
    /// the library pass it a computed (non-literal) key, i.e. they are probing.</para>
    ///
    /// <para><b>The key names are the WP7 ones, which are not the <see cref="DeviceStatus"/> ones.</b>
    /// The per-app memory cap is <c>ApplicationWorkingSetLimit</c> here and
    /// <c>ApplicationMemoryUsageLimit</c> on <see cref="DeviceStatus"/>; only the latter used to be
    /// implemented, so every one of the 38 call sites in the library that asks this type for the cap
    /// got null. If you add a key, check a real game's IL for the spelling rather than guessing from
    /// the sibling API.</para>
    /// </summary>
    public class DeviceExtendedProperties
    {
        /// <summary>
        /// The screen WPR reports to games. Deliberately the same 480x800 that
        /// <c>WPR.Xna.Compat.GraphicsAdapter.CurrentDisplayMode</c> and
        /// <c>WPR.Xna.Compat.GraphicsDevice.DisplayMode</c> hand out: a game that asks for its
        /// screen size two ways must not get two answers.
        /// </summary>
        private const double ScreenWidth = 480;

        /// <summary>See <see cref="ScreenWidth"/>.</summary>
        private const double ScreenHeight = 800;

        /// <summary>
        /// Dots per inch implied by <see cref="ScreenWidth"/>x<see cref="ScreenHeight"/> on the
        /// 3.7-inch panel WP7's chassis spec required: sqrt(480^2 + 800^2) / 3.7 is about 252.
        /// Derived rather than picked, so it stays consistent if the reported resolution changes.
        /// </summary>
        private const double RawDpi = 252.0;

        /// <summary>
        /// Never throws. Returns false (and null) for a key this shim does not implement, matching
        /// WP7 — games use this to probe for keys that only exist on later OS versions.
        /// </summary>
        public static bool TryGetValue(string propertyName, out Object? propertyValue)
        {
            return TryResolve(propertyName, out propertyValue);
        }

        /// <summary>
        /// Throws <see cref="ArgumentOutOfRangeException"/> for an unknown key, as WP7 does.
        /// See the note on the class about why this must not return null.
        /// </summary>
        public static Object GetValue(string property)
        {
            if (!TryResolve(property, out Object? value) || value == null)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(property), property,
                    "Unsupported device property. (WPR shim: Microsoft.Phone.Info.DeviceExtendedProperties)");
            }

            return value;
        }

        private static bool TryResolve(string property, out Object? value)
        {
            switch (property)
            {
                case "DeviceManufacturer":
                    value = "WPRunner";
                    return true;

                case "DeviceName":
                    value = "WPRunner 2022";
                    return true;

                case "DeviceFirmwareVersion":
                case "DeviceHardwareVersion":
                    value = "8.0.0";
                    return true;

                case "DeviceTotalMemory":
                    value = 2048L * 1024 * 1024;
                    return true;

                // Per-app memory counters — WP7 games commonly read these for crash
                // telemetry (e.g. PressPlay.Tentacles.MetricsSender.CreateTearDownExtendedKeys
                // calls .ToString() on the value, so returning null here NREs the host on
                // game exit). Long, in bytes, matching the WP7 SDK shape.
                case "ApplicationCurrentMemoryUsage":
                    try { value = Process.GetCurrentProcess().WorkingSet64; }
                    catch { value = GC.GetTotalMemory(false); }
                    return true;

                case "ApplicationPeakMemoryUsage":
                    try { value = Process.GetCurrentProcess().PeakWorkingSet64; }
                    catch { value = GC.GetTotalMemory(false); }
                    return true;

                // The per-app cap, in bytes. "ApplicationWorkingSetLimit" is the real WP7 spelling
                // and is what 38 call sites across the library use; "ApplicationMemoryUsageLimit"
                // is DeviceStatus's name for the same quantity, accepted here only because this
                // shim answered to it before the real one existed.
                //
                // WP7's hard cap was 90 MB on lowmem devices and 180 MB on full-RAM ones. Report
                // the higher value so games that gate quality on it light everything up: Contre
                // Jour's HardwareCapabilities.IsLowMemoryDevice is literally `limit < 94371840`.
                case "ApplicationWorkingSetLimit":
                case "ApplicationMemoryUsageLimit":
                    value = 180L * 1024 * 1024;
                    return true;

                // Boxed as WPR.SilverlightCompability.Size, which is what the patcher rewrites the
                // game's `unbox.any System.Windows.Size` to name. 49 call sites in the library.
                case "PhysicalScreenResolution":
                    value = new global::WPR.SilverlightCompability.Size(ScreenWidth, ScreenHeight);
                    return true;

                // Doubles. Games unbox these as System.Double (29 sites) or Nullable<double>
                // (10 sites); a boxed double satisfies both.
                case "RawDpiX":
                case "RawDpiY":
                    value = RawDpi;
                    return true;

                // No SIM, so no operator. WP7 returns an empty string on an unbranded device
                // rather than null, and the two games that read it (both Tetris Blitz builds)
                // feed it straight into string handling.
                case "OriginalMobileOperatorName":
                    value = string.Empty;
                    return true;

                case "DeviceUniqueId":
                    // WP7 returns a 20-byte anonymous per-device hash here. Games cast the
                    // result straight to byte[] and index it (RISK's MpWiFiNetwork..ctor does
                    // `mDeviceUniqueID = (byte[])GetValue("DeviceUniqueId"); mDeviceUniqueID[0] = 0;`)
                    // — returning null NREs them mid-init, and because RISK swallows the throw
                    // in XNAGame.Update's catch{} the game hangs on a black logo screen forever.
                    // Return a stable, machine-specific 20-byte id (SHA-1 is exactly 20 bytes).
                    value = System.Security.Cryptography.SHA1.HashData(
                        System.Text.Encoding.UTF8.GetBytes("WPR-DeviceUniqueId:" + Environment.MachineName));
                    return true;

                default:
                    value = null;
                    return false;
            }
        }
    }
}
