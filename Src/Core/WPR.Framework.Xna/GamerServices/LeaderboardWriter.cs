using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

namespace Microsoft.Xna.Framework.GamerServices
{
    /// <summary>
    /// XNA's leaderboard write path, backed by WPR Hub (<c>WPR.Engine.Online.OnlineBackend.Leaderboards</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>The XNA idiom is fetch-then-assign</b>:
    /// <c>var e = gamer.LeaderboardWriter.GetLeaderboard(id); e.Rating = score; e.Columns.SetValue("Level", 3);</c>.
    /// There is no commit call - on Xbox LIVE the writer flushed on its own from
    /// <c>GamerServicesDispatcher</c>. So the entry reports a <see cref="LeaderboardEntry.Rating"/>
    /// assignment here, and the write is sent on the next game-thread pump rather than inside the
    /// setter, which is what lets columns the game sets on the lines after <c>Rating</c> go with
    /// it.</para>
    ///
    /// <para><b>One entry per board</b>, so a game writing two boards in a frame (a score and a
    /// time, say) gets two writes. The previous stub handed every identity the same entry.</para>
    ///
    /// <para>With no online service composed, or nobody signed in, this still queues: the hub
    /// takes scores only from signed-in players, and anything played before sign-in is sent the
    /// first time a token appears.</para>
    /// </remarks>
    public class LeaderboardWriter
    {
        /// <summary>The hub keeps a handful of small columns; anything bigger is dropped, not truncated.</summary>
        private const int MaxColumns = 10;
        private const int MaxColumnChars = 64;

        private readonly Dictionary<string, LeaderboardEntry> _entries = new Dictionary<string, LeaderboardEntry>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public LeaderboardWriter()
        {
        }

        public LeaderboardEntry GetLeaderboard(LeaderboardIdentity identity)
        {
            string key = BoardKey(identity);
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out LeaderboardEntry entry))
                {
                    entry = new LeaderboardEntry();
                    entry.AttachWriter(this, key);
                    _entries[key] = entry;
                }
                return entry;
            }
        }

        /// <summary>The hub's board key: <c>&lt;LeaderboardKey&gt;:&lt;GameMode&gt;</c>.</summary>
        internal static string BoardKey(LeaderboardIdentity identity) =>
            (identity.Key ?? "") + ":" + identity.GameMode.ToString(CultureInfo.InvariantCulture);

        /// <summary>Called by an entry of ours when its rating is assigned.</summary>
        internal void RatingAssigned(LeaderboardEntry entry)
        {
            if (!entry.TryArmCommit()) return; // already scheduled for this pump

            WPR.Xna.Rhi.XnaBackend.PostToGameThread(() => Commit(entry));
        }

        private static void Commit(LeaderboardEntry entry)
        {
            entry.DisarmCommit();

            try
            {
                WPR.Engine.Online.ILeaderboardService service = WPR.Engine.Online.OnlineBackend.Leaderboards;
                string productId = WPR.Common.WprHostEnvironment.CurrentProductId;
                if (service == null || string.IsNullOrEmpty(productId)) return;

                service.Submit(productId, WPR.Common.WprHostEnvironment.CurrentTitleName,
                    entry.BoardKey, entry.Rating, SmallColumns(entry.Columns));
            }
            catch (Exception ex)
            {
                // A leaderboard must never be why a game falls over.
                WprDebugTrace.WriteLine("[wpr-ex] LeaderboardWriter commit threw: " + ex);
            }
        }

        /// <summary>
        /// The game's columns, reduced to what is worth showing beside a score: plain values only.
        /// Blob columns (streams) are how games store replays and ghost data, and they stay local.
        /// </summary>
        private static IReadOnlyDictionary<string, string> SmallColumns(PropertyDictionary columns)
        {
            if (columns == null || columns.Count == 0) return null;

            Dictionary<string, string> result = new Dictionary<string, string>();
            foreach (KeyValuePair<string, object> column in columns)
            {
                if (result.Count >= MaxColumns) break;
                if (column.Value == null || column.Value is Stream) continue;

                string text = Convert.ToString(column.Value, CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(text) || text.Length > MaxColumnChars) continue;
                result[column.Key] = text;
            }
            return result.Count == 0 ? null : result;
        }
    }
}
