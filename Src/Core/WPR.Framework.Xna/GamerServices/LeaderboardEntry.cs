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
        internal static LeaderboardEntry FromRow(Gamer gamer, long rating, System.Collections.Generic.IReadOnlyDictionary<string, string>? columns)
        {
            LeaderboardEntry entry = new LeaderboardEntry { _Gamer = gamer, _Rating = rating };
            if (columns != null)
            {
                entry._Columns!.BeginFillData();
                foreach (var column in columns)
                {
                    // Numbers come back as their JSON text, so give games the typed value their
                    // GetValueInt32 / GetValueInt64 reads expect rather than a string.
                    if (long.TryParse(column.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                        entry._Columns.SetValue(column.Key, number);
                    else
                        entry._Columns.SetValue(column.Key, column.Value);
                }
                entry._Columns.EndFillData();
            }
            return entry;
        }
    }
}
