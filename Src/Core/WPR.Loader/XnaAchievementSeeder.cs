using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Xna.Framework.GamerServices;

using WPR.Achievements;
using WPR.Common;
using WPR.Models;

namespace WPR
{
    /// <summary>
    /// Populates / keeps in sync the local achievements DB from the game's catalogue
    /// (<see cref="HardcodedAchievementCatalogue"/>): the one WPR ships under
    /// <c>Database/Achievements/&lt;productId&gt;/</c>, or, for a game it ships none for, the one
    /// built from the installed package (<see cref="GeneratedAchievementCatalogues"/>).
    ///
    /// Reconciliation is <b>non-destructive</b>: new entries are inserted with
    /// <c>IsEarned = false</c>, changed metadata (name / description / score / icon)
    /// is updated in place, and unlock state (<c>IsEarned</c> / <c>EarnedDateTime</c>
    /// / <c>EarnedOnline</c>) is never touched. Safe to run at every install and at
    /// every startup. At runtime <see cref="SignedInGamer.BeginAwardAchievement"/>
    /// flips the row keyed by <c>OwnProductId + Key</c> when the game unlocks.
    /// </summary>
    public static class XnaAchievementSeeder
    {
        /// <summary>
        /// Autofill / reconcile a single product's achievements at install time.
        /// </summary>
        public static async Task SeedAsync(string productId, string appName)
        {
            if (string.IsNullOrEmpty(productId)) return;
            try
            {
                // No catalogue ships for this game: build one from the package just installed.
                GeneratedAchievementCatalogues.EnsureFor(productId);
                await ReconcileAsync(AchievementContext.Current, productId, appName, HardcodedAchievementCatalogue.Load(productId));
                OfferToHub(productId);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall,
                    $"XnaAchievementSeeder: seed failed for '{appName}' ({productId}): {ex}");
            }
        }

        /// <summary>
        /// Re-check every installed product that ships a hardcoded catalogue against
        /// it, applying additions / metadata changes without resetting unlock
        /// progress. Runs at startup so catalogue updates reach already-installed
        /// games with no reinstall.
        /// </summary>
        public static async Task ReconcileCatalogueGamesAsync()
        {
            try
            {
                Dictionary<string, string> installed;
                try
                {
                    installed = (await ApplicationContext.Current.Applications!.AsNoTracking().ToListAsync())
                        .GroupBy(a => a.ProductId.Trim('{').Trim('}'), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.Startup,
                        $"XnaAchievementSeeder: cannot read installed apps for reconcile: {ex.Message}");
                    return;
                }

                // Installed games with no shipped catalogue and none from the current generator
                // (installed before generation existed, or by an older generator) get one built
                // in the background, so startup does not wait on reading their packages.
                List<string> missing = installed.Keys
                    .Where(id => GeneratedAchievementCatalogues.NeedsOne(id) &&
                                 PackageAchievementCatalogue.WrittenVersion(GeneratedAchievementCatalogues.Directory(id)) < PackageAchievementCatalogue.GeneratorVersion)
                    .ToList();
                if (missing.Count > 0)
                {
                    Dictionary<string, string> names = installed;
                    _ = Task.Run(() => GenerateMissingAsync(missing, names));
                }

                IReadOnlyList<string> catalogueIds = HardcodedAchievementCatalogue.ProductIds();
                if (catalogueIds.Count == 0) return;

                // Games restored from WPR Hub have rows without an install; they get catalogue
                // updates too, or a corrected key would leave their row orphaned.
                HashSet<string> withRows = new HashSet<string>(
                    (await AchievementContext.Current.Achievements!.AsNoTracking()
                        .Select(a => a.OwnProductId).Distinct().ToListAsync())
                    .Where(id => !string.IsNullOrEmpty(id)).Select(id => id.Trim('{').Trim('}')),
                    StringComparer.OrdinalIgnoreCase);

                foreach (string productId in catalogueIds)
                {
                    // Only reconcile games the user has installed or has achievements in; leave
                    // catalogues for absent games alone.
                    string? appName;
                    if (!installed.TryGetValue(productId, out appName))
                    {
                        if (!withRows.Contains(productId)) continue;
                        appName = HardcodedAchievementCatalogue.GameName(productId);
                    }
                    await ReconcileAsync(AchievementContext.Current, productId, appName ?? productId,
                        HardcodedAchievementCatalogue.Load(productId));
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Startup,
                    $"XnaAchievementSeeder: catalogue reconcile pass failed: {ex}");
            }
        }

        /// <summary>
        /// Make sure each of these games has its catalogue in the local DB, installed or not. For
        /// WPR Hub's restore, which runs this before marking the account's unlocks earned, so a
        /// device that never installed a game still lists every achievement in it (the locked ones
        /// too) with names, descriptions and icons. Ids are matched loosely (case, braces); a game
        /// with no bundled catalogue is skipped, and the restore makes its rows from the hub's data.
        /// </summary>
        /// <remarks>
        /// Runs on a thread-pool thread, so it uses a context of its own rather than the tracked
        /// <see cref="AchievementContext.Current"/>, which a running game's award path uses.
        /// </remarks>
        public static async Task SeedCataloguesAsync(IEnumerable<string> productIds)
        {
            try
            {
                Dictionary<string, string> catalogue = HardcodedAchievementCatalogue.ProductIds()
                    .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                using AchievementContext db = new AchievementContext();
                foreach (string raw in productIds.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (!catalogue.TryGetValue(raw.Trim().Trim('{', '}'), out string? productId)) continue;
                    await ReconcileAsync(db, productId, HardcodedAchievementCatalogue.GameName(productId) ?? productId,
                        HardcodedAchievementCatalogue.Load(productId));
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.GamerServices, $"XnaAchievementSeeder: catalogue seed for restored games failed: {ex}");
            }
        }

        /// <summary>
        /// Builds catalogues for installed games that lack one, then seeds each into the
        /// database (on a context of its own: this runs off the UI thread, beside any game's
        /// award path) and offers it to WPR Hub. Never throws.
        /// </summary>
        private static async Task GenerateMissingAsync(List<string> productIds, Dictionary<string, string> names)
        {
            try
            {
                using AchievementContext db = new AchievementContext();
                foreach (string productId in productIds)
                {
                    if (!GeneratedAchievementCatalogues.EnsureFor(productId)) continue;
                    List<HardcodedAchievement> catalogue = HardcodedAchievementCatalogue.Load(productId);
                    if (catalogue.Count == 0) continue;
                    await ReconcileAsync(db, productId, names.TryGetValue(productId, out string? name) ? name : productId, catalogue);
                    OfferToHub(productId);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Startup, $"XnaAchievementSeeder: generating missing catalogues failed: {ex}");
            }
        }

        /// <summary>
        /// A product's catalogue as WPR Hub's achievement definitions (icons as absolute paths),
        /// or null when it has none.
        /// </summary>
        public static WPR.Engine.Online.TitleAchievements? DescribeForHub(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId) || Configuration.Current is not { } config) return null;
            List<HardcodedAchievement> catalogue = HardcodedAchievementCatalogue.Load(productId);
            if (catalogue.Count == 0) return null;
            string source = HardcodedAchievementCatalogue.HasBundledAchievements(productId) ? "bundled" : "package";
            return new WPR.Engine.Online.TitleAchievements(productId, source, catalogue.Select(a =>
                new WPR.Engine.Online.OfferedAchievement(a.Key, a.Name, a.NameFromKey,
                    string.IsNullOrWhiteSpace(a.Description) ? null : a.Description, a.GamerScore, a.Secret,
                    string.IsNullOrEmpty(a.IconRelativePath) ? null : config.DataPath(a.IconRelativePath))).ToList());
        }

        /// <summary>
        /// Every installed game's catalogue, described for WPR Hub: the backfill
        /// <c>HubAchievementDefinitions</c> runs at a process's first flush. Reads its own database
        /// context, because it runs on a flush thread.
        /// </summary>
        public static IEnumerable<WPR.Engine.Online.TitleAchievements> InstalledCataloguesForHub()
        {
            List<string> ids;
            using (var db = new ApplicationContext())
            {
                ids = db.Applications?.AsNoTracking().Select(a => a.ProductId).ToList()
                          .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim('{', '}'))
                          .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                      ?? new List<string>();
            }
            foreach (string id in ids)
            {
                if (DescribeForHub(id) is { } catalogue) yield return catalogue;
            }
        }

        /// <summary>Queue a product's catalogue for WPR Hub. A no-op without a hub or with server logging off.</summary>
        private static void OfferToHub(string productId)
        {
            try
            {
                if (WPR.Engine.Online.OnlineBackend.AchievementDefinitions is { } sync && DescribeForHub(productId) is { } catalogue)
                {
                    sync.Offer(catalogue);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"Could not offer achievement definitions to WPR Hub (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Upserts <paramref name="desired"/> into the DB for the product, preserving
        /// earned state. Inserts missing rows, updates changed metadata, and removes rows whose
        /// key left the catalogue: any such row for a shipped catalogue, only unearned ones for
        /// a generated catalogue (which a later generator may key differently), so an unlock is
        /// never lost to a regeneration.
        /// </summary>
        private static async Task ReconcileAsync(AchievementContext db, string productId, string appName, List<HardcodedAchievement> desired)
        {
            bool generated = HardcodedAchievementCatalogue.IsGenerated(productId);
            productId = productId.Trim('{').Trim('}');
            if (desired.Count == 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"XnaAchievementSeeder: no catalogue for '{appName}' ({productId}); nothing to reconcile.");
                return;
            }

            List<Achievement> existing = await db.Achievements!
                .Where(a => a.OwnProductId == productId)
                .ToListAsync();
            Dictionary<string, Achievement> byKey = existing
                .Where(a => !string.IsNullOrEmpty(a.Key))
                .GroupBy(a => a.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            int inserted = 0, updated = 0;
            foreach (HardcodedAchievement d in desired)
            {
                if (byKey.TryGetValue(d.Key, out Achievement? row))
                {
                    // Metadata only — never IsEarned / EarnedDateTime / EarnedOnline.
                    bool changed = false;
                    if (row.Name != d.Name) { row.Name = d.Name; changed = true; }
                    if (row.Description != d.Description) { row.Description = d.Description; changed = true; }
                    if (row.HowToEarn != d.Description) { row.HowToEarn = d.Description; changed = true; }
                    if (row.GamerScore != d.GamerScore) { row.GamerScore = d.GamerScore; changed = true; }
                    // Only overwrite the icon when the catalogue actually supplies one.
                    if (!string.IsNullOrEmpty(d.IconRelativePath) && row._IconPath != d.IconRelativePath)
                    {
                        row._IconPath = d.IconRelativePath; changed = true;
                    }
                    if (changed) updated++;
                }
                else
                {
                    db.Achievements!.Add(new Achievement
                    {
                        OwnProductId = productId,
                        Key = d.Key,
                        Name = d.Name,
                        Description = d.Description,
                        HowToEarn = d.Description,
                        _IconPath = d.IconRelativePath,
                        GamerScore = d.GamerScore,
                        DisplayBeforeEarned = true,
                        IsEarned = false,
                        EarnedOnline = false,
                        EarnedDateTime = DateTime.MinValue,
                    });
                    inserted++;
                }
            }

            // Remove stale rows whose Key is no longer in the catalogue. The
            // catalogue is authoritative for a curated game, so a corrected/removed
            // key must not leave an orphan row behind (a wrong key can crash games
            // that index their own asset map by achievement key). Earned state for
            // keys that still exist is preserved by the upsert above.
            var desiredKeys = new HashSet<string>(desired.Select(d => d.Key), StringComparer.Ordinal);
            List<Achievement> stale = existing.Where(a => !desiredKeys.Contains(a.Key) && !(generated && a.IsEarned)).ToList();
            int removed = stale.Count;
            if (removed > 0) db.Achievements!.RemoveRange(stale);

            if (inserted > 0 || updated > 0 || removed > 0)
            {
                await db.SaveChangesAsync();
            }

            Log.Info(LogCategory.AppInstall,
                $"XnaAchievementSeeder: '{appName}' ({productId}) reconciled — {removed} removed, " +
                $"{inserted} added, {updated} updated, {existing.Count} existing.");
        }
    }
}
