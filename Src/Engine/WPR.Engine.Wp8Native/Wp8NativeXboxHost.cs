using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Xna.Framework.GamerServices;
using WPR.Common;
using WPR.Engine.Online;
using WPR.Xna.Achievements;
using WPR.Xna.Rhi;
using XnaAchievement = Microsoft.Xna.Framework.GamerServices.Achievement;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Xbox Live for a WP8 native title, served by what WPR has: the achievement store every XNA
    /// title uses, and WPR Hub's leaderboards.
    /// </summary>
    /// <remarks>
    /// <para><b>Achievements are numbers.</b> <c>IUser::UnlockAchievementAsync</c> takes a
    /// <c>UInt32</c>, so a row's <see cref="XnaAchievement.Key"/> is that number as text. Names,
    /// descriptions and gamerscore came from Xbox Live, not from the XAP, so a title with a
    /// committed catalogue under <c>Database/Achievements/&lt;productId&gt;/</c> (keys = the ids)
    /// shows real ones; without one a row is created the first time the game unlocks it, named
    /// "Achievement N" and worth nothing, and is still awarded, toasted and sent to the hub.</para>
    ///
    /// <para><b>The award goes through <see cref="SignedInGamer.AwardAchievement"/></b>, the same
    /// path an XNA title takes, so the toast, the "already earned, no toast" rule and the hub
    /// upload behave identically. It runs on a worker: this is called from inside a guest call on
    /// the emulator thread, and the store is asynchronous.</para>
    ///
    /// <para><b>Leaderboards</b> are keyed <c>BestScore&lt;id&gt;:0</c>, or <c>BestTime&lt;id&gt;:0</c>
    /// for a board the game posts to with the Min aggregation, because the hub guesses a board's
    /// sort order from that prefix. Reads block the guest for at most
    /// <see cref="ReadTimeout"/>: the image waits for the operation either way, and it is only
    /// ever asked for on a leaderboard screen.</para>
    /// </remarks>
    public sealed class Wp8NativeXboxHost : IXboxLiveHost
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

        private readonly string _productId;
        private readonly string? _titleName;
        private readonly ConcurrentDictionary<uint, bool> _lowerIsBetter = new();
        private readonly object _cacheLock = new();
        private IReadOnlyList<XboxAchievement>? _cache;

        public Wp8NativeXboxHost(string productId, string? titleName)
        {
            _productId = productId;
            _titleName = titleName;
        }

        public string Gamertag => Configuration.Current?.EffectiveGamerTag ?? string.Empty;

        public IReadOnlyList<XboxAchievement> GetAchievements()
        {
            lock (_cacheLock)
            {
                if (_cache is not null)
                {
                    return _cache;
                }
            }

            IAchievementStore? store = XnaBackend.Achievements;
            if (store is null)
            {
                return [];
            }

            IReadOnlyList<XnaAchievement> rows;
            try
            {
                rows = Task.Run(() => store.GetForProductAsync(_productId)).Wait(ReadTimeout, out var r) ? r : [];
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-xbox] reading achievements failed: {ex.Message}");
                return [];
            }

            var list = new List<XboxAchievement>();
            foreach (XnaAchievement row in rows)
            {
                if (!uint.TryParse(row.Key, out uint id))
                {
                    continue;
                }

                list.Add(new XboxAchievement(
                    id,
                    string.IsNullOrEmpty(row.Name) ? $"Achievement {id}" : row.Name,
                    row.Description ?? string.Empty,
                    row.GamerScore,
                    !row.DisplayBeforeEarned,
                    row.IsEarned,
                    row.IsEarned ? row.EarnedDateTime : null));
            }

            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            Trace.WriteLine($"[wpr-xbox] achievement list read: {list.Count} known, {list.Count(a => a.IsEarned)} earned");
            lock (_cacheLock)
            {
                _cache = list;
            }

            return list;
        }

        public void UnlockAchievement(uint achievementId)
        {
            string key = achievementId.ToString();
            lock (_cacheLock)
            {
                // Mark it earned in what the game will read next, without waiting on the store.
                if (_cache is not null)
                {
                    List<XboxAchievement> updated = _cache.Select(a => a.Id == achievementId && !a.IsEarned
                        ? a with { IsEarned = true, Unlocked = DateTime.Now }
                        : a).ToList();
                    if (updated.All(a => a.Id != achievementId))
                    {
                        updated.Add(new XboxAchievement(achievementId, $"Achievement {achievementId}", string.Empty, 0, false, true, DateTime.Now));
                        updated.Sort((a, b) => a.Id.CompareTo(b.Id));
                    }

                    _cache = updated;
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    IAchievementStore? store = XnaBackend.Achievements;
                    if (store is null)
                    {
                        Trace.WriteLine($"[wpr-xbox] achievement {key} unlocked, but no achievement store is registered");
                        return;
                    }

                    if ((await store.GetByKeyAsync(_productId, key)).Count == 0)
                    {
                        await store.AddAsync(new XnaAchievement
                        {
                            OwnProductId = _productId,
                            Key = key,
                            Name = $"Achievement {achievementId}",
                            Description = string.Empty,
                            HowToEarn = string.Empty,
                            _IconPath = string.Empty,
                            DisplayBeforeEarned = true,
                            GamerScore = 0,
                        });
                        Trace.WriteLine($"[wpr-xbox] achievement {key} has no catalogue row; created one");
                    }

                    SignedInGamer? gamer = Gamer.SignedInGamers.Count > 0 ? Gamer.SignedInGamers[0] : null;
                    if (gamer is null)
                    {
                        Trace.WriteLine($"[wpr-xbox] achievement {key} unlocked, but there is no signed-in gamer");
                        return;
                    }

                    gamer.AwardAchievement(key);
                    Trace.WriteLine($"[wpr-xbox] achievement {key} awarded");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[wpr-xbox] awarding achievement {key} failed: {ex}");
                }
            });
        }

        public void PostResult(uint leaderboardId, int aggregation, long value)
        {
            // LeaderboardAggregation: Add=0, Replace=1, Min=2, Max=3.
            bool lowerIsBetter = aggregation == 2;
            _lowerIsBetter[leaderboardId] = lowerIsBetter;
            string board = BoardKey(leaderboardId);
            Trace.WriteLine($"[wpr-xbox] leaderboard {board} <- {value} (aggregation {aggregation})");

            try
            {
                OnlineBackend.Leaderboards?.Submit(_productId, _titleName, board, value);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-xbox] leaderboard post failed: {ex.Message}");
            }
        }

        public XboxLeaderboardPage? ReadLeaderboard(uint leaderboardId, uint skip, uint max, bool titleView)
        {
            ILeaderboardService? service = OnlineBackend.Leaderboards;
            if (service is null)
            {
                return null;
            }

            string board = BoardKey(leaderboardId);
            try
            {
                service.EnsureBoard(_productId, _titleName, board);
                if (!service.CanRead)
                {
                    return null;
                }

                // titleView: the whole title's board rather than the player's friends.
                LeaderboardView view = titleView ? LeaderboardView.Top : LeaderboardView.Friends;
                int limit = max == 0 ? 25 : (int)Math.Min(max, 100);
                Task<LeaderboardPage?> read = Task.Run(() => service.ReadAsync(_productId, board, view, (int)skip, limit));
                if (!read.Wait(ReadTimeout) || read.Result is not { } page)
                {
                    Trace.WriteLine($"[wpr-xbox] leaderboard {board} read gave nothing");
                    return null;
                }

                List<XboxLeaderboardRow> rows = page.Rows
                    .Select(r => new XboxLeaderboardRow((uint)Math.Max(r.Rank, 0), r.Username, r.Score, r.IsMe))
                    .ToList();
                Trace.WriteLine($"[wpr-xbox] leaderboard {board} ({view}) -> {rows.Count} row(s) of {page.TotalPlayers}");
                return new XboxLeaderboardPage(rows, (uint)Math.Max(page.TotalPlayers, 0));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-xbox] leaderboard {board} read failed: {ex.Message}");
                return null;
            }
        }

        private string BoardKey(uint leaderboardId) =>
            (_lowerIsBetter.TryGetValue(leaderboardId, out bool lower) && lower ? "BestTime" : "BestScore") +
            leaderboardId + ":0";
    }

    internal static class TaskWaitExtensions
    {
        public static bool Wait<T>(this Task<T> task, TimeSpan timeout, out T result)
        {
            if (task.Wait(timeout))
            {
                result = task.Result;
                return true;
            }

            result = default!;
            return false;
        }
    }
}
