using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPR.Online.Hub.Client;

// Wire format for WPR Hub. The server speaks snake_case JSON; HubJson handles
// the mapping, so these stay idiomatic C#.

public sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval,
    // Null from a hub older than 2026-09-28; the link page then offers its own buttons.
    IReadOnlyList<SignInProvider>? Providers = null);

/// <summary>A sign-in the hub's link page offers, e.g. <c>github</c> / "GitHub". The first sign-in creates the account.</summary>
public sealed record SignInProvider(string Key, string Label);

public sealed record HubUser(string Username, string? GamerpicUrl);

public sealed record HubAccount(
    string Username,
    string? AvatarUrl,          // from GitHub/Microsoft; only ever shown to the player themselves
    GamerpicRef Gamerpic,
    int AchievementCount,
    int DiagnosticReportCount,
    IReadOnlyList<string> LinkedLogins,
    AccountSettings Settings);

/// <summary>The player's picture (Id null = using the default). Urls null if no default is set.</summary>
public sealed record GamerpicRef(long? Id, string? Url, [property: JsonPropertyName("url_64")] string? Url64);

public sealed record GamerpicInfo(long Id, string Name, string? Category, string Url, [property: JsonPropertyName("url_64")] string Url64,
    bool Locked = false, IReadOnlyList<GamerpicUnlock>? UnlockedBy = null);

/// <summary>A milestone that unlocks a reward gamerpic.</summary>
public sealed record GamerpicUnlock(long MilestoneId, string Name, string Requirement);

public sealed record GamerpicCatalog(long? DefaultId, IReadOnlyList<GamerpicInfo> Gamerpics, IReadOnlyList<string> Categories);

public sealed record GamerpicChoice(long? GamerpicId, string? Url, [property: JsonPropertyName("url_64")] string? Url64);

/// <summary>
/// ServerLogging: the player's one-time opt-in to automatic crash reports,
/// including uploading an unknown game package without asking each time.
/// </summary>
public sealed record AccountSettings(bool ServerLogging, DateTimeOffset? ServerLoggingEnabledAt);

public sealed record HubDevice(long Id, string Name, bool Current, DateTimeOffset? CreatedAt, DateTimeOffset? LastUsedAt);

public sealed record DeviceList(IReadOnlyList<HubDevice> Devices);

public sealed record DeviceTokenResponse(string AccessToken, string TokenType, HubUser User);

public sealed record Unlock(
    string TitleId,
    string Key,
    DateTimeOffset UnlockedAt,
    string? TitleName = null,
    long? PlaytimeSeconds = null);   // total play in that game at the moment of unlocking

public sealed record SyncRequest(IReadOnlyList<Unlock> Unlocks);

public sealed record SyncResponse(int Accepted, int Updated, int Unchanged, int Total, DateTimeOffset ServerTime, IReadOnlyList<EarnedMilestone>? MilestonesEarned = null);

public sealed record RemoteUnlock(string TitleId, string Key, DateTimeOffset UnlockedAt, string? Name, int? Points, long? PlaytimeSeconds);

public sealed record RestoreResponse(DateTimeOffset ServerTime, IReadOnlyList<RemoteUnlock> Unlocks);

public sealed record AchievementDefinition(
    string Key,
    string Name,
    string? Description,
    int Points,
    string? IconUrl,
    bool Secret,
    AchievementStats? Stats);

/// <summary>
/// "How long to achieve" (null until enough players have it). Times are
/// playtime in the game at unlock: median, and the middle half (p25-p75).
/// </summary>
public sealed record AchievementStats(double? UnlockRate, long? MedianSeconds, long? P25Seconds, long? P75Seconds, int Samples);

public sealed record TitleStats(int Players, long? MedianPlaytimeSeconds, int CompletedPlayers, long? MedianCompletionSeconds, DateTimeOffset ComputedAt);

public sealed record TitleDefinitions(
    string TitleId,
    string Name,
    string? Publisher,
    string? IconUrl,
    int TotalPoints,
    IReadOnlyList<AchievementDefinition> Achievements,
    TitleStats? Stats);

/// <summary>How WPR ran the game. Must match DiagnosticReport::RUNTIME_PATHS on the server.</summary>
public static class RuntimePaths
{
    public const string XnaFna = "xna-fna";
    public const string SilverlightAvalonia = "silverlight-avalonia";
    public const string UnitySwap = "unity-swap";
    public const string ModernNativeUnicorn = "modern-native-unicorn";
    public const string Other = "other";
}

public sealed record DiagnosticReport
{
    public string Kind { get; init; } = "crash"; // "crash" | "manual"
    public string? InstallId { get; init; }
    public required string WprVersion { get; init; }
    public required string Platform { get; init; } // "windows" | "android"
    public string? OsVersion { get; init; }
    public string? Arch { get; init; }
    public string? TitleId { get; init; }
    public string? TitleName { get; init; }
    public string? XapHash { get; init; }   // SHA-256 of the XAP/APPX, lowercase hex
    public long? XapSize { get; init; }     // lets the server decline packages over its limits up front
    public string? RuntimePath { get; init; } // see RuntimePaths
    public string? GraphicsBackend { get; init; }
    public string? Gpu { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? LogTail { get; init; }
    public string? UserComment { get; init; }
    public Dictionary<string, string>? Extra { get; init; }

    /// <summary>
    /// True when the player has server logging on. Without it the server never
    /// asks for the game package. Signed-in players: the account setting wins.
    /// </summary>
    public bool ServerLogging { get; init; }
}

public sealed record DiagnosticReceipt(
    string Code,
    string StatusUrl,
    string LogUploadUrl,
    string UploadToken,
    long MaxLogBytes,
    GameFileOffer GameFile);

/// <summary>
/// Present with Wanted = true when the server has no copy of the crashing game's
/// package. Only offered when server logging is on, which is the player's consent: upload without prompting.
/// The token only works for this hash.
/// </summary>
public sealed record GameFileOffer(bool Wanted, string? Sha256, string? StartUrl, string? Token, long? MaxBytes, string? Reason = null);

public sealed record GameFileInfo(string Sha256, bool Known, bool ServerLogging, bool Wanted, long MaxBytes);

/// <summary>Upload state. Status: receiving | verifying | complete | failed | in_progress | not_wanted.</summary>
public sealed record GameFileUploadState(
    string? UploadId,
    string? UploadUrl,
    string Status,
    long Offset,
    long Size,
    int ChunkBytes,
    string? Error,
    string? Message);

public sealed record ReportStatus(
    string Code,
    DateTimeOffset ReceivedAt,
    bool Processed,
    string? Status,           // open | fixed | ignored | null (not a crash)
    string? FixedInVersion,
    int SimilarReports);

public sealed record HubError(string? Error, string? ErrorDescription, string? Message);

internal static class HubJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>A milestone the sync just earned (hub: Playtime.cs, which WPR does not use yet).</summary>
public sealed record EarnedMilestone(long Id, string Name, string? Description, GamerpicInfo? RewardGamerpic);
