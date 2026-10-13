namespace WPR.Wp8Native
{
    /// <summary>
    /// What stands behind <c>Microsoft.Xbox</c> when a host has something real to offer:
    /// the player's gamertag, a place to record achievements, and an online leaderboard.
    /// </summary>
    /// <remarks>
    /// The probe runs without one, and <see cref="WinRtRuntime"/> then answers as it always has -
    /// a signed-in player with no achievements and empty boards. WPR's game host implements this
    /// over its achievement store and WPR Hub. Everything here is called on the emulator thread
    /// in the middle of a guest call, so it must not throw, and anything that waits on the
    /// network must give up quickly.
    /// </remarks>
    public interface IXboxLiveHost
    {
        string Gamertag { get; }

        /// <summary>Every achievement known for this title, earned or not.</summary>
        IReadOnlyList<XboxAchievement> GetAchievements();

        /// <summary><c>IUser::UnlockAchievementAsync</c>. Unlocking one already earned is normal.</summary>
        void UnlockAchievement(uint achievementId);

        /// <summary><c>ILeaderboardService::PostResultAsync</c>. <paramref name="aggregation"/> is
        /// Add=0, Replace=1, Min=2, Max=3.</summary>
        void PostResult(uint leaderboardId, int aggregation, long value);

        /// <summary><c>ILeaderboardService::GetLeaderboardAsync</c>; null when there is nothing to show.</summary>
        XboxLeaderboardPage? ReadLeaderboard(uint leaderboardId, uint skip, uint max, bool titleView);
    }

    public sealed record XboxAchievement(
        uint Id,
        string Name,
        string Description,
        int Gamerscore,
        bool IsSecret,
        bool IsEarned,
        DateTime? Unlocked);

    public sealed record XboxLeaderboardRow(uint Rank, string Gamertag, long Rating, bool IsMe);

    public sealed record XboxLeaderboardPage(IReadOnlyList<XboxLeaderboardRow> Rows, uint TotalRecords);
}
