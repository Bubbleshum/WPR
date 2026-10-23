// Game details contributed by installs: /api/titles/{titleId}/details. Anonymous on both sides;
// the hub fills only fields it does not have and never replaces one (an admin's or an earlier
// install's).

namespace WPR.Online.Hub.Client;

/// <summary>Which fields the hub would still take for a title. All true for a title it has never seen.</summary>
public sealed record TitleDetailsWants(bool Icon, bool Name, bool Publisher, bool Developer, bool Platform);

public sealed record TitleDetailsStatus(string TitleId, bool Known, string? Name, string? IconUrl, TitleDetailsWants Wants);

/// <summary>What WPR offers. <see cref="Icon"/> is base64 of the package's own PNG or JPEG.</summary>
public sealed record TitleDetailsContribution(
    string? Name = null,
    string? Publisher = null,
    string? Developer = null,
    string? Platform = null,
    string? Icon = null);

/// <summary><see cref="Accepted"/> names the fields the hub kept (<c>icon</c>, <c>name</c>, ...).</summary>
public sealed record TitleDetailsResult(string TitleId, IReadOnlyList<string> Accepted, string? IconUrl, TitleDetailsWants Wants);

public static class TitleDetailsCalls
{
    public static Task<TitleDetailsStatus> GetTitleDetailsAsync(this HubClient hub, string titleId, CancellationToken ct = default) =>
        hub.SendPublicAsync<TitleDetailsStatus>(HttpMethod.Get, Path(titleId), null, ct);

    public static Task<TitleDetailsResult> ContributeTitleDetailsAsync(this HubClient hub, string titleId,
        TitleDetailsContribution contribution, CancellationToken ct = default) =>
        hub.SendPublicAsync<TitleDetailsResult>(HttpMethod.Post, Path(titleId), contribution, ct);

    private static string Path(string titleId) => $"api/titles/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}/details";
}
