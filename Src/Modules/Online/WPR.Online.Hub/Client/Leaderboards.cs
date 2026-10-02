using System.Text.Json;

// Leaderboard wire models and calls. The hub copy of this file also carries a LeaderboardStore;
// WPR uses its own on-disk queue instead (HubLeaderboards), which is safe across the two Android
// processes.

namespace WPR.Online.Hub.Client;

// ------------------------------------------------------------------ models

public sealed record LeaderboardInfo(string Key, string Name, string Sort, string Format, bool Defined, int Players, MyBest? MyBest);

public sealed record MyBest(long Score, string Formatted, int? Rank);

public sealed record LeaderboardList(string TitleId, IReadOnlyList<LeaderboardInfo> Leaderboards);

public sealed record LeaderboardEntryDto(
    int Rank,
    string Username,
    string? GamerpicUrl,
    long Score,
    string Formatted,
    Dictionary<string, JsonElement>? Extra,
    DateTimeOffset AchievedAt,
    bool Me);

public sealed record LeaderboardHeader(string Key, string Name, string Sort, string Format, bool Defined, int Players);

public sealed record LeaderboardPage(LeaderboardHeader Leaderboard, string View, IReadOnlyList<LeaderboardEntryDto> Entries, LeaderboardEntryDto? Me);

public sealed record ScoreSubmission(string Key, long Score, DateTimeOffset AchievedAt, Dictionary<string, object>? Extra = null);

/// <summary>Status: new_best | not_better | rejected (Reason: out_of_range, too_many_undefined_boards).</summary>
public sealed record ScoreResult(string Key, string Status, long? Best, int? Rank, string? Reason);

public sealed record ScoreResults(IReadOnlyList<ScoreResult> Results);

/// <summary>Status: ok | rejected (Reason: too_many_undefined_boards).</summary>
public sealed record BoardRegistration(string Key, string Status, bool Defined = false, string? Reason = null);

public sealed record BoardRegistrations(string TitleId, IReadOnlyList<BoardRegistration> Leaderboards);

public static class LeaderboardViews
{
    public const string Top = "top";
    public const string AroundMe = "around_me";
    public const string Friends = "friends";
}

// ------------------------------------------------------------------ calls

public static class HubLeaderboardExtensions
{
    public static Task<LeaderboardList> GetLeaderboardsAsync(this HubClient hub, string titleId, CancellationToken ct = default) =>
        hub.SendAuthedAsync<LeaderboardList>(HttpMethod.Get, $"api/leaderboards/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}", null, ct);

    public static Task<LeaderboardPage> GetLeaderboardAsync(this HubClient hub, string titleId, string key,
        string view = LeaderboardViews.Top, int limit = 25, int offset = 0, CancellationToken ct = default) =>
        hub.SendAuthedAsync<LeaderboardPage>(HttpMethod.Get,
            $"api/leaderboards/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}/{Uri.EscapeDataString(key)}?view={view}&limit={limit}&offset={offset}",
            null, ct);

    /// <summary>
    /// Anonymous: make sure these boards exist on the hub (created undefined if not). Reveals
    /// nothing about players, which is why it needs no sign-in.
    /// </summary>
    public static Task<BoardRegistrations> RegisterBoardsAsync(this HubClient hub, string titleId, IReadOnlyList<string> keys,
        string? titleName = null, CancellationToken ct = default) =>
        hub.SendPublicAsync<BoardRegistrations>(HttpMethod.Post, $"api/leaderboards/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}/boards",
            new { title_name = titleName, keys }, ct);

    public static Task<ScoreResults> SubmitScoresAsync(this HubClient hub, string titleId, IReadOnlyList<ScoreSubmission> scores,
        string? titleName = null, CancellationToken ct = default) =>
        hub.SendAuthedAsync<ScoreResults>(HttpMethod.Post, $"api/leaderboards/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}/scores",
            new { title_name = titleName, scores }, ct);
}
