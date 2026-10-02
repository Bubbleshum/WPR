using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using WPR.Common;

namespace WPR.Shell
{
    /// <summary>
    /// Game names from WPR Hub, for games WPR has no name for itself: no bundled achievement
    /// catalogue and no install record, which is every uninstalled game whose unlocks are still in
    /// achievements.db. Shared by both heads; the lists that show them decide the order of the
    /// local sources and ask this last.
    /// </summary>
    /// <remarks>
    /// A name found is kept in <c>&lt;DataStore&gt;/Online/title-names.json</c>, so the hub is asked
    /// once per game, not once per page visit. A game the hub has no record of is asked about again
    /// only in a later process, so a title the hub learns about later still gets its name. Works
    /// signed out (the hub's title endpoint is anonymous) and never throws: an unreachable hub just
    /// leaves the name unknown.
    /// </remarks>
    public static class HubTitleNames
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string>? _cache;
        private static readonly ConcurrentDictionary<string, bool> AskedThisProcess = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The name a previous lookup found, or null.</summary>
        public static string? Cached(string productId)
        {
            lock (Gate)
            {
                return Load().TryGetValue(Key(productId), out string? name) ? name : null;
            }
        }

        /// <summary>
        /// Asks the hub for every id not already known, at most four at a time, and returns the names
        /// it found (keyed by the ids as given). Ids already cached or already asked this process are
        /// skipped.
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, string>> FetchMissingAsync(IEnumerable<string> productIds)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (HubSetup.Current is not { } hub) return found;

            List<string> wanted;
            lock (Gate)
            {
                Dictionary<string, string> cache = Load();
                wanted = productIds
                    .Where(id => !string.IsNullOrWhiteSpace(id) && !cache.ContainsKey(Key(id)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(id => AskedThisProcess.TryAdd(Key(id), true))
                    .ToList();
            }
            if (wanted.Count == 0) return found;

            using var throttle = new System.Threading.SemaphoreSlim(4);
            await Task.WhenAll(wanted.Select(async id =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    string? name = await hub.Social.GetTitleNameAsync(id).ConfigureAwait(false);
                    if (name != null) lock (found) found[id] = name;
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false);

            if (found.Count > 0)
            {
                lock (Gate)
                {
                    Dictionary<string, string> cache = Load();
                    foreach (var pair in found) cache[Key(pair.Key)] = pair.Value;
                    Save(cache);
                }
            }
            return found;
        }

        private static string Key(string productId) => productId.Trim().Trim('{', '}').ToUpperInvariant();

        private static string? FilePath() =>
            Configuration.Current == null ? null : Path.Combine(Configuration.Current.DataPath("Online"), "title-names.json");

        private static Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string? path = FilePath();
                if (path != null && File.Exists(path))
                {
                    var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (read != null) foreach (var pair in read) _cache[pair.Key] = pair.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Common, $"[wpr-titles] could not read title-names.json: {ex.Message}");
            }
            return _cache;
        }

        private static void Save(Dictionary<string, string> cache)
        {
            try
            {
                string? path = FilePath();
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(cache));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Common, $"[wpr-titles] could not write title-names.json: {ex.Message}");
            }
        }
    }
}
