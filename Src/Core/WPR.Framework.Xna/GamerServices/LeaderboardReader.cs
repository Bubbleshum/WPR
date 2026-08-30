using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

using WPR.Common;

namespace Microsoft.Xna.Framework.GamerServices
{
    public class LeaderboardReader : IDisposable
    {
        private ReadOnlyCollection<LeaderboardEntry>? _Entries;
        public ReadOnlyCollection<LeaderboardEntry>? Entries => this._Entries;

        public LeaderboardReader() : this(default)
        {
        }

        public LeaderboardReader(LeaderboardIdentity identity)
        {
            LeaderboardIdentity = identity;
            _Entries = new ReadOnlyCollection<LeaderboardEntry>(new List<LeaderboardEntry>());
        }

        // Empty-leaderboard stub surface. The game treats this reader as "no scores
        // available": no further pages, nothing to sync, an empty entries collection.
        public bool CanPageDown => false;
        public bool CanPageUp => false;
        public bool IsDisposed { get; private set; }
        public bool IsSynchronizedWithLiveServer => false;
        // Real XNA member name. Games read reader.LeaderboardIdentity back off the reader
        // returned by EndRead — Battleship's XBOXLive.LeaderboardReadCallback does
        // `reader.LeaderboardIdentity.GameMode` to pick which leaderboards[] slot to fill.
        // Captured at BeginRead so GameMode round-trips the slot index the caller passed in;
        // without it (the shim previously mis-named this "Leaderboard" and discarded the
        // identity) the game threw MissingMethodException on get_LeaderboardIdentity() every
        // leaderboard read.
        public LeaderboardIdentity LeaderboardIdentity { get; internal set; }
        public int PageSize => 0;
        public int PageStart => 0;
        public int TotalLeaderboardSize => 0;

        public IAsyncResult BeginPageDown(AsyncCallback callback, object asyncState)
        {
            return StubUtils.ForeverTask;
        }
        public IAsyncResult BeginPageUp(AsyncCallback callback, object asyncState)
        {
            return StubUtils.ForeverTask;
        }
        public static IAsyncResult BeginRead(LeaderboardIdentity leaderb,
            int pageStart, int pageSize, AsyncCallback callback, object asyncState)
        {
            return CompleteRead(leaderb, callback, asyncState);
        }

        public static IAsyncResult BeginRead(
          LeaderboardIdentity leaderboardId,
          Gamer pivotGamer,
          int pageSize,
          AsyncCallback callback,
          object asyncState)
        {
            return CompleteRead(leaderboardId, callback, asyncState);
        }

        public static IAsyncResult BeginRead(
          LeaderboardIdentity leaderboardId,
          IEnumerable<Gamer> gamers,
          Gamer pivotGamer,
          int pageSize,
          AsyncCallback callback,
          object asyncState)
        {
            return CompleteRead(leaderboardId, callback, asyncState);
        }

        private static IAsyncResult CompleteRead(LeaderboardIdentity identity, AsyncCallback? callback, object? asyncState)
        {
            var reader = new LeaderboardReader(identity);
            var task = Task.FromResult(reader);
            callback?.Invoke(task);
            return task;
        }

        public static LeaderboardReader EndRead(IAsyncResult result)
        {
            return ((Task<LeaderboardReader>)result).GetAwaiter().GetResult();
        }

        public void EndPageDown(IAsyncResult result) { }
        public void EndPageUp(IAsyncResult result) { }

        public void PageDown() { }
        public void PageUp() { }

        public void Dispose() { IsDisposed = true; }
    }
}
