using System;
using System.Globalization;
using System.Threading;

namespace Microsoft.Xna.Framework.GamerServices
{
    public class LeaderboardEntry
    {
        private Gamer? _Gamer;
        private PropertyDictionary? _Columns;
        private long _Rating;

        // Set only on entries a LeaderboardWriter handed out. Entries a LeaderboardReader builds
        // from the hub have none, so assigning to them (which XNA forbids anyway) sends nothing.
        private LeaderboardWriter? _Writer;
        private int _CommitArmed;

        internal string BoardKey { get; private set; } = "";

        public Gamer? Gamer
        {
            get => _Gamer;
            set => _Gamer = value;
        }

        public PropertyDictionary? Columns
        {
            get => _Columns;
            set => _Columns = value;
        }

        public long Rating
        {
            get => _Rating;
            set
            {
                _Rating = value;
                _Writer?.RatingAssigned(this);
            }
        }

        public LeaderboardEntry()
        {
            _Columns = new PropertyDictionary(false, 20);
        }

        internal void AttachWriter(LeaderboardWriter writer, string boardKey)
        {
            _Writer = writer;
            BoardKey = boardKey;
        }

        /// <summary>
        /// True for the first rating assignment since the last commit. A game that sets Rating
        /// several times in one Update sends one write, carrying the last value.
        /// </summary>
        internal bool TryArmCommit() => Interlocked.Exchange(ref _CommitArmed, 1) == 0;

        internal void DisarmCommit() => Interlocked.Exchange(ref _CommitArmed, 0);

        /// <summary>Builds a read-only entry from a hub row.</summary>
        /// <param name="leaderboardKey">The board's <see cref="LeaderboardIdentity.Key"/>, which decides
        /// the column that carries the rating (<see cref="RatingColumn"/>).</param>
        internal static LeaderboardEntry FromRow(Gamer gamer, long rating, System.Collections.Generic.IReadOnlyDictionary<string, string>? columns, string? leaderboardKey)
        {
            LeaderboardEntry entry = new LeaderboardEntry { _Gamer = gamer, _Rating = rating };
            entry._Columns!.BeginFillData();
            if (columns != null)
            {
                foreach (var column in columns)
                {
                    // Numbers come back as their JSON text, so give games the typed value their
                    // GetValueInt32 / GetValueInt64 reads expect rather than a string.
                    if (long.TryParse(column.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                        entry._Columns.SetValue(column.Key, number);
                    else
                        entry._Columns.SetValue(column.Key, column.Value);
                }
            }

            // The rating's own column. Games set Rating to post a score but read it back by column
            // name. Zuma's Revenge, Cut the Rope, Doodle Jump and most others call
            // Columns.GetValueInt32("BestScore"), and Mirror's Edge reads "BestTime". On Xbox LIVE
            // the standard leaderboards always filled that column from the rating. The hub stores
            // only the columns a game set, which for those games is none, so without this every
            // score on the board read as 0. A value the game wrote itself wins.
            string? ratingColumn = RatingColumn(leaderboardKey);
            if (ratingColumn != null && !entry._Columns.ContainsKey(ratingColumn))
                entry._Columns.SetValue(ratingColumn, rating);

            entry._Columns.EndFillData();
            return entry;
        }

        /// <summary>
        /// The column a standard XNA leaderboard keeps its rating in: <c>BestScore</c> for the
        /// <c>BestScore*</c> keys, <c>BestTime</c> for <c>BestTime*</c>. Null for anything else.
        /// </summary>
        internal static string? RatingColumn(string? leaderboardKey)
        {
            if (string.IsNullOrEmpty(leaderboardKey)) return null;
            if (leaderboardKey.StartsWith("BestScore", StringComparison.Ordinal)) return "BestScore";
            if (leaderboardKey.StartsWith("BestTime", StringComparison.Ordinal)) return "BestTime";
            return null;
        }
    }
}
