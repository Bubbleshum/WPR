using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using WPR.Common;
using WPR.Models;

namespace WPR
{
    /// <summary>
    /// A copy of each game's tile icon kept in the app's own data store, outside the per-game
    /// install folder, so it outlives an uninstall.
    ///
    /// <para><b>Why this exists.</b> <see cref="Application.IconPath"/> points inside
    /// <c>AppData/&lt;ProductId&gt;/</c>, which <see cref="ApplicationInstaller.UninstallAsync"/>
    /// deletes — and the <c>Application</c> row that named it goes with it. The achievements pages
    /// outlive both: achievement rows are keyed by <c>OwnProductId</c> and are deliberately kept
    /// after uninstall so a player's gamerscore survives. So an uninstalled game's row on the
    /// achievements list fell back to the placeholder tile, while its per-achievement icons kept
    /// rendering — those live under <c>Database/Achievements/&lt;ProductId&gt;/</c>, which is app
    /// data and was never touched by uninstall. This puts the game icon on the same footing.</para>
    ///
    /// <para><b>Layout.</b> <c>GameIcons/&lt;ProductId&gt;&lt;ext&gt;</c> under
    /// <see cref="Configuration.DataStorePath"/>, keeping the source file's extension rather than
    /// re-encoding: WP7 tiles are usually PNG but a manifest may name a JPG, and neither this
    /// project nor the Android head has an imaging library to normalise with. Relative paths use
    /// forward slashes for the same reason <see cref="HardcodedAchievementCatalogue"/> does — they
    /// are handed to <see cref="Configuration.DataPath"/> on both heads.</para>
    ///
    /// <para><b>Lifetime.</b> Entries are written on install, refreshed on repatch, and captured
    /// one last time on uninstall — that last call is what backfills games installed before this
    /// existed, so no migration step is needed. Nothing removes them: an icon is a few KB and its
    /// whole purpose is to outlive the game. A reinstall overwrites, so a game that changes its
    /// tile still updates.</para>
    /// </summary>
    public static class GameIconStore
    {
        /// <summary>Store folder, relative to <see cref="Configuration.DataStorePath"/>.</summary>
        public const string StoreRoot = "GameIcons";

        private static string StoreFolder => Configuration.Current!.DataPath(StoreRoot);

        /// <summary>
        /// Copy <paramref name="app"/>'s installed icon into the store, overwriting any previous
        /// copy. Best-effort and silent when the app has no icon or the file is already gone —
        /// every caller is an install or uninstall path that must not fail over a thumbnail.
        /// </summary>
        public static void Capture(Application? app)
        {
            if (app == null) return;
            Capture(app.ProductId, app.IconPath);
        }

        /// <summary>
        /// Copy the icon at <paramref name="installedIconRelativePath"/> (relative to the data
        /// store, i.e. an <see cref="Application.IconPath"/> value) into the store under
        /// <paramref name="productId"/>.
        /// </summary>
        public static void Capture(string? productId, string? installedIconRelativePath)
        {
            string? trimmed = Normalise(productId);
            if (trimmed == null || string.IsNullOrWhiteSpace(installedIconRelativePath)) return;

            try
            {
                string source = Configuration.Current!.DataPath(installedIconRelativePath!);
                if (!File.Exists(source)) return;

                string destination = Path.Combine(StoreFolder, trimmed + Path.GetExtension(source));

                // The repatch and uninstall calls re-run over games the install path has already
                // covered, so skip rather than rewrite an identical file on every pass.
                if (PathsEqual(source, destination)) return;

                Directory.CreateDirectory(StoreFolder);

                // A previous capture may have used a different extension (a reinstall from a
                // repackaged XAP) or a different casing of the product id. Drop those, or Find()
                // could resolve to the stale one — and on Android's case-sensitive filesystem a
                // case-variant would sit beside the new file rather than being overwritten by it.
                // Ordinal, so a case-variant counts as stale rather than as the destination.
                foreach (string stale in EnumerateCandidates(trimmed))
                {
                    if (string.Equals(Path.GetFullPath(stale), Path.GetFullPath(destination), StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try { File.Delete(stale); } catch { /* best-effort */ }
                }

                File.Copy(source, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall,
                    $"GameIconStore: could not cache the icon for {productId}: {ex.Message}");
            }
        }

        /// <summary>
        /// The stored icon's path relative to the data store, or null when nothing was captured
        /// for this product. Feed the result to <see cref="Configuration.DataPath"/>.
        /// </summary>
        public static string? Find(string? productId)
        {
            string? trimmed = Normalise(productId);
            if (trimmed == null) return null;

            try
            {
                string? file = EnumerateCandidates(trimmed).FirstOrDefault();
                return file == null ? null : $"{StoreRoot}/{Path.GetFileName(file)}";
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall,
                    $"GameIconStore: could not read the icon cache for {productId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The icon to draw for a product: the live install's icon while the game is installed,
        /// otherwise the stored copy. Both are paths relative to the data store; null means no
        /// icon exists and the caller should fall back to its own placeholder.
        ///
        /// <para>Preferring the install keeps installed games rendering exactly what they did
        /// before, and lets a reinstall that changes the tile take effect immediately.</para>
        /// </summary>
        public static string? Resolve(string? productId, string? installedIconRelativePath)
        {
            if (!string.IsNullOrWhiteSpace(installedIconRelativePath))
            {
                try
                {
                    if (File.Exists(Configuration.Current!.DataPath(installedIconRelativePath!)))
                    {
                        return installedIconRelativePath;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"GameIconStore: could not probe the installed icon for {productId}: {ex.Message}");
                }
            }

            return Find(productId);
        }

        /// <summary>
        /// Every stored file for this product — normally one, but a capture whose extension
        /// changed can briefly leave two.
        ///
        /// <para>Filtered in managed code rather than by handing the product id to
        /// <see cref="Directory.EnumerateFiles(string, string)"/> as a pattern, because that
        /// pattern matches case-insensitively on Windows and case-sensitively on Android — the
        /// exact shape of bug that works on the desktop head and not on the phone. Product ids
        /// reach the two ends of this from different tables (<c>Application.ProductId</c> when
        /// captured, <c>Achievement.OwnProductId</c> when looked up), which is why both shells
        /// already compare them <see cref="StringComparer.OrdinalIgnoreCase"/>.</para>
        /// </summary>
        private static IEnumerable<string> EnumerateCandidates(string trimmedProductId)
        {
            string folder = StoreFolder;
            if (!Directory.Exists(folder)) return Array.Empty<string>();

            return Directory
                .EnumerateFiles(folder)
                .Where(f => string.Equals(
                    Path.GetFileNameWithoutExtension(f), trimmedProductId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Product ids reach us both braced (<c>{...}</c>, straight off a manifest) and bare (as
        /// stored on <see cref="Application.ProductId"/> and <c>Achievement.OwnProductId</c>).
        /// One filename per game either way.
        /// </summary>
        private static string? Normalise(string? productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return null;
            string trimmed = productId!.Trim().Trim('{').Trim('}');
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
