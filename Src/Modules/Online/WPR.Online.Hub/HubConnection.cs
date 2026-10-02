using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// One <see cref="HubClient"/> per configured base URI, re-armed with the current token before
/// every use. Shared by the leaderboard and crash services so the app holds one HttpClient.
/// </summary>
internal sealed class HubConnection
{
    private readonly HubSettings _settings;
    private readonly object _gate = new();
    private HubClient? _client;

    public HubConnection(HubSettings settings) => _settings = settings;

    public HubSettings Settings => _settings;

    /// <summary>Null when no hub is configured.</summary>
    public HubClient? Client()
    {
        Uri? baseUri = SafeGet(_settings.BaseUri);
        if (baseUri is null) return null;

        lock (_gate)
        {
            // Compared with the slash HubClient appends, so an unchanged setting reuses the client.
            string wanted = baseUri.AbsoluteUri.EndsWith('/') ? baseUri.AbsoluteUri : baseUri.AbsoluteUri + "/";
            if (_client is null || _client.BaseUri.AbsoluteUri != wanted)
            {
                _client?.Dispose();
                _client = new HubClient(baseUri, userAgent: $"WPR/{_settings.WprVersion} ({_settings.Platform})");
            }

            _client.AccessToken = SafeGet(_settings.AccessToken) is { Length: > 0 } token ? token : null;
            return _client;
        }
    }

    public bool IsSignedIn => SafeGet(_settings.BaseUri) is not null && SafeGet(_settings.AccessToken) is { Length: > 0 };

    public bool ServerLogging => SafeGet(_settings.ServerLogging);

    public bool AppearOffline => _settings.AppearOffline is { } read && SafeGet(read);

    /// <summary>
    /// HubClient clears its token on a 401 and says so with this error code; the stored token is
    /// the head's, so tell it.
    /// </summary>
    public void Observe(Exception e)
    {
        if (e is HubException { ErrorCode: "unauthenticated" })
        {
            Log("the hub rejected the device token; treating this device as signed out");
            try { _settings.SignedOut?.Invoke(); } catch { /* the head's problem, not ours */ }
        }
    }

    public void Log(string message)
    {
        try { _settings.Log?.Invoke("[wpr-online] " + message); } catch { /* diagnostics only */ }
    }

    private static T? SafeGet<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
