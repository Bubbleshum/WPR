using System;
using System.IO;

namespace WPR.Platform.Android.Graphics
{
    /// <summary>
    /// Decides which FNA3D driver this device should use, and applies it through
    /// <see cref="WPR.Backend.FNA.GraphicsDriverSelection"/> before the game creates its device.
    ///
    /// <para><b>The problem this solves.</b> <c>fna3d.env</c> forces <c>OpenGL</c> for the whole
    /// process, because the Vulkan driver mistranslates <c>SkinnedEffect</c> and T-poses every
    /// animated character on real hardware. But the Android emulator cannot run the OpenGL path:
    /// its host GL translator leaves the game's own clear colour on screen and draws nothing. So a
    /// single build-time answer is wrong for one of the two — hence a runtime decision.</para>
    ///
    /// <para><b>Failure direction is deliberate.</b> The env var stays as the default, and hardware
    /// takes the "do nothing" branch. If this class never runs, or its detection is wrong in the
    /// conservative direction, a phone still gets OpenGL — the outcome we know is correct. Only the
    /// emulator, and an explicit opt-in, ever loosen it. Getting that backwards would silently
    /// reintroduce the T-pose on real devices.</para>
    /// </summary>
    internal static class GraphicsDriverPolicy
    {
        /// <summary>
        /// Drop a file with this name next to the app's external files (alongside
        /// <c>AppData/</c>) containing <c>OpenGL</c>, <c>Vulkan</c> or <c>auto</c> to override the
        /// choice without a rebuild. Exists for triaging a phone whose GL driver misbehaves — the
        /// alternative is asking the user to wait for a new APK to test a one-word change:
        /// <code>adb shell "echo Vulkan > /storage/emulated/0/Android/data/com.wpr.android/files/fna3d_driver.txt"</code>
        /// </summary>
        private const string OverrideFileName = "fna3d_driver.txt";

        /// <summary>
        /// Applies the driver choice. Call at the start of the game thread, before the host builds
        /// the game — the hint is read once inside <c>FNA3D_PrepareWindowAttributes</c>.
        /// </summary>
        /// <param name="externalFilesDir">The app's external files directory, or null if unknown.</param>
        public static void Apply(string? externalFilesDir)
        {
            string? requested = ReadOverride(externalFilesDir);
            if (requested != null)
            {
                WPR.Common.Log.Info(WPR.Common.LogCategory.AppList,
                    $"[wpr-gfx] {OverrideFileName} requests driver '{requested}'");
                WPR.Backend.FNA.GraphicsDriverSelection.Apply(requested);
                return;
            }

            if (IsEmulator())
            {
                /* Automatic selection, not "Vulkan" by name: FNA3D's order already offers OpenGL
                 * first and falls through, so "auto" stays correct if an emulator image ever gains
                 * a working GL translator, and it does not hard-fail on an image built without the
                 * Vulkan driver. */
                /* Hoisted: `global::` is not usable inside an interpolated-string hole, and inside
                 * namespace WPR.Platform.Android the bare identifier `Android` binds to this
                 * namespace rather than the Mono.Android root. */
                string hardware = global::Android.OS.Build.Hardware ?? "?";
                string model = global::Android.OS.Build.Model ?? "?";

                WPR.Common.Log.Info(WPR.Common.LogCategory.AppList,
                    $"[wpr-gfx] emulator detected ({hardware} / {model}); using automatic driver " +
                    "selection so the game renders here. Real devices keep the forced OpenGL path.");
                WPR.Backend.FNA.GraphicsDriverSelection.Apply(null);
                return;
            }

            /* Real device: leave fna3d.env's force in place, untouched. */
            WPR.Common.Log.Info(WPR.Common.LogCategory.AppList,
                "[wpr-gfx] physical device; keeping the forced OpenGL driver from fna3d.env");
        }

        private static string? ReadOverride(string? externalFilesDir)
        {
            if (string.IsNullOrEmpty(externalFilesDir))
            {
                return null;
            }

            try
            {
                string path = Path.Combine(externalFilesDir!, OverrideFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                string content = File.ReadAllText(path).Trim();
                return content.Length == 0 ? null : content;
            }
            catch (Exception ex)
            {
                /* An unreadable override must not stop a game launching. */
                WPR.Common.Log.Warn(WPR.Common.LogCategory.AppList,
                    $"[wpr-gfx] could not read {OverrideFileName}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Build-field heuristics for the Android emulator. There is no supported API for this —
        /// <c>ro.kernel.qemu</c> is not readable from managed code without reflection — so this is
        /// the conventional set: the emulator kernels are <c>goldfish</c>/<c>ranchu</c>, Cuttlefish
        /// is <c>cutf_cvm</c>, and the AVD images brand themselves <c>generic</c>/<c>sdk_*</c>.
        ///
        /// <para>Biased towards false negatives on purpose: a missed emulator only means the
        /// emulator keeps rendering nothing, while a false positive would put a real phone back on
        /// the T-posing Vulkan driver.</para>
        /// </summary>
        private static bool IsEmulator()
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
