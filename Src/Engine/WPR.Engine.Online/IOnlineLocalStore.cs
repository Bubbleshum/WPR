using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// The local database's side of online progress: which achievement unlocks and play sessions
    /// are still waiting to reach WPR Hub, and the calls that mark them uploaded.
    /// </summary>
    /// <remarks>
    /// <para>Implemented by <c>WPR.Database</c> (<c>EfOnlineLocalStore</c>), because that is where
    /// the achievements already live, and handed to the hub module as a constructor argument. The
    /// contract speaks strings, Guids and times only, like every contract in this project.</para>
    ///
    /// <para><b>An unlock is always recorded locally first.</b> "Awaiting upload" is an earned row
    /// whose <c>EarnedOnline</c> is false. The hub sync sets it true once the hub accepts the unlock,
    /// and until then it is retried on every flush. So offline, signed out and "the send failed"
    /// are all the same state, and there is only one path to online.</para>
    /// </remarks>
    public interface IOnlineLocalStore
    {
        /// <summary>Earned achievements the hub has not confirmed yet, oldest first.</summary>
        Task<IReadOnlyList<PendingUnlock>> GetPendingUnlocksAsync(int max);

        /// <summary>The hub accepted these; mark them uploaded (<c>EarnedOnline</c> = true).</summary>
        Task MarkUnlocksUploadedAsync(IReadOnlyList<PendingUnlock> unlocks);

        /// <summary>Insert or update a play session by its id. Called at start, every checkpoint, and at the end.</summary>
        Task SavePlaySessionAsync(PlaySessionRecord session);

        /// <summary>Sessions not yet confirmed by the hub, including one still running.</summary>
        Task<IReadOnlyList<PlaySessionRecord>> GetPendingPlaySessionsAsync(int max);

        /// <summary>The hub accepted these, and they had ended, so they will not change again.</summary>
        Task MarkPlaySessionsUploadedAsync(IReadOnlyList<Guid> sessionIds);

        /// <summary>Total seconds played in a game before a moment, for "unlocked after 2 minutes of play".</summary>
        Task<long> GetPlaytimeSecondsAsync(string titleId, DateTimeOffset before);
    }

    /// <param name="LocalId">The local row, for marking it uploaded.</param>
    public sealed record PendingUnlock(long LocalId, string TitleId, string Key, DateTimeOffset UnlockedAt, string? TitleName);

    /// <summary>One continuous run of a game. <see cref="EndedAt"/> is null while it is still running.</summary>
    public sealed record PlaySessionRecord(
        Guid SessionId,
        string TitleId,
        string? TitleName,
        DateTimeOffset StartedAt,
        DateTimeOffset LastSeenAt,
        DateTimeOffset? EndedAt,
        long Seconds);
}
