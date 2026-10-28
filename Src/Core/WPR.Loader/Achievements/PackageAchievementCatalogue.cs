using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Mono.Cecil;

namespace WPR.Achievements
{
    /// <summary>One achievement of a catalogue generated from a package.</summary>
    /// <param name="NameFromKey">True when no source named it and <see cref="Name"/> is the key made readable.</param>
    /// <param name="Icon">PNG or JPEG bytes, or null.</param>
    public sealed record GeneratedAchievement(
        string Key, string Name, bool NameFromKey, string Description, int GamerScore, bool Secret, byte[]? Icon);

    /// <summary>
    /// A catalogue built from what a package says about its achievements, for a game WPR ships
    /// no catalogue for.
    /// </summary>
    /// <param name="KeySource">Where the keys came from: <c>xlast</c>, <c>xbox-id</c> (XLAST ids),
    /// <c>spa</c> (SPA ids) or <c>il</c> (the award calls' constants); <c>none</c> when nothing was found.</param>
    public sealed record GeneratedCatalogue(string KeySource, string? TextSource, IReadOnlyList<GeneratedAchievement> Achievements)
    {
        public static readonly GeneratedCatalogue Empty = new("none", null, Array.Empty<GeneratedAchievement>());
    }

    /// <summary>
    /// Builds an achievement catalogue from a game package, generally (nothing per title).
    /// </summary>
    /// <remarks>
    /// <para><b>Keys</b>, in order of preference:</para>
    /// <list type="number">
    /// <item>The XLAST configuration XNA embeds in a GamerServices game. Its strings are the keys
    /// when the package calls <c>SignedInGamer</c>'s award methods; otherwise (a WP8 Xbox title,
    /// which unlocks by number) the pair table's Xbox ids are.</item>
    /// <item>The Xbox SPA's achievement ids, for a WP8 title with no XLAST.</item>
    /// <item>The string constants the package's award calls receive (<see cref="AwardCallKeyScanner"/>).</item>
    /// </list>
    /// <para><b>Text and icons</b>: the SPA (matched by Xbox id, which the XLAST pair table gives
    /// for string keys too), then the package's own string tables and images named after the key,
    /// then the key made readable, with no icon. Gamerscore and the secret flag come only from the
    /// SPA; elsewhere they are 0 and false.</para>
    /// </remarks>
    public static class PackageAchievementCatalogue
    {
        /// <summary>Bump when the generator would produce a different catalogue from the same package.</summary>
        public const int GeneratorVersion = 1;

        public static GeneratedCatalogue Build(IPackageFiles package)
        {
            List<(string Path, ModuleDefinition Module)> modules = new();
            try
            {
                foreach (string path in package.Paths)
                {
                    if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (package.Length(path) is 0 or > 96L * 1024 * 1024) continue;
                    try
                    {
                        ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(package.ReadAll(path), writable: false),
                            new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred });
                        modules.Add((path, module));
                    }
                    catch (Exception)
                    {
                        // Native code, or not an assembly Cecil reads: nothing to scan in it.
                    }
                }
                return Build(package, modules);
            }
            finally
            {
                foreach ((_, ModuleDefinition module) in modules) module.Dispose();
            }
        }

        private static GeneratedCatalogue Build(IPackageFiles package, List<(string Path, ModuleDefinition Module)> modules)
        {
            // Top-level modules are the game; satellites only carry resources.
            List<ModuleDefinition> code = modules.Where(m => !m.Path.Contains('/')).Select(m => m.Module).ToList();

            IReadOnlyList<XlastAchievement>? xlast = null;
            foreach (ModuleDefinition module in code)
            {
                EmbeddedResource? resource = module.Resources.OfType<EmbeddedResource>()
                    .FirstOrDefault(r => r.Name == XlastAchievementReader.ResourceName);
                if (resource is not null && XlastAchievementReader.Parse(resource.GetResourceData()) is { Count: > 0 } parsed)
                {
                    xlast = parsed;
                    break;
                }
            }

            AwardCallKeyScanner scanner = new AwardCallKeyScanner(code).Scan();
            XboxSpa? spa = XboxSpaAchievementReader.Find(package);
            Dictionary<uint, SpaAchievement> spaById = spa?.Achievements.ToDictionary(a => a.Id) ?? new();

            List<(string Key, uint? XboxId, string? Friendly)> keys;
            string keySource;
            if (xlast is not null)
            {
                bool stringKeys = scanner.CallsSignedInGamer;
                // XLAST pads some keys the game passes trimmed: where the award calls' own
                // constants have only the trimmed form, that is the key.
                HashSet<string> constants = new(scanner.Keys, StringComparer.Ordinal);
                string GameKey(string key) => !constants.Contains(key) && constants.Contains(key.Trim()) ? key.Trim() : key;
                keys = xlast.Select(a => (stringKeys ? GameKey(a.Key) : a.XboxId.ToString(CultureInfo.InvariantCulture), (uint?)a.XboxId, (string?)a.Key.Trim())).ToList();
                keySource = stringKeys ? "xlast" : "xbox-id";
            }
            else if (spa is not null)
            {
                keys = spa.Achievements.Select(a => (a.Id.ToString(CultureInfo.InvariantCulture), (uint?)a.Id, (string?)a.FriendlyName)).ToList();
                keySource = "spa";
            }
            else if (scanner.Keys.Count > 0)
            {
                keys = scanner.Keys.Select(k => (k, (uint?)null, (string?)null)).ToList();
                keySource = "il";
            }
            else
            {
                return GeneratedCatalogue.Empty;
            }

            // Duplicate keys would collide on the database's (product, key) row.
            keys = keys.GroupBy(k => k.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();

            PackageStringTables? tables = null;
            PackageImages? images = null;
            List<GeneratedAchievement> result = new(keys.Count);
            HashSet<string> textSources = new();
            foreach ((string key, uint? xboxId, string? keyFriendly) in keys)
            {
                SpaAchievement? fromSpa = xboxId is { } id && spaById.TryGetValue(id, out SpaAchievement? a) ? a : null;
                string? name = fromSpa?.Name;
                string? description = fromSpa?.HowToEarn ?? fromSpa?.Description;
                byte[]? icon = fromSpa is not null ? spa!.Image(fromSpa.ImageId) : null;
                if (icon is not null && !PackageImages.IconFits(icon)) icon = null;
                if (fromSpa is not null && (name ?? description) is not null) textSources.Add("spa");

                if (name is null || description is null)
                {
                    tables ??= PackageStringTables.Read(package, modules);
                    // A numeric key names nothing; the XLAST or SPA identifier beside it might.
                    string lookup = keySource is "xbox-id" or "spa" ? fromSpa?.FriendlyName ?? keyFriendly ?? key : key;
                    if (tables.Find(lookup) is { } text)
                    {
                        name ??= text.Name;
                        description ??= text.Description;
                        textSources.Add("resources");
                    }
                }

                if (icon is null)
                {
                    images ??= new PackageImages(package);
                    List<string> hints = new() { key };
                    if (fromSpa?.FriendlyName is { } friendly) hints.Add(friendly);
                    if (keyFriendly is not null) hints.Add(keyFriendly);
                    hints.AddRange(ParallelNames(scanner, key));
                    icon = images.Find(hints);
                }

                bool fromKey = string.IsNullOrWhiteSpace(name);
                if (fromKey)
                {
                    name = Readable(fromSpa?.FriendlyName ?? keyFriendly ?? key, keySource);
                }
                result.Add(new GeneratedAchievement(key, name!.Trim(), fromKey, description?.Trim() ?? "",
                    fromSpa?.GamerScore ?? 0, fromSpa?.Secret ?? false, icon));
            }

            return new GeneratedCatalogue(keySource, textSources.Count == 0 ? null : string.Join("+", textSources.Order()), result);
        }

        /// <summary>
        /// Elements at the same index of the other constant string arrays of the type a key's array
        /// is declared in, when they have as many elements: games keep icon names in a list parallel
        /// to their keys.
        /// </summary>
        private static IEnumerable<string> ParallelNames(AwardCallKeyScanner scanner, string key)
        {
            if (!scanner.Origins.TryGetValue(key, out AwardCallKeyScanner.ArrayOrigin? origin)) yield break;
            Dictionary<int, string>? keyArray = scanner.StringArray(origin.Field);
            if (keyArray is null) yield break;
            foreach (FieldDefinition sibling in origin.Field.DeclaringType.Fields)
            {
                if (sibling == origin.Field) continue;
                if (scanner.StringArray(sibling) is { } values && values.Count == keyArray.Count &&
                    values.TryGetValue(origin.Index, out string? value))
                {
                    yield return value;
                }
            }
        }

        /// <summary>
        /// A key as a display name: <c>ACHIEVEMENT_COLLECT_STARS</c> is "Collect Stars",
        /// <c>GET_BLUE_BIRD</c> "Get Blue Bird", <c>TourOfDuty</c> "Tour Of Duty"; a number is
        /// "Achievement 12".
        /// </summary>
        public static string Readable(string key, string keySource = "il") =>
            WPR.Xna.Achievements.UnlistedAchievements.Readable(key);

        // ---- the on-disk form: the bundled catalogue's achievements.json, plus provenance ----

        public const string ManifestName = "achievements.json";

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Writes the catalogue in the bundled format (<c>achievements.json</c> + one image per
        /// icon) into <paramref name="directory"/>, replacing what was there.
        /// </summary>
        public static void Write(GeneratedCatalogue catalogue, string directory, string productId)
        {
            string staging = directory + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(staging);
            try
            {
                List<GeneratedEntry> entries = new();
                int n = 0;
                foreach (GeneratedAchievement a in catalogue.Achievements)
                {
                    string? icon = null;
                    if (a.Icon is { } bytes)
                    {
                        bool jpeg = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
                        icon = $"{n:D3}{(jpeg ? ".jpg" : ".png")}";
                        File.WriteAllBytes(Path.Combine(staging, icon), bytes);
                    }
                    entries.Add(new GeneratedEntry(a.Key, a.Name, a.NameFromKey ? true : null, a.Description, a.GamerScore,
                        a.Secret ? true : null, icon));
                    n++;
                }
                GeneratedManifest manifest = new(productId, GeneratorVersion, catalogue.KeySource, catalogue.TextSource, entries);
                File.WriteAllText(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, WriteOptions));

                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Directory.Move(staging, directory);
            }
            catch
            {
                try { Directory.Delete(staging, true); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>The generator version a written catalogue was made by, or 0 when there is none.</summary>
        public static int WrittenVersion(string directory)
        {
            try
            {
                string path = Path.Combine(directory, ManifestName);
                if (!File.Exists(path)) return 0;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty("GeneratorVersion", out JsonElement v) && v.TryGetInt32(out int version) ? version : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private sealed record GeneratedManifest(
            string ProductId, int GeneratorVersion, string KeySource, string? TextSource, List<GeneratedEntry> Achievements)
        {
            /// <summary>Marks the catalogue as made on this device, not shipped with WPR.</summary>
            public string Generated => "package";
        }

        private sealed record GeneratedEntry(
            string Key, string Name, bool? NameFromKey, string Description, int GamerScore, bool? Secret, string? Icon);
    }
}
