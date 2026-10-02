namespace WPR.Online.Hub.Client;

// Playtime and presence wire models and calls. The hub copies live in Playtime.cs / Social.cs,
// which also carry trackers of their own; WPR records sessions in its local database instead
// (WPR.Engine.Online.PlaytimeTracker), so only the calls are here.

public sealed record PlaySession(string SessionId, string TitleId, string? TitleName, DateTimeOffset StartedAt, DateTimeOffset EndedAt, long Seconds);

public sealed record PresenceBeat(int NextHeartbeatSeconds, int FriendsOnline, int UnreadMessages, int PendingFriendRequests);

public static class PresenceStatuses
{
    public const string Online = "online";
    public const string Offline = "offline";

    /// <summary>
    /// "Appear offline": the hub records the session as appear-offline and friends see the player as
    /// offline, last seen when they were last visibly online.
    /// </summary>
    public const string AppearOffline = "invisible";
}

public static class HubProgressExtensions
{
    /// <summary>Idempotent: a session re-sent with the same id keeps its longest copy.</summary>
    public static Task SyncPlaytimeAsync(this HubClient hub, IReadOnlyList<PlaySession> sessions, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Post, "api/me/playtime", new { sessions }, ct);

    public static Task<PresenceBeat> SendPresenceAsync(this HubClient hub, string status, string? titleId, string? titleName,
        CancellationToken ct = default) =>
        hub.SendAuthedAsync<PresenceBeat>(HttpMethod.Put, "api/me/presence",
            new { status, title_id = titleId is null ? null : TitleIds.Normalize(titleId), title_name = titleName }, ct);
}
