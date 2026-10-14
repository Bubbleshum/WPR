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
    /// sort order from that prefix. An Add board is a running total, kept here because the hub
    /// keeps only a player's best. <c>PostResultsAsync</c>'s columns arrive through
    /// <see cref="PostResults"/>: the rating is posted, the rest ride along as columns.
    /// Reads block the guest for at most
    /// <see cref="ReadTimeout"/>: the image waits for the operation either way, and it is only
    /// ever asked for on a leaderboard screen.</para>
    /// </remarks>
    public sealed class Wp8NativeXboxHost : IXboxLiveHost
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

        private readonly string _productId;
        private readonly string? _titleName;
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

        public IReadOnlyList<uint> KnownLeaderboards()
        {
            lock (_boardLock)
            {
                return Boards.Keys.OrderBy(id => id).ToList();
            }
        }

        public void PostResult(uint leaderboardId, int aggregation, long value) =>
            Post(leaderboardId, aggregation, value, null);

        public void PostResults(uint leaderboardId, IReadOnlyList<XboxLeaderboardAttribute> attributes)
        {
            if (XboxLeaderboardAttribute.PickRating(attributes) is not { } rating || !rating.TryGetInt64(out long value))
            {
                Trace.WriteLine($"[wpr-xbox] leaderboard {leaderboardId}: {attributes.Count} column(s), none numeric - nothing posted");
                return;
            }

            // The other columns travel with the score, as an XNA title's extra columns do.
            Dictionary<string, string>? columns = attributes.Count > 1
                ? attributes.Where(a => a != rating)
                    .GroupBy(a => "Attribute" + a.AttributeId)
                    .ToDictionary(g => g.Key, g => g.Last().Value)
                : null;
            Post(leaderboardId, rating.Verb, value, columns);
        }

        private void Post(uint leaderboardId, int aggregation, long value, IReadOnlyDictionary<string, string>? columns)
        {
            // LeaderboardAggregation: Add=0, Replace=1, Min=2, Max=3. The hub keeps each player's
            // best per board, so Max and Replace are sent as they come and Min goes to a
            // lower-is-better board. Add was a running total the service kept: it is summed here
            // and the total sent.
            //
            // Nothing scored is not a score. Angry Birds Star Wars II posts 0 to all sixteen of its
            // boards at every start from a fresh save; sent, each would rank the player on every
            // board with nothing - and on a Min board 0 would be the unbeatable best time. The
            // board's aggregation is still remembered, so a read names the right board.
            bool nothing = (value == 0 && aggregation != 1) || (aggregation == 2 && value < 0);
            long sent = value;
            lock (_boardLock)
            {
                BoardState state = Boards.TryGetValue(leaderboardId, out BoardState? known) ? known : new BoardState();
                bool changed = state.Aggregation != aggregation || !Boards.ContainsKey(leaderboardId);
                state.Aggregation = aggregation;
                if (aggregation == 0 && !nothing)
                {
                    state.Total += value;
                    sent = state.Total;
                    changed = true;
                }

                Boards[leaderboardId] = state;
                if (changed)
                {
                    SaveBoards();
                }
            }

            if (nothing)
            {
                Trace.WriteLine($"[wpr-xbox] leaderboard {leaderboardId} <- {value} (aggregation {aggregation}) ignored: nothing scored");
                return;
            }

            string board = BoardKey(leaderboardId);
            Trace.WriteLine($"[wpr-xbox] leaderboard {board} <- {sent} (aggregation {aggregation}" +
                            (aggregation == 0 ? $", +{value}" : string.Empty) +
                            (columns is null ? string.Empty : $", {columns.Count} more column(s)") + ")");

            try
            {
                OnlineBackend.Leaderboards?.Submit(_productId, _titleName, board, sent, columns);
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

        /// <summary>
        /// <c>BestTime&lt;id&gt;:0</c> for a board the game posts to with Min, else
        /// <c>BestScore&lt;id&gt;:0</c>: the hub sorts an undefined board by that prefix. The
        /// aggregation is remembered across runs, so a board read before anything is posted to it
        /// in this run still names the right one.
        /// </summary>
        private string BoardKey(uint leaderboardId)
        {
            bool lower;
            lock (_boardLock)
            {
                lower = Boards.TryGetValue(leaderboardId, out BoardState? state) && state.Aggregation == 2;
            }

            return (lower ? "BestTime" : "BestScore") + leaderboardId + ":0";
        }

        // -------------------------------------------------------------------------------------
        // What WPR remembers per board, in <DataStore>/Online/wp8-leaderboards/<productId>.json:
        // its aggregation (for the key) and, for an Add board, the running total.
        // -------------------------------------------------------------------------------------

        private sealed class BoardState
        {
            public int Aggregation { get; set; } = 3;

            public long Total { get; set; }
        }

        private readonly object _boardLock = new();
        private Dictionary<uint, BoardState>? _boards;

        private Dictionary<uint, BoardState> Boards => _boards ??= LoadBoards();

        private string? BoardsPath =>
            Configuration.Current is { } config
                ? Path.Combine(config.DataPath("Online"), "wp8-leaderboards", _productId + ".json")
                : null;

        private Dictionary<uint, BoardState> LoadBoards()
        {
            try
            {
                if (BoardsPath is { } path && File.Exists(path))
                {
                    return System.Text.Json.JsonSerializer.Deserialize<Dictionary<uint, BoardState>>(File.ReadAllText(path)) ?? new();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-xbox] leaderboard state unreadable, starting over: {ex.Message}");
            }

            return new();
        }

        private void SaveBoards()
        {
            try
            {
                if (BoardsPath is { } path)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(Boards));
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-xbox] leaderboard state not saved: {ex.Message}");
            }
        }
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
