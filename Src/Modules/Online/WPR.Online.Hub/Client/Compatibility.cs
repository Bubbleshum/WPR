namespace WPR.Online.Hub.Client;

// "How did it run?" ratings (hub: Api\CompatibilityController). Signed-in only; one report per player
// per game per platform and major OS version, so rating again (say after a WPR update) replaces it.

/// <summary>The hub's six ratings, best first, with the wording its public page uses.</summary>
public static class CompatibilityRatings
{
    public const string Perfect = "perfect";
    public const string Playable = "playable";
    public const string InGame = "ingame";
    public const string Menus = "menus";
    public const string Boots = "boots";
    public const string Nothing = "nothing";

    public static readonly IReadOnlyList<(string Key, string Label, string Meaning)> All = new[]
    {
        (Perfect, "Perfect", "No problems at all."),
        (Playable, "Playable", "Minor glitches, but it can be played to the end."),
        (InGame, "In-game", "Gets into the game, but with serious problems."),
        (Menus, "Menus", "Reaches the menus, but not into the game."),
        (Boots, "Boots", "Starts, then crashes or hangs."),
        (Nothing, "Nothing", "Doesn't launch."),
    };
}

public sealed record CompatibilitySubmission(
    string TitleId,
    string Rating,
    string Platform,
    string? OsVersion,
    string WprVersion,
    string? Arch = null,
    string? Gpu = null,
    string? GraphicsBackend = null,
    string? RuntimePath = null,
    string? Notes = null);

public static class HubCompatibilityExtensions
{
    public static Task SubmitCompatibilityAsync(this HubClient hub, CompatibilitySubmission report, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Post, "api/compatibility",
            report with { TitleId = TitleIds.Normalize(report.TitleId) }, ct);
}
