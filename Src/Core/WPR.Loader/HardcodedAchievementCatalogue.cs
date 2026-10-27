using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// One curated achievement from a game's hardcoded catalogue. <see cref="IconRelativePath"/>
    /// is relative to <see cref="Configuration.DataStorePath"/> (empty when no icon ships), so it
    /// round-trips through <see cref="Configuration.DataPath"/> exactly like the rest of the DB's
    /// <c>_IconPath</c> values.
    /// </summary>
    public sealed record HardcodedAchievement(
        string Key,
        string Name,
        string Description,
        int GamerScore,
        string IconRelativePath,
        bool Secret = false,
        bool NameFromKey = false);

    /// <summary>
    /// Reads the committed, hardcoded achievement catalogue for a product — the authoritative
    /// source for curated games (no network, no IL scan). Each game ships under
    /// <c>Database/Achievements/&lt;productId&gt;/</c> as an <c>achievements.json</c> manifest plus
    /// one PNG per achievement; that tree is deployed to the runtime data dir at startup, so the
    /// paths here resolve against <see cref="Configuration.DataStorePath"/>.
    /// </summary>
    public static class HardcodedAchievementCatalogue
    {
        public const string CatalogueRoot = "Database/Achievements";
        private const string ManifestName = "achievements.json";

        private static readonly JsonSerializerOptions JsonOptions =
            new() { PropertyNameCaseInsensitive = true };

        private static string ProductDir(string productId) =>
            Configuration.Current!.DataPath(Path.Combine(CatalogueRoot, productId));

        /// <summary>
        /// True when WPR ships a catalogue with at least one achievement for the product. Such a
        /// catalogue always wins over one generated from the package
        /// (<see cref="WPR.Achievements.GeneratedAchievementCatalogues"/>).
        /// </summary>
        public static bool HasBundledAchievements(string productId) =>
            !string.IsNullOrEmpty(productId) && ReadEntries(ProductDir(productId)) is { Count: > 0 };

        /// <summary>True when the product's achievements come from a catalogue generated on this device.</summary>
        public static bool IsGenerated(string productId) =>
            !string.IsNullOrEmpty(productId) && !HasBundledAchievements(productId) &&
            File.Exists(Path.Combine(WPR.Achievements.GeneratedAchievementCatalogues.Directory(productId), ManifestName));

        /// <summary>Drops cached manifest reads for a product whose catalogue was just (re)written.</summary>
        public static void Forget(string productId) => ManifestCache.TryRemove(productId, out _);

        public static bool HasCatalogue(string productId) =>
            !string.IsNullOrEmpty(productId)
            && File.Exists(Path.Combine(ProductDir(productId), ManifestName));

        /// <summary>
        /// Product IDs (folder names) that have a catalogue manifest: shipped with WPR, or
        /// generated on this device from an installed package.
        /// </summary>
        public static IReadOnlyList<string> ProductIds()
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rootName in new[] { CatalogueRoot, WPR.Achievements.GeneratedAchievementCatalogues.Root })
            {
                string root = Configuration.Current!.DataPath(rootName);
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, ManifestName)) && seen.Add(Path.GetFileName(dir)))
                        ids.Add(Path.GetFileName(dir));
                }
            }
            return ids;
        }

        /// <summary>
        /// The curated game display name from the product's catalogue manifest (the top-level
        /// <c>"Name"</c> in <c>achievements.json</c>), or null when no catalogue ships for the
        /// product or the manifest omits a name. Used to title a running game from its curated
        /// metadata, falling back to the manifest title when there's no catalogue.
        /// </summary>
        public static string? GameName(string productId)
        {
            string? name = ReadManifest(productId)?.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        /// <summary>
        /// The curated game description from the product's catalogue manifest (the top-level
        /// <c>"Description"</c> in <c>achievements.json</c>), or null when no catalogue ships for
        /// the product or the manifest omits a description. Used on the game page in preference to
        /// the (often empty/terse) WMAppManifest description.
        /// </summary>
        public static string? GameDescription(string productId)
        {
            string? desc = ReadManifest(productId)?.Description;
            return string.IsNullOrWhiteSpace(desc) ? null : desc;
        }

        // GameName/GameDescription back the library list and game-page bindings, which re-read
        // per item (and on every tooltip hover). The catalogue tree is deployed once at startup
        // and stable for the session, so cache the parsed manifest (null included) to keep the UI
        // off the disk. Not used by Load() — the install/reconcile path keeps its own fresh read.
        private static readonly ConcurrentDictionary<string, ManifestDto?> ManifestCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads and deserialises a product's catalogue manifest, or null if absent/unparseable. Cached per session.</summary>
        private static ManifestDto? ReadManifest(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return null;

            return ManifestCache.GetOrAdd(productId, static id =>
            {
                string manifest = Path.Combine(ProductDir(id), ManifestName);
                if (!File.Exists(manifest)) return null;

                try
                {
                    return JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifest), JsonOptions);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"HardcodedAchievementCatalogue: failed to read manifest {manifest}: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// The product's catalogue: the bundled one when it has entries, else the one generated
        /// from its package on this device, else empty.
        /// </summary>
        public static List<HardcodedAchievement> Load(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return new List<HardcodedAchievement>();

            List<HardcodedAchievement> bundled = Load(ProductDir(productId), $"{CatalogueRoot}/{productId}");
            if (bundled.Count > 0) return bundled;

            string generatedId = WPR.Achievements.GeneratedAchievementCatalogues.Normalise(productId);
            return Load(WPR.Achievements.GeneratedAchievementCatalogues.Directory(productId),
                $"{WPR.Achievements.GeneratedAchievementCatalogues.Root}/{generatedId}");
        }

        private static List<EntryDto>? ReadEntries(string dir)
        {
            string manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) return null;
            try
            {
                return JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifest), JsonOptions)?.Achievements;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall,
                    $"HardcodedAchievementCatalogue: failed to parse {manifest}: {ex.Message}");
                return null;
            }
        }

        /// <param name="relativeRoot">The folder relative to the data store, for icon paths.</param>
        private static List<HardcodedAchievement> Load(string dir, string relativeRoot)
        {
            var result = new List<HardcodedAchievement>();
            List<EntryDto>? entries = ReadEntries(dir);
            if (entries == null) return result;

            foreach (EntryDto a in entries)
            {
                if (string.IsNullOrEmpty(a.Key)) continue;

                string icon = string.Empty;
                if (!string.IsNullOrEmpty(a.Icon) && File.Exists(Path.Combine(dir, a.Icon)))
                {
                    icon = $"{relativeRoot}/{a.Icon}";
                }

                result.Add(new HardcodedAchievement(
                    Key: a.Key,
                    Name: string.IsNullOrEmpty(a.Name) ? a.Key : a.Name,
                    Description: a.Description ?? string.Empty,
                    GamerScore: a.GamerScore,
                    IconRelativePath: icon,
                    Secret: a.Secret,
                    NameFromKey: a.NameFromKey || string.IsNullOrEmpty(a.Name)));
            }

            return result;
        }

        private sealed class ManifestDto
        {
            public string? Name { get; set; }
            public string? Description { get; set; }
            public List<EntryDto>? Achievements { get; set; }
        }

        private sealed class EntryDto
        {
            public string Key { get; set; } = "";
            public string? Name { get; set; }
            public string? Description { get; set; }
            public int GamerScore { get; set; }
            public string? Icon { get; set; }
            public bool Secret { get; set; }
            public bool NameFromKey { get; set; }
        }
    }
}
