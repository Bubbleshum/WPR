using System;

namespace WPR.Platform.Android
{
    /// <summary>
    /// Is this an emulator? Build-field heuristics, because there is no supported API for it —
    /// <c>ro.kernel.qemu</c> is not readable from managed code without reflection.
    ///
    /// <para>Split out of the old <c>Graphics.GraphicsDriverPolicy</c> when the engine tier landed.
    /// The rest of that class — reading the override file, turning a choice into a driver name —
    /// was platform-independent and moved to <c>WPR.Engine.Graphics</c>. This part cannot: it reads
    /// <c>Android.OS.Build</c>, so it belongs to the head, and the head declares its
    /// <em>conclusion</em> to the engine rather than the rule.</para>
    ///
    /// <para><b>Biased to false negatives, deliberately.</b> A missed emulator only means the
    /// emulator renders nothing; a false positive puts a real phone back on the Vulkan driver that
    /// T-poses every <c>SkinnedEffect</c> character. Keep it that way round.</para>
    /// </summary>
    internal static class AndroidDeviceKind
    {
        /// <summary>
        /// The conventional heuristic set: emulator kernels are <c>goldfish</c>/<c>ranchu</c>,
        /// Cuttlefish is <c>cutf_cvm</c>, and AVD images brand themselves <c>generic</c>/<c>sdk_*</c>.
        /// </summary>
        internal static bool IsEmulator()
        {
            try
            {
                string hardware = (global::Android.OS.Build.Hardware ?? string.Empty).ToLowerInvariant();
                string product = (global::Android.OS.Build.Product ?? string.Empty).ToLowerInvariant();
                string model = (global::Android.OS.Build.Model ?? string.Empty).ToLowerInvariant();
                string brand = (global::Android.OS.Build.Brand ?? string.Empty).ToLowerInvariant();
                string device = (global::Android.OS.Build.Device ?? string.Empty).ToLowerInvariant();
                string fingerprint = (global::Android.OS.Build.Fingerprint ?? string.Empty).ToLowerInvariant();

                return hardware is "goldfish" or "ranchu" or "cutf_cvm"
                    || product.StartsWith("sdk", StringComparison.Ordinal)
                    || product.Contains("emulator", StringComparison.Ordinal)
                    || product.Contains("simulator", StringComparison.Ordinal)
                    || model.Contains("emulator", StringComparison.Ordinal)
                    || model.Contains("android sdk built for", StringComparison.Ordinal)
                    || device.StartsWith("emulator", StringComparison.Ordinal)
                    || device.StartsWith("generic", StringComparison.Ordinal)
                    || fingerprint.StartsWith("generic", StringComparison.Ordinal)
                    || (brand.StartsWith("generic", StringComparison.Ordinal)
                        && device.StartsWith("generic", StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                WPR.Common.Log.Warn(WPR.Common.LogCategory.AppList,
                    $"[wpr-gfx] emulator detection failed ({ex.GetType().Name}); assuming a real device");
                return false;
            }
        }
    }
}
