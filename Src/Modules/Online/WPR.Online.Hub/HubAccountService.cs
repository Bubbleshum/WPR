using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// Signing in to WPR Hub, for the settings pages of both heads.
/// </summary>
/// <remarks>
/// <para><b>The flow is RFC 8628's device code, and the player never types a password into
/// WPR.</b> <see cref="StartAsync"/> asks the hub for a pair of codes: a short <b>link key</b>
/// (<c>KQ7X-M2PD</c>) for the player, and a long secret device code this class keeps. The head
/// shows the link key and opens <see cref="SignIn.LinkUrl"/> (the hub's <c>/link</c> page, the
/// key already filled in). The player signs in there with GitHub or Microsoft, checks that the
/// key on the page matches the one WPR shows, and approves. <see cref="SignIn.CompleteAsync"/>
/// polls the hub the whole time and returns once they do. The key match is the point: it is what
/// stops someone who got the player to open a link from approving a code that is not theirs.</para>
///
/// <para><b>On success three things happen, in this order.</b> The token and username are saved
/// (<see cref="HubSettings.SignedIn"/>). The account's server-logging setting is brought in line
/// with WPR's, because for a signed-in player the hub goes by the account and ignores what the
/// client sends: a new account starts with it off, so skipping this would stop every game-package
/// upload the moment someone signed in. Then everything queued is flushed, which is when scores
/// played while signed out reach the leaderboards.</para>
/// </remarks>
public sealed class HubAccountService
{
    private readonly HubConnection _connection;

    internal HubAccountService(HubConnection connection) => _connection = connection;

    public bool IsSignedIn => _connection.IsSignedIn;

    /// <summary>
    /// Step 1. Null when no hub is configured. Throws HubException / HttpRequestException when
    /// the hub cannot be reached, which the settings page shows as-is.
    /// </summary>
    public async Task<SignIn?> StartAsync(string clientName, CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is null) return null;

        DeviceCodeResponse code = await hub.StartDeviceLoginAsync(clientName, ct).ConfigureAwait(false);
        _connection.Log($"sign-in started, link key {code.UserCode}");
        return new SignIn(this, hub, code);
    }

    /// <summary>Revoke this device's token on the hub and forget it here. Never throws.</summary>
    public async Task SignOutAsync()
    {
        HubClient? hub = _connection.Client();
        try
        {
            if (hub is not null) await hub.SignOutAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Forget it locally regardless; the website's device list can revoke it later.
            _connection.Log("sign-out on the hub failed, forgetting the token locally: " + e.Message);
        }
        try { _connection.Settings.SignedOut?.Invoke(); } catch { /* the head's problem */ }
        _connection.Log("signed out");
    }

    /// <summary>
    /// The account as the hub sees it now, or null when signed out or unreachable. A token the
    /// website revoked signs this device out as a side effect.
    /// </summary>
    public async Task<HubAccount?> RefreshAsync(CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is null || !hub.IsSignedIn) return null;
        try
        {
            HubAccount account = await hub.GetAccountAsync(ct).ConfigureAwait(false);
            // Keep the stored name current: the player may have renamed on another device.
            if (_connection.Settings.AccessToken() is { } token)
                _connection.Settings.SignedIn?.Invoke(token, account.Username);
            // ...and the gamerpic, which may have been changed elsewhere too.
            await ApplyGamerpicAsync(account.Gamerpic?.Url, explicitChoice: false, ct).ConfigureAwait(false);
            return account;
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            return null;
        }
    }

    /// <summary>
    /// Push WPR's server-logging switch to the account. Call it when the switch changes while
    /// signed in; see the remarks for why the account setting matters. Never throws.
    /// </summary>
    public async Task SyncServerLoggingAsync()
    {
        HubClient? hub = _connection.Client();
        if (hub is null || !hub.IsSignedIn) return;
        try
        {
            bool on = _connection.ServerLogging;
            await hub.SetServerLoggingAsync(on).ConfigureAwait(false);
            _connection.Log($"account server logging set {(on ? "on" : "off")}");
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            _connection.Log("could not update the account's server logging: " + e.Message);
        }
    }

    private async Task CompletedAsync(DeviceTokenResponse token)
    {
        _connection.Settings.SignedIn?.Invoke(token.AccessToken, token.User.Username);
        _connection.Log($"signed in as {token.User.Username}");
        await SyncServerLoggingAsync().ConfigureAwait(false);
        await ApplyGamerpicAsync(token.User.GamerpicUrl, explicitChoice: false).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ gamerpics

    private readonly Dictionary<string, byte[]> _images = new();

    /// <summary>The pictures this player may choose from, with locked reward pictures marked. Null when signed out.</summary>
    public async Task<GamerpicCatalog?> GetGamerpicsAsync(CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is null || !hub.IsSignedIn) return null;
        try
        {
            return await hub.GetGamerpicsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            throw;
        }
    }

    /// <summary>
    /// Pick a gamerpic (null = back to the hub's default). Also makes it this device's in-game
    /// gamer picture, since picking it is an explicit choice. Throws on a locked picture
    /// (<see cref="HubException.ErrorCode"/> <c>locked</c>) or a hub error.
    /// </summary>
    public async Task<GamerpicChoice> ChooseGamerpicAsync(long? gamerpicId, CancellationToken ct = default)
    {
        HubClient hub = _connection.Client() ?? throw new InvalidOperationException("No WPR Hub server is set.");
        GamerpicChoice choice;
        try
        {
            choice = await hub.SetGamerpicAsync(gamerpicId, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            throw;
        }
        _connection.Log($"gamerpic set to {(gamerpicId?.ToString() ?? "the default")}");
        await ApplyGamerpicAsync(choice.Url, explicitChoice: true, ct).ConfigureAwait(false);
        return choice;
    }

    /// <summary>
    /// A gamerpic image for display, cached for the life of the process. Null for anything that is
    /// not served by the configured hub: the catalog only ever names the hub's own images, and
    /// following an arbitrary URL would let one leak where this device is.
    /// </summary>
    public async Task<byte[]?> GetImageAsync(string? url, CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is null || string.IsNullOrEmpty(url) || !IsHubUrl(hub, url)) return null;

        lock (_images)
            if (_images.TryGetValue(url, out byte[]? cached)) return cached;

        try
        {
            byte[] bytes = await hub.GetPublicBytesAsync(url, ct).ConfigureAwait(false);
            lock (_images) _images[url] = bytes;
            return bytes;
        }
        catch (Exception e) when (e is HubException or HttpRequestException or TaskCanceledException)
        {
            _connection.Log($"could not load gamerpic image: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download the account's gamerpic into this module's folder and hand the file to the head,
    /// which decides whether to make it the in-game gamer picture. Never throws.
    /// </summary>
    private async Task ApplyGamerpicAsync(string? url, bool explicitChoice, CancellationToken ct = default)
    {
        try
        {
            string path = Path.Combine(_connection.Settings.DataDirectory, "gamerpic.png");
            string marker = path + ".url";

            if (string.IsNullOrEmpty(url))
            {
                _connection.Settings.GamerpicChanged?.Invoke(null, explicitChoice);
                return;
            }

            // Already have this one: the URL carries a content hash, so a match means the same image.
            bool current = File.Exists(path) && File.Exists(marker) && File.ReadAllText(marker) == url;
            if (!current)
            {
                byte[]? bytes = await GetImageAsync(url, ct).ConfigureAwait(false);
                if (bytes is null) return;
                Directory.CreateDirectory(_connection.Settings.DataDirectory);
                File.WriteAllBytes(path, bytes);
                File.WriteAllText(marker, url);
            }

            _connection.Settings.GamerpicChanged?.Invoke(path, explicitChoice);
        }
        catch (Exception e)
        {
            _connection.Log("could not save the gamerpic: " + e.Message);
        }
    }

    private static bool IsHubUrl(HubClient hub, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Host, hub.BaseUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Scheme == hub.BaseUri.Scheme;

    /// <summary>A sign-in in progress: what to show the player, and the wait for them to approve.</summary>
    public sealed class SignIn
    {
        private readonly HubAccountService _owner;
        private readonly HubClient _hub;
        private readonly DeviceCodeResponse _code;

        internal SignIn(HubAccountService owner, HubClient hub, DeviceCodeResponse code)
        {
            _owner = owner;
            _hub = hub;
            _code = code;
        }

        /// <summary>The link key to show, e.g. <c>KQ7X-M2PD</c>. The player checks it matches the one on the hub's page.</summary>
        public string LinkKey => _code.UserCode;

        /// <summary>The hub's link page with the key filled in. Open this in the browser.</summary>
        public string LinkUrl => _code.VerificationUriComplete;

        /// <summary>The same page without the key, for typing it in on another device.</summary>
        public string LinkPage => _code.VerificationUri;

        /// <summary>
        /// The sign-ins to offer as "continue with …" buttons. Signing in and signing up are the
        /// same step: the first sign-in with one of these creates the WPR Hub account. A hub too
        /// old to say gets the three it can have, and ignores the hint anyway.
        /// </summary>
        public IReadOnlyList<SignInProvider> Providers =>
            _code.Providers is { Count: > 0 } offered ? offered : DefaultProviders;

        private static readonly SignInProvider[] DefaultProviders =
        {
            new("github", "GitHub"), new("microsoft", "Microsoft"), new("google", "Google"),
        };

        /// <summary>
        /// <see cref="LinkUrl"/>, but going straight to <paramref name="provider"/>'s sign-in. The
        /// player still comes back to the link page to check the key and approve.
        /// </summary>
        public string LinkUrlFor(string provider) =>
            LinkUrl + (LinkUrl.Contains('?') ? "&" : "?") + "provider=" + Uri.EscapeDataString(provider);

        public TimeSpan ExpiresIn => TimeSpan.FromSeconds(_code.ExpiresIn);

        /// <summary>
        /// Step 2: wait for the player to approve on the website. Returns the username, or null
        /// when they declined or the key expired ("try again"). Cancel it if they give up. Throws
        /// for an unexpected hub error.
        /// </summary>
        public async Task<string?> CompleteAsync(CancellationToken ct = default)
        {
            // The poll sets the client's AccessToken on success, but that client is shared and
            // re-armed from the head's settings before every use - so what matters is saving it.
            DeviceTokenResponse? token = await _hub.WaitForDeviceLoginAsync(_code, ct).ConfigureAwait(false);
            if (token is null)
            {
                _owner._connection.Log("sign-in declined or expired");
                return null;
            }

            await _owner.CompletedAsync(token).ConfigureAwait(false);
            _ = WPR.Engine.Online.OnlineBackend.FlushAsync();
            return token.User.Username;
        }
    }
}
