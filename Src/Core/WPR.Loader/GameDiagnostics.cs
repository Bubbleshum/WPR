using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Xna.Framework.GamerServices;

using WPR.Common;
using WPR.Models;

namespace WPR
{
    /// <summary>One labelled fact about an installed game.</summary>
    public sealed record GameDiagnosticField(string Label, string Value);

    /// <summary>A titled group of <see cref="GameDiagnosticField"/>s.</summary>
    public sealed record GameDiagnosticSection(string Title, IReadOnlyList<GameDiagnosticField> Fields);

    /// <summary>
    /// The facts behind each head's per-game "info" screen: what the catalogue row says, where
    /// the install landed, whether the IL was patched by the current patcher, and whether an
    /// achievement catalogue exists for the product ID at all.
    ///
    /// <para>Lives here rather than in either head because both need exactly the same answers
    /// and the two shells share no UI project. The heads own only their own presentation and an
    /// "environment" section (OS version, device), which is the one part that genuinely differs.</para>
    ///
    /// <para>Motivating case: a sideloaded build whose product ID we have no copy of shows no
    /// achievements, and the seeder's "no catalogue for …" line only reaches a log. The
    /// product ID and the catalogue verdict have to be readable in the app.</para>
    /// </summary>
    public static class GameDiagnostics
    {
        /// <summary>
        /// Label of the field <see cref="MeasureInstallFolder"/> fills in. Collect() leaves a
        /// placeholder there so a head can swap the value once the walk finishes off-thread.
        /// </summary>
        public const string FolderContentsLabel = "folder contents";

        /// <summary>Product IDs are stored unbraced; manifests and intents may carry braces.</summary>
        public static string NormalizeProductId(string? productId) =>
            (productId ?? "").Trim('{').Trim('}');

        public static string InstallFolder(string productId) => Path.Combine(
            Configuration.Current!.DataPath(Application.DataStoreFolder), NormalizeProductId(productId));

        public static string CatalogueFolder(string productId) => Path.Combine(
            Configuration.Current!.DataPath(HardcodedAchievementCatalogue.CatalogueRoot),
            NormalizeProductId(productId));

        /// <summary>
        /// Everything cheap enough to read synchronously: the catalogue row, the catalogue
        /// manifest and the achievement table. Callers on a UI thread are the expected case —
        /// <see cref="ApplicationContext"/> and <see cref="AchievementContext"/> are shared
        /// single instances and are not thread-safe, and these are a few local SQLite rows.
        /// The one expensive fact, the install folder walk, is deferred to
        /// <see cref="MeasureInstallFolder"/>.
        /// </summary>
        public static List<GameDiagnosticSection> Collect(string productId)
        {
            productId = NormalizeProductId(productId);

            Application? app = null;
            try
            {
                app = ApplicationContext.Current.Applications!
                    .AsNoTracking()
                    .ToList()
                    .FirstOrDefault(a => string.Equals(
                        NormalizeProductId(a.ProductId), productId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppList, $"GameDiagnostics: cannot read the application table:\n{ex}");
            }

            return new List<GameDiagnosticSection>
            {
                new GameDiagnosticSection("identity", Identity(productId, app)),
                new GameDiagnosticSection("install", Install(productId, app)),
                new GameDiagnosticSection("achievements", Achievements(productId)),
            };
        }

        private static List<GameDiagnosticField> Identity(string productId, Application? app)
        {
            // Product ID leads: it is what keys the catalogue folder, the install folder and
            // every achievement row, so it is the first thing worth quoting in a bug report.
            return new List<GameDiagnosticField>
            {
                new GameDiagnosticField("product id",
                    string.IsNullOrEmpty(productId) ? "(unknown)" : productId),
                new GameDiagnosticField("display name",
                    HardcodedAchievementCatalogue.GameName(productId) ?? "(no catalogue name)"),
                new GameDiagnosticField("manifest title", Blank(app?.Name)),
                new GameDiagnosticField("version", Blank(app?.Version)),
                new GameDiagnosticField("runtime type",
                    app == null ? "(not installed)" : app.ApplicationType.ToString().ToLowerInvariant()),
                new GameDiagnosticField("author", Blank(app?.Author)),
                new GameDiagnosticField("publisher", Blank(app?.Publisher)),
                new GameDiagnosticField("entry assembly", Blank(app?.Assembly)),
                new GameDiagnosticField("entry point", Blank(app?.EntryPoint)),
            };
        }

        private static List<GameDiagnosticField> Install(string productId, Application? app)
        {
            var fields = new List<GameDiagnosticField>
            {
                new GameDiagnosticField("installed",
                    app == null
                        ? "(not installed)"
                        : app.InstalledTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            };

            // The patcher rewrites IL at install time, so a game installed before a patcher
            // change still carries the old redirects. Comparing the stamped version against the
            // running one is the cheap way to see that a re-patch is owed.
            if (app != null)
            {
                string patched = $"v{app.PatchedVersion}  ·  patcher is v{ApplicationPatcher.Version}";
                if (app.PatchedVersion < ApplicationPatcher.Version)
                {
                    patched += "  ·  STALE, re-patch this game";
                }
                fields.Add(new GameDiagnosticField("patched with", patched));
            }

            string installFolder = InstallFolder(productId);
            fields.Add(new GameDiagnosticField("install folder", installFolder));
            fields.Add(new GameDiagnosticField(FolderContentsLabel,
                Directory.Exists(installFolder) ? "measuring…" : "MISSING, the folder is not on disk"));
            fields.Add(new GameDiagnosticField("icon", Blank(app?.IconPath)));

            return fields;
        }

        private static List<GameDiagnosticField> Achievements(string productId)
        {
            var fields = new List<GameDiagnosticField>();

            // A missing catalogue is silent everywhere else — the seeder logs it and moves on —
            // and is the most common reason a game shows no achievements at all.
            if (HardcodedAchievementCatalogue.HasCatalogue(productId))
            {
                int entries = 0;
                try
                {
                    entries = HardcodedAchievementCatalogue.Load(productId).Count;
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.GamerServices, $"GameDiagnostics: catalogue unreadable: {ex.Message}");
                }
                fields.Add(new GameDiagnosticField("catalogue", $"present  ·  {entries} entries"));
            }
            else
            {
                fields.Add(new GameDiagnosticField("catalogue",
                    "NONE for this product id, nothing will seed"));
            }

            fields.Add(new GameDiagnosticField("catalogue folder", CatalogueFolder(productId)));

            try
            {
                List<Achievement> owned = AchievementContext.Current.Achievements!
                    .AsNoTracking()
                    .Where(a => a.OwnProductId == productId)
                    .ToList();

                int earned = owned.Count(a => a.IsEarned);
                int earnedScore = owned.Where(a => a.IsEarned).Sum(a => a.GamerScore);
                int totalScore = owned.Sum(a => a.GamerScore);

                fields.Add(new GameDiagnosticField("database rows",
                    owned.Count == 0
                        ? "0, nothing seeded for this product id"
                        : $"{owned.Count} rows  ·  {earned} earned  ·  {earnedScore}/{totalScore} G"));
            }
            catch (Exception ex)
            {
                fields.Add(new GameDiagnosticField("database rows", $"query failed: {ex.Message}"));
            }

            return fields;
        }

        /// <summary>
        /// Walk the install folder and summarise it. Content-heavy games run to thousands of
        /// files, so callers are expected to run this off the UI thread and then replace the
        /// <see cref="FolderContentsLabel"/> field's value.
        /// </summary>
        public static string MeasureInstallFolder(string productId)
        {
            string installFolder = InstallFolder(productId);
            if (!Directory.Exists(installFolder)) return "MISSING, the folder is not on disk";

            try
            {
                List<FileInfo> files = new DirectoryInfo(installFolder)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .ToList();

                long bytes = files.Sum(f => f.Length);
                int dlls = files.Count(f => f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

                // .dll.original siblings are the patcher's backups; their absence means the
                // install never ran through PatchDll. Their timestamp is reported as the file's
                // own, NOT as "when this was patched" — extraction preserves the timestamp from
                // inside the XAP, so on Windows these routinely read as the game's original
                // build date. For patch recency use "patched with" (the stamped patcher version)
                // and "installed" instead.
                List<FileInfo> backups = files
                    .Where(f => f.Name.EndsWith(".dll.original", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                string summary = $"{files.Count} files  ·  {FormatBytes(bytes)}  ·  {dlls} dll";

                return summary + (backups.Count == 0
                    ? "  ·  no .dll.original, never patched"
                    : $"  ·  {backups.Count} .dll.original, newest {backups.Max(f => f.LastWriteTime):yyyy-MM-dd HH:mm}");
            }
            catch (Exception ex)
            {
                return $"unreadable: {ex.Message}";
            }
        }

        /// <summary>The whole report as plain text, for pasting into a bug report.</summary>
        public static string ToPlainText(IEnumerable<GameDiagnosticSection> sections)
        {
            var builder = new StringBuilder();

            foreach (GameDiagnosticSection section in sections)
            {
                builder.Append('\n').Append('[').Append(section.Title).Append("]\n");

                foreach (GameDiagnosticField field in section.Fields)
                {
                    builder.Append(field.Label).Append(": ").Append(field.Value).Append('\n');
                }
            }

            return builder.ToString().Trim();
        }

        private static string Blank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "(empty)" : value!;

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };

            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }
    }
}
