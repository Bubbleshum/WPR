namespace WPR.Online.Hub.Client;

// A title's online services (hub: Api\ServiceController::directory). Anonymous; signed in, each
// service that wants sign-in also carries a short-lived player token for it.

/// <summary>One host rule: the game's host (or <c>*.host</c>), a port, and for TCP the port it goes to.</summary>
public sealed record ServiceHostRule(string Host, int? Port, int? ToPort);

/// <summary>Where a service is: <c>Url</c> for HTTP, <c>Host</c> and <c>Port</c> for TCP/UDP.</summary>
public sealed record ServiceTarget(string? Url, string? Host, int? Port);

public sealed record DirectoryService(
    string Slug,
    string Name,
    string Kind,
    string Protocol,
    string Status,
    bool Healthy,
    string Auth,
    string TokenVia,
    IReadOnlyList<ServiceHostRule> Rules,
    ServiceTarget Target,
    string? Token,
    DateTimeOffset? TokenExpiresAt);

public sealed record ServiceDirectory(string TitleId, IReadOnlyList<DirectoryService> Services, string Tokens);

public static class HubGameServicesExtensions
{
    /// <summary>
    /// The services WPR Hub runs for a title. Sends the account token when there is one, so
    /// services that want sign-in come with a player token.
    /// </summary>
    public static Task<ServiceDirectory> GetServiceDirectoryAsync(this HubClient hub, string titleId, CancellationToken ct = default) =>
        hub.SendAuthedAsync<ServiceDirectory>(HttpMethod.Get, $"api/titles/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}/services", null, ct);
}
