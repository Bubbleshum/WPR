using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Xna.Framework.GamerServices;

using WPR.Common;
using WPR.Models;

namespace WPR
{
    /// <summary>
    /// Populates / keeps in sync the local achievements DB from the committed,
    /// hardcoded catalogue (<see cref="HardcodedAchievementCatalogue"/>) — the sole
    /// source of achievements. No network, no scanning of game assemblies: a game
    /// only gets achievements if it ships a catalogue under
    /// <c>Database/Achievements/&lt;productId&gt;/</c>.
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
                await ReconcileAsync(productId, appName, HardcodedAchievementCatalogue.Load(productId));
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
                IReadOnlyList<string> catalogueIds = HardcodedAchievementCatalogue.ProductIds();
                if (catalogueIds.Count == 0) return;

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

                foreach (string productId in catalogueIds)
                {
                    // Only reconcile games the user actually has installed; leave
                    // catalogues for absent games alone.
                    if (!installed.TryGetValue(productId, out string? appName)) continue;
                    await ReconcileAsync(productId, appName ?? productId,
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
        /// Upserts <paramref name="desired"/> into the DB for the product, preserving
        /// earned state. Inserts missing rows, updates changed metadata, deletes
        /// nothing.
        /// </summary>
        private static async Task ReconcileAsync(string productId, string appName, List<HardcodedAchievement> desired)
        {
            productId = productId.Trim('{').Trim('}');
            if (desired.Count == 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"XnaAchievementSeeder: no catalogue for '{appName}' ({productId}); nothing to reconcile.");
                return;
            }

            List<Achievement> existing = await AchievementContext.Current.Achievements!
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
                    AchievementContext.Current.Achievements!.Add(new Achievement
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
            List<Achievement> stale = existing.Where(a => !desiredKeys.Contains(a.Key)).ToList();
            int removed = stale.Count;
            if (removed > 0) AchievementContext.Current.Achievements!.RemoveRange(stale);

            if (inserted > 0 || updated > 0 || removed > 0)
            {
                await AchievementContext.Current.SaveChangesAsync();
            }

            Log.Info(LogCategory.AppInstall,
                $"XnaAchievementSeeder: '{appName}' ({productId}) reconciled — {removed} removed, " +
                $"{inserted} added, {updated} updated, {existing.Count} existing.");
        }
    }
}
