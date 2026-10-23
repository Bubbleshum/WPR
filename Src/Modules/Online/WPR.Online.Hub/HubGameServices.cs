using System.Diagnostics;
using WPR.Engine.Online;
using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// <see cref="IGameServiceDirectory"/> over the hub's per-title service directory: every HTTP
/// service's host rules become routes to its <c>target.url</c>, with the player's token when
/// signed in.
/// </summary>
/// <remarks>
/// TCP and UDP services are listed by the directory too, but WPR doesn't redirect sockets: a
/// game finds its socket server through its lobby, and a hub-served lobby names the hub's relay
/// itself. They are logged, so a title whose socket server isn't reached that way stands out.
/// </remarks>
public sealed class HubGameServices : IGameServiceDirectory
{
    private readonly HubConnection _connection;

    internal HubGameServices(HubConnection connection) => _connection = connection;

    public async Task<IReadOnlyList<HttpServiceRoute>> GetRoutesAsync(string titleId, CancellationToken ct)
    {
        HubClient? hub = _connection.Client();
        if (hub is null)
        {
            return Array.Empty<HttpServiceRoute>();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        ServiceDirectory directory = await hub.GetServiceDirectoryAsync(titleId, timeout.Token).ConfigureAwait(false);
        return ToRoutes(directory, line => Trace.WriteLine(line));
    }

    /// <summary>The routes in a directory. Public for tests.</summary>
    public static IReadOnlyList<HttpServiceRoute> ToRoutes(ServiceDirectory directory, Action<string>? log = null)
    {
        var routes = new List<HttpServiceRoute>();
        foreach (DirectoryService s in directory.Services ?? Array.Empty<DirectoryService>())
        {
            if (!string.Equals(s.Protocol, "http", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"[wpr-net] {s.Slug}: {s.Protocol} service at {s.Target?.Host}:{s.Target?.Port}; not redirected by WPR (the game's lobby names it)");
                continue;
            }
            if (s.Target?.Url is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out Uri? target))
            {
                log?.Invoke($"[wpr-net] {s.Slug}: no usable target, skipped");
                continue;
            }
            foreach (ServiceHostRule rule in s.Rules ?? Array.Empty<ServiceHostRule>())
            {
                if (!string.IsNullOrWhiteSpace(rule.Host))
                {
                    routes.Add(new HttpServiceRoute(rule.Host.Trim(), rule.Port, target, s.Token, s.TokenExpiresAt, s.Slug));
                }
            }
        }
        return routes;
    }
}
