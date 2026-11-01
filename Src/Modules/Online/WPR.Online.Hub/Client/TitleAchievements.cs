// Achievement definitions contributed by installs: /api/titles/{titleId}/achievement-details.
// Anonymous on both sides; the hub fills only what it does not have and never replaces a
// definition or icon an admin or an import set.

namespace WPR.Online.Hub.Client;

/// <summary>What the hub would still take for one achievement it already has a row for.</summary>
/// <param name="WantsDetails">Its name is still the key (an unlock nobody defined), or it came from an install with blanks.</param>
/// <param name="WantsIcon">It has no icon.</param>
public sealed record AchievementDetailsWant(string Key, bool WantsDetails, bool WantsIcon);

/// <summary>The hub's achievement rows for a title. A key not listed is one it would create.</summary>
public sealed record AchievementDetailsStatus(string TitleId, bool Known, IReadOnlyList<AchievementDetailsWant> Achievements);

/// <summary>One definition WPR offers. <see cref="Icon"/> is base64 of a PNG or JPEG.</summary>
/// <param name="Name">Null when WPR has no real name (the key made readable); the hub then names a new row after its key.</param>
public sealed record AchievementDetailsItem(
    string Key,
    string? Name = null,
    string? Description = null,
    int? Points = null,
    bool? Secret = null,
    string? Icon = null);

public sealed record AchievementDetailsContribution(IReadOnlyList<AchievementDetailsItem> Achievements);

/// <summary>How many definitions and icons the hub kept.</summary>
public sealed record AchievementDetailsResult(string TitleId, int Created, int Filled, int Icons);

public static class AchievementDetailsCalls
{
    public static Task<AchievementDetailsStatus> GetAchievementDetailsAsync(this HubClient hub, string titleId, CancellationToken ct = default) =>
        hub.SendPublicAsync<AchievementDetailsStatus>(HttpMethod.Get, Path(titleId), null, ct);

    public static Task<AchievementDetailsResult> ContributeAchievementDetailsAsync(this HubClient hub, string titleId,
        AchievementDetailsContribution contribution, CancellationToken ct = default) =>
        hub.SendPublicAsync<AchievementDetailsResult>(HttpMethod.Post, Path(titleId), contribution, ct);

    private static string Path(string titleId) => $"api/titles/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}/achievement-details";
}
