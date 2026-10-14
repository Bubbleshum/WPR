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

        /// <summary>
        /// <c>ILeaderboardService::PostResultsAsync</c>: several columns at once. The default posts
        /// the rating (<see cref="XboxLeaderboardAttribute.PickRating"/>) through
        /// <see cref="PostResult"/> and drops the other columns.
        /// </summary>
        /// <summary>
        /// Boards the host knows this title uses, for <c>GetLeaderboardsAsync</c> when the title's
        /// SPA defines none (see <see cref="XboxTitleConfig"/>): the ones it has posted to.
        /// </summary>
        IReadOnlyList<uint> KnownLeaderboards() => [];

        void PostResults(uint leaderboardId, IReadOnlyList<XboxLeaderboardAttribute> attributes)
        {
            if (XboxLeaderboardAttribute.PickRating(attributes) is { } rating && rating.TryGetInt64(out long value))
            {
                PostResult(leaderboardId, rating.Verb, value);
            }
        }
    }

    /// <summary>
    /// One <c>Microsoft.Xbox.Leaderboards.LeaderboardAttribute</c>: a column id, how it aggregates
    /// (Add=0, Replace=1, Min=2, Max=3), its value type (Unknown=0, Int32=1, Int64=2, Float=3)
    /// and the value, which the API carries as a string.
    /// </summary>
    public sealed record XboxLeaderboardAttribute(uint AttributeId, int Verb, int Type, string Value)
    {
        /// <summary>The value as a whole number; a Float column is rounded.</summary>
        public bool TryGetInt64(out long value)
        {
            string text = Value.Trim();
            if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) &&
                !double.IsNaN(d) && !double.IsInfinity(d))
            {
                value = (long)Math.Round(d);
                return true;
            }

            value = 0;
            return false;
        }

        /// <summary>
        /// The column a board ranks by. The API does not mark it, so: the lowest attribute id that
        /// holds a number - column 0 when a title numbers from zero, and the only column when
        /// there is one, which is the common case.
        /// </summary>
        public static XboxLeaderboardAttribute? PickRating(IReadOnlyList<XboxLeaderboardAttribute> attributes) =>
            attributes.Where(a => a.TryGetInt64(out _)).OrderBy(a => a.AttributeId).FirstOrDefault();
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
