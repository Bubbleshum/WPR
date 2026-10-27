using System;
using System.Diagnostics;
using System.IO;

using WPR.Common;
using WPR.Models;

namespace WPR.Achievements
{
    /// <summary>
    /// Achievement catalogues WPR builds on this device from an installed package, for a game it
    /// ships no catalogue for (<see cref="PackageAchievementCatalogue"/>). They live in the data
    /// store at <c>Database/GeneratedAchievements/&lt;productid&gt;/</c>, in the bundled format,
    /// beside (never inside) the bundled <c>Database/Achievements</c> tree, which the heads
    /// overwrite-copy at every start.
    /// </summary>
    /// <remarks>
    /// <para>A bundled catalogue with entries always wins: nothing is generated for it, and
    /// <see cref="HardcodedAchievementCatalogue.Load"/> reads it in preference. An empty bundled
    /// manifest (a title WPR knows has no achievements through <c>SignedInGamer</c>) still lets a
    /// generated one be used, since the package may say otherwise.</para>
    ///
    /// <para>A package that yields nothing still gets a manifest with no entries, so it is scanned
    /// once per <see cref="PackageAchievementCatalogue.GeneratorVersion"/>, not at every start.</para>
    /// </remarks>
    public static class GeneratedAchievementCatalogues
    {
        public const string Root = "Database/GeneratedAchievements";

        /// <summary>The folder a product's generated catalogue lives in.</summary>
        public static string Directory(string productId) =>
            Configuration.Current!.DataPath(Path.Combine(Root, Normalise(productId)));

        public static string Normalise(string productId) => productId.Trim().Trim('{', '}').ToLowerInvariant();

        /// <summary>The install folder of a product, where its package was extracted.</summary>
        public static string InstallFolder(string productId) =>
            Path.Combine(Configuration.Current!.DataPath(Application.DataStoreFolder), productId.Trim().Trim('{', '}'));

        /// <summary>True when this product's achievements will come from a generated catalogue.</summary>
        public static bool NeedsOne(string productId) =>
            !string.IsNullOrWhiteSpace(productId) && !HardcodedAchievementCatalogue.HasBundledAchievements(productId);

        /// <summary>
        /// Builds and writes the catalogue for an installed product when it has no bundled one and
        /// no catalogue from the current generator. Returns true when a catalogue was (re)written.
        /// Never throws: a package that cannot be read is logged and leaves things as they were.
        /// </summary>
        public static bool EnsureFor(string productId, string? installFolder = null)
        {
            try
            {
                if (!NeedsOne(productId) || Configuration.Current is null) return false;
                string target = Directory(productId);
                if (PackageAchievementCatalogue.WrittenVersion(target) >= PackageAchievementCatalogue.GeneratorVersion) return false;

                installFolder ??= InstallFolder(productId);
                if (!System.IO.Directory.Exists(installFolder)) return false;

                Stopwatch watch = Stopwatch.StartNew();
                GeneratedCatalogue catalogue;
                using (DirectoryPackageFiles package = new(installFolder))
                {
                    catalogue = PackageAchievementCatalogue.Build(package);
                }
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                PackageAchievementCatalogue.Write(catalogue, target, Normalise(productId));
                HardcodedAchievementCatalogue.Forget(productId);

                int named = 0, icons = 0;
                foreach (GeneratedAchievement a in catalogue.Achievements)
                {
                    if (!a.NameFromKey) named++;
                    if (a.Icon is not null) icons++;
                }
                Log.Info(LogCategory.AppInstall,
                    $"[wpr-achievements] generated catalogue for {productId}: {catalogue.Achievements.Count} achievement(s) " +
                    $"from {catalogue.KeySource}, {named} named ({catalogue.TextSource ?? "keys only"}), {icons} icon(s), " +
                    $"{watch.ElapsedMilliseconds} ms");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"[wpr-achievements] could not generate a catalogue for {productId}: {ex.Message}");
                return false;
            }
        }
    }
}
