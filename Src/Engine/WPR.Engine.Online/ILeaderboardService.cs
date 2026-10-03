using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// Online leaderboards, backing XNA's <c>LeaderboardWriter</c> and <c>LeaderboardReader</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Writes are offline-first and never fail.</b> A score is queued on disk the moment
    /// the game posts it and sent once a player is signed in. The hub keeps each player's best per
    /// board, so every write can be sent as-is: deciding which score is "better" is the hub's
    /// job, because only it knows a board's sort order.</para>
    ///
    /// <para><b>Reads need a signed-in player</b>, because the hub serves leaderboards to
    /// signed-in players only. <see cref="ReadAsync"/> returns null when nobody is signed in,
    /// when offline, and on any error, and the caller then shows the game the empty leaderboard
    /// it has always seen.</para>
    ///
    /// <para><b>Board keys</b> are <c>&lt;LeaderboardKey&gt;:&lt;GameMode&gt;</c>, e.g.
    /// <c>BestScoreLifeTime:0</c>, which is what the hub's definitions repo uses.</para>
    /// </remarks>
    public interface ILeaderboardService
    {
        /// <summary>True when reads can succeed, i.e. a player is signed in.</summary>
        bool CanRead { get; }

        /// <summary>Queue a score. Must not throw, and must not wait on the network.</summary>
        void Submit(string titleId, string? titleName, string boardKey, long score, IReadOnlyDictionary<string, string>? columns = null);

        /// <summary>
        /// Make sure the board exists on the hub, creating it if nobody has yet. Called whenever
        /// a game reads a board, signed in or not, so the hub learns every board a game uses the
        /// first time it asks for one. Fire-and-forget: must not throw or wait on the network,
        /// and repeated calls for the same board are cheap.
        /// </summary>
        void EnsureBoard(string titleId, string? titleName, string boardKey);

        Task<LeaderboardPage?> ReadAsync(string titleId, string boardKey, LeaderboardView view, int offset, int limit, CancellationToken ct = default);

        /// <summary>Send queued scores if signed in. Never throws.</summary>
        Task FlushAsync();
    }

    public enum LeaderboardView
    {
        Top,
        AroundMe,
        Friends,
    }

    /// <summary>One ranked row. <see cref="IsMe"/> marks the signed-in player's own entry.</summary>
    /// <param name="Gamerpic">The player's gamerpic as PNG bytes, or null when they have none or it
    /// could not be fetched. Fetched before the page is handed back, because games ask for it
    /// synchronously from their draw code.</param>
    public sealed record LeaderboardRow(int Rank, string Username, long Score, bool IsMe, IReadOnlyDictionary<string, string>? Columns,
        byte[]? Gamerpic = null);

    /// <param name="Rows">This page, best first.</param>
    /// <param name="TotalPlayers">Everyone on the board, not just this page.</param>
    public sealed record LeaderboardPage(IReadOnlyList<LeaderboardRow> Rows, int TotalPlayers);
}
