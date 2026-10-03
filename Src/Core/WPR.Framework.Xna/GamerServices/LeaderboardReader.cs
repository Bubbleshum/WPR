using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using WPR.Common;
using WPR.Engine.Online;

namespace Microsoft.Xna.Framework.GamerServices
{
    /// <summary>
    /// XNA's leaderboard read path, backed by WPR Hub (<c>OnlineBackend.Leaderboards</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Empty is still the normal answer.</b> The hub serves leaderboards to signed-in
    /// players only, so with nobody signed in, no online service composed, or the network down,
    /// every read completes with no entries - exactly what this stub returned before the hub
    /// existed, and what games already handle.</para>
    ///
    /// <para>The three <c>BeginRead</c> overloads map onto the hub's three views: a page from a
    /// rank is <c>top</c>, a page around a pivot gamer is <c>around_me</c> (the hub only knows
    /// the signed-in player, which is the only pivot WP7 games pass), and a list of gamers is
    /// <c>friends</c>.</para>
    ///
    /// <para><b>The callback runs on the game thread</b>, posted the same way the achievement
    /// callbacks are, because games touch their own state from it. The returned IAsyncResult
    /// carries the caller's <c>asyncState</c>; the previous stub returned a bare
    /// <c>Task.FromResult</c>, whose AsyncState is always null.</para>
    /// </remarks>
    public class LeaderboardReader : IDisposable
    {
        private const int DefaultPageSize = 25;

        private ReadOnlyCollection<LeaderboardEntry>? _Entries;
        public ReadOnlyCollection<LeaderboardEntry>? Entries => this._Entries;

        private LeaderboardView _view = LeaderboardView.Top;
        private int _pageSize = DefaultPageSize;
        private int _pageStart;
        private int _total;
        private bool _fromServer;

        public LeaderboardReader() : this(default)
        {
        }

        public LeaderboardReader(LeaderboardIdentity identity)
        {
            LeaderboardIdentity = identity;
            _Entries = new ReadOnlyCollection<LeaderboardEntry>(new List<LeaderboardEntry>());
        }

        // Paging is only meaningful for a ranked page: around_me and friends are one window each.
        public bool CanPageDown => _view == LeaderboardView.Top && _pageStart + (_Entries?.Count ?? 0) < _total;
        public bool CanPageUp => _view == LeaderboardView.Top && _pageStart > 0;
        public bool IsDisposed { get; private set; }
        public bool IsSynchronizedWithLiveServer => _fromServer;
        // Real XNA member name. Games read reader.LeaderboardIdentity back off the reader
        // returned by EndRead — Battleship's XBOXLive.LeaderboardReadCallback does
        // `reader.LeaderboardIdentity.GameMode` to pick which leaderboards[] slot to fill.
        // Captured at BeginRead so GameMode round-trips the slot index the caller passed in;
        // without it (the shim previously mis-named this "Leaderboard" and discarded the
        // identity) the game threw MissingMethodException on get_LeaderboardIdentity() every
        // leaderboard read.
        public LeaderboardIdentity LeaderboardIdentity { get; internal set; }
        public int PageSize => _pageSize;
        public int PageStart => _pageStart;
        public int TotalLeaderboardSize => _total;

        // ------------------------------------------------------------------ reads

        public static IAsyncResult BeginRead(LeaderboardIdentity leaderb,
            int pageStart, int pageSize, AsyncCallback callback, object asyncState)
        {
            return Start(ReadAsync(leaderb, LeaderboardView.Top, pageStart, pageSize), () => new LeaderboardReader(leaderb), callback, asyncState);
        }

        public static IAsyncResult BeginRead(
          LeaderboardIdentity leaderboardId,
          Gamer pivotGamer,
          int pageSize,
          AsyncCallback callback,
          object asyncState)
        {
            return Start(ReadAsync(leaderboardId, PivotView(pivotGamer), 0, pageSize), () => new LeaderboardReader(leaderboardId), callback, asyncState);
        }

        public static IAsyncResult BeginRead(
          LeaderboardIdentity leaderboardId,
          IEnumerable<Gamer> gamers,
          Gamer pivotGamer,
          int pageSize,
          AsyncCallback callback,
          object asyncState)
        {
            return Start(ReadAsync(leaderboardId, LeaderboardView.Friends, 0, pageSize), () => new LeaderboardReader(leaderboardId), callback, asyncState);
        }

        public static LeaderboardReader EndRead(IAsyncResult result)
        {
            return ((Task<LeaderboardReader>)result).GetAwaiter().GetResult();
        }

        // Synchronous forms, as XNA has them. They block the caller on the network, as they did on
        // Xbox LIVE; with nobody signed in they return at once.
        public static LeaderboardReader Read(LeaderboardIdentity leaderboardId, int pageStart, int pageSize) =>
            ReadAsync(leaderboardId, LeaderboardView.Top, pageStart, pageSize).GetAwaiter().GetResult();

        public static LeaderboardReader Read(LeaderboardIdentity leaderboardId, Gamer pivotGamer, int pageSize) =>
            ReadAsync(leaderboardId, PivotView(pivotGamer), 0, pageSize).GetAwaiter().GetResult();

        public static LeaderboardReader Read(LeaderboardIdentity leaderboardId, IEnumerable<Gamer> gamers, Gamer pivotGamer, int pageSize) =>
            ReadAsync(leaderboardId, LeaderboardView.Friends, 0, pageSize).GetAwaiter().GetResult();

        // ------------------------------------------------------------------ paging

        public IAsyncResult BeginPageDown(AsyncCallback callback, object asyncState)
        {
            return Start(PageAsync(+1), () => this, callback, asyncState);
        }

        public IAsyncResult BeginPageUp(AsyncCallback callback, object asyncState)
        {
            return Start(PageAsync(-1), () => this, callback, asyncState);
        }

        public void EndPageDown(IAsyncResult result) => ((Task<LeaderboardReader>)result).GetAwaiter().GetResult();
        public void EndPageUp(IAsyncResult result) => ((Task<LeaderboardReader>)result).GetAwaiter().GetResult();

        public void PageDown() => PageAsync(+1).GetAwaiter().GetResult();
        public void PageUp() => PageAsync(-1).GetAwaiter().GetResult();

        public void Dispose() { IsDisposed = true; }

        // ------------------------------------------------------------------ plumbing

        private static LeaderboardView PivotView(Gamer pivotGamer) =>
            pivotGamer == null || pivotGamer is SignedInGamer ? LeaderboardView.AroundMe : LeaderboardView.Top;

        private static async Task<LeaderboardReader> ReadAsync(LeaderboardIdentity identity, LeaderboardView view, int pageStart, int pageSize)
        {
            LeaderboardReader reader = new LeaderboardReader(identity)
            {
                _view = view,
                _pageSize = pageSize > 0 ? Math.Min(pageSize, 100) : DefaultPageSize,
                _pageStart = Math.Max(0, pageStart),
            };
            await reader.FillAsync().ConfigureAwait(false);
            return reader;
        }

        private async Task<LeaderboardReader> PageAsync(int direction)
        {
            if (direction > 0 ? CanPageDown : CanPageUp)
            {
                _pageStart = Math.Max(0, _pageStart + direction * _pageSize);
                await FillAsync().ConfigureAwait(false);
            }
            return this;
        }

        private async Task FillAsync()
        {
            ILeaderboardService service = OnlineBackend.Leaderboards;
            string productId = WprHostEnvironment.CurrentProductId;
            if (service == null || string.IsNullOrEmpty(productId)) return;

            // Every board a game asks for is made to exist on the hub, signed in or not, so boards
            // appear there as soon as a game uses them rather than only after the first score.
            try { service.EnsureBoard(productId, WprHostEnvironment.CurrentTitleName, LeaderboardWriter.BoardKey(LeaderboardIdentity)); }
            catch (Exception ex) { WprDebugTrace.WriteLine("[wpr-ex] LeaderboardReader EnsureBoard threw: " + ex); }

            if (!service.CanRead) return;

            WPR.Engine.Online.LeaderboardPage page;
            try
            {
                page = await service.ReadAsync(productId, LeaderboardWriter.BoardKey(LeaderboardIdentity),
                    _view, _pageStart, _pageSize).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WprDebugTrace.WriteLine("[wpr-ex] LeaderboardReader read threw: " + ex);
                return;
            }
            if (page == null) return;

            List<LeaderboardEntry> entries;
            try
            {
                SignedInGamer me = Gamer.SignedInGamers.Count > 0 ? Gamer.SignedInGamers[0] : null;
                entries = page.Rows
                    .Select(row => LeaderboardEntry.FromRow(row.IsMe && me != null ? me : new LeaderboardGamer(row.Username, row.Gamerpic), row.Score, row.Columns, LeaderboardIdentity.Key))
                    .ToList();
            }
            catch (Exception ex)
            {
                // Same answer as a failed read: the empty board the game has always handled.
                WprDebugTrace.WriteLine("[wpr-ex] LeaderboardReader could not build entries: " + ex);
                return;
            }

            _Entries = new ReadOnlyCollection<LeaderboardEntry>(entries);
            _total = page.TotalPlayers;
            _fromServer = true;
            if (_view != LeaderboardView.Top && page.Rows.Count > 0) _pageStart = Math.Max(0, page.Rows[0].Rank - 1);
        }

        /// <summary>
        /// Adapts a read to XNA's Begin/End shape: an IAsyncResult that carries the caller's
        /// state, and a callback delivered on the game thread once the read is done.
        /// </summary>
        private static IAsyncResult Start(Task<LeaderboardReader> read, Func<LeaderboardReader> fallback, AsyncCallback callback, object asyncState)
        {
            TaskCompletionSource<LeaderboardReader> source = new TaskCompletionSource<LeaderboardReader>(asyncState);
            read.ContinueWith(t =>
            {
                // A read never faults to the game: EndRead would throw inside the game's own
                // callback, which on Xbox LIVE only happened for a real network error and which
                // most WP7 titles do not guard. Anything unexpected is an empty board instead.
                if (t.IsFaulted || t.IsCanceled)
                {
                    WprDebugTrace.WriteLine("[wpr-ex] LeaderboardReader read faulted: " + t.Exception);
                    source.TrySetResult(fallback());
                }
                else source.TrySetResult(t.Result);

                if (callback != null)
                {
                    WPR.Xna.Rhi.XnaBackend.PostToGameThread(() => callback(source.Task));
                }
            }, TaskScheduler.Default);
            return source.Task;
        }
    }

    /// <summary>
    /// Another player on a leaderboard. XNA has no public type for this - on Xbox LIVE it was an
    /// internal gamer too - so games only ever see it as a <see cref="Gamer"/>.
    /// </summary>
    internal sealed class LeaderboardGamer : Gamer
    {
        private readonly byte[] _picture;

        internal LeaderboardGamer(string gamertag, byte[] picture)
        {
            Gamertag = gamertag;
            _picture = picture;
        }

        // GetProfile().GetGamerPicture() is how games draw the picture beside each row.
        internal override bool IsOtherPlayer => true;
        internal override byte[] PictureBytes => _picture;
    }
}
