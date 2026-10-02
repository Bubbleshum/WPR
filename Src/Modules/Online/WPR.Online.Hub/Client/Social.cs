namespace WPR.Online.Hub.Client;

// Friends and the player's games, for the WPR Hub page of both heads. Shapes follow the hub's
// SocialController::friends / requests and PlaytimeService::gamesFor.

public sealed record FriendPresence(string Status, string? TitleId, string? TitleName, DateTimeOffset? LastSeenAt)
{
    public bool IsOnline => Status != PresenceStatuses.Offline;
}

public sealed record Friend(string Username, string? GamerpicUrl, DateTimeOffset? FriendsSince, FriendPresence Presence, int UnreadMessages);

public sealed record FriendList(IReadOnlyList<Friend> Friends, int MaxFriends);

public sealed record IncomingFriendRequest(long Id, string From, string? GamerpicUrl, DateTimeOffset SentAt);

public sealed record OutgoingFriendRequest(long Id, string To, DateTimeOffset SentAt);

public sealed record FriendRequests(IReadOnlyList<IncomingFriendRequest> Incoming, IReadOnlyList<OutgoingFriendRequest> Outgoing);

/// <summary>"sent", or "accepted" when they had already asked you.</summary>
public sealed record FriendRequestResult(string Status, long RequestId);

public sealed record HubGame(
    string TitleId,
    string Name,
    string? IconUrl,
    long PlaytimeSeconds,
    int Sessions,
    DateTimeOffset? FirstPlayedAt,
    DateTimeOffset? LastPlayedAt,
    int AchievementsUnlocked,
    int AchievementsTotal,
    int Points,
    int PointsTotal);

public sealed record HubGameList(IReadOnlyList<HubGame> Games);

/// <summary>
/// One item of a friend's latest activity. Type is <c>achievement</c> (Name, Description, Points, IconUrl)
/// or <c>played</c> (StartedAt, Seconds); At is when it happened (a play session's end). The hub leaves
/// out anything from while the friend was appearing offline, and masks secret achievements the
/// viewer has not unlocked as "Secret achievement".
/// </summary>
public sealed record FriendActivity(
    string Type,
    DateTimeOffset At,
    string TitleId,
    string TitleName,
    string? TitleIconUrl,
    string? Name = null,
    string? Description = null,
    int Points = 0,
    string? IconUrl = null,
    DateTimeOffset? StartedAt = null,
    long Seconds = 0)
{
    public const string Achievement = "achievement";
    public const string Played = "played";
}

/// <summary>A friend's page (hub: SocialController::friend). Totals are over everything they have done.</summary>
public sealed record FriendProfile(
    string Username,
    string? GamerpicUrl,
    DateTimeOffset? FriendsSince,
    FriendPresence Presence,
    int Games,
    int Achievements,
    int Points,
    IReadOnlyList<FriendActivity> Activity);

public static class HubSocialExtensions
{
    public static Task<FriendList> GetFriendsAsync(this HubClient hub, CancellationToken ct = default) =>
        hub.SendAuthedAsync<FriendList>(HttpMethod.Get, "api/friends", null, ct);

    public static Task<FriendRequests> GetFriendRequestsAsync(this HubClient hub, CancellationToken ct = default) =>
        hub.SendAuthedAsync<FriendRequests>(HttpMethod.Get, "api/friends/requests", null, ct);

    public static Task<FriendRequestResult> SendFriendRequestAsync(this HubClient hub, string username, CancellationToken ct = default) =>
        hub.SendAuthedAsync<FriendRequestResult>(HttpMethod.Post, "api/friends/requests", new { username }, ct);

    public static Task AcceptFriendRequestAsync(this HubClient hub, long requestId, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Post, $"api/friends/requests/{requestId}/accept", null, ct);

    public static Task DeclineFriendRequestAsync(this HubClient hub, long requestId, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Post, $"api/friends/requests/{requestId}/decline", null, ct);

    public static Task CancelFriendRequestAsync(this HubClient hub, long requestId, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Delete, $"api/friends/requests/{requestId}", null, ct);

    /// <summary>Throws HubException <c>not_friends</c> (404) unless you are friends.</summary>
    public static Task<FriendProfile> GetFriendAsync(this HubClient hub, string username, CancellationToken ct = default) =>
        hub.SendAuthedAsync<FriendProfile>(HttpMethod.Get, $"api/friends/{Uri.EscapeDataString(username)}", null, ct);

    public static Task UnfriendAsync(this HubClient hub, string username, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Delete, $"api/friends/{Uri.EscapeDataString(username)}", null, ct);

    /// <summary>Every game the player has played or unlocked in, most recently played first.</summary>
    public static Task<HubGameList> GetGamesAsync(this HubClient hub, CancellationToken ct = default) =>
        hub.SendAuthedAsync<HubGameList>(HttpMethod.Get, "api/me/games", null, ct);
}
