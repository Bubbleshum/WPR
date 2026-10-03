using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WPR.Online.Hub.Client;

public sealed class HubException(string message, HttpStatusCode? status = null, string? errorCode = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>
/// Thin HTTP client for WPR Hub. Holds the access token in memory only;
/// persist it with <see cref="ITokenStore"/> (DPAPI on Windows, Keystore on Android).
/// </summary>
public sealed class HubClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string? AccessToken { get; set; }
    public bool IsSignedIn => AccessToken is not null;

    /// <summary>The hub this client talks to. There is deliberately no default: an unconfigured
    /// WPR sends nothing anywhere.</summary>
    public Uri BaseUri => _http.BaseAddress!;

    public HubClient(Uri baseUri, string? userAgent = null, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        // Relative request paths ("api/...") resolve against the LAST segment of a base without a
        // trailing slash, so "https://host/hub" would silently post to "https://host/api/...".
        if (!baseUri.AbsoluteUri.EndsWith('/')) baseUri = new Uri(baseUri.AbsoluteUri + "/");

        _ownsHttp = http is null;
        // Two minutes, not the usual thirty seconds: one game-package chunk is 4 MB, which a phone on
        // a poor uplink cannot always push in thirty, and a timed-out chunk is re-sent from scratch.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.BaseAddress ??= baseUri;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (userAgent is not null)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    // ---------------------------------------------------------------- sign-in

    /// <summary>Step 1: get a code to show the user. Open VerificationUriComplete in their browser.</summary>
    public Task<DeviceCodeResponse> StartDeviceLoginAsync(string clientName, CancellationToken ct = default) =>
        SendAsync<DeviceCodeResponse>(HttpMethod.Post, "api/device/code", new { client_name = clientName }, auth: false, ct);

    /// <summary>
    /// Step 2: poll until the user approves on the website. Returns null if
    /// they declined or the code expired (show "try again"). Sets AccessToken on success.
    /// </summary>
    public async Task<DeviceTokenResponse?> WaitForDeviceLoginAsync(DeviceCodeResponse code, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(code.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            // A failed request is not a failed sign-in: poll again next tick. The player spends
            // this wait in the browser, and Android drops a backgrounded app's sockets, so the
            // first poll after they come back reuses a dead pooled connection and throws
            // "Software caused connection abort". The retry opens a fresh one. Safe to repeat:
            // the hub only consumes the device code when it hands out the token.
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync("api/device/token", new { device_code = code.DeviceCode }, HubJson.Options, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested
                                      && e is HttpRequestException or IOException or TaskCanceledException)
            {
                continue;
            }
            using var res = response;

            if (res.IsSuccessStatusCode)
            {
                var token = await ReadAsync<DeviceTokenResponse>(res, ct).ConfigureAwait(false);
                AccessToken = token.AccessToken;
                return token;
            }

            if ((int)res.StatusCode == 429)
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }

            var err = await TryReadErrorAsync(res, ct).ConfigureAwait(false);
            switch (err?.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5); // per RFC 8628
                    continue;
                case "access_denied":
                case "expired_token":
                case "invalid_grant":
                    return null;
                default:
                    throw new HubException(err?.ErrorDescription ?? err?.Message ?? $"Sign-in failed ({(int)res.StatusCode}).", res.StatusCode, err?.Error);
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- account
    // There are no account pages on the website: these back WPR's own settings screen.

    public Task<HubAccount> GetAccountAsync(CancellationToken ct = default) =>
        SendAsync<HubAccount>(HttpMethod.Get, "api/me", null, auth: true, ct);

    public Task<HubAccount> RenameAsync(string newUsername, CancellationToken ct = default) =>
        SendAsync<HubAccount>(HttpMethod.Patch, "api/me", new { username = newUsername }, auth: true, ct);

    /// <summary>Turn server logging on or off for the account (applies to all its devices).</summary>
    public Task<HubAccount> SetServerLoggingAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync<HubAccount>(HttpMethod.Patch, "api/me/settings", new { server_logging = enabled }, auth: true, ct);

    /// <summary>The pictures a player can choose from (admin-curated).</summary>
    public Task<GamerpicCatalog> GetGamerpicsAsync(CancellationToken ct = default) =>
        SendAsync<GamerpicCatalog>(HttpMethod.Get, "api/gamerpics", null, auth: true, ct);

    /// <summary>Pick a gamerpic; null goes back to the default.</summary>
    public Task<GamerpicChoice> SetGamerpicAsync(long? gamerpicId, CancellationToken ct = default) =>
        SendAsync<GamerpicChoice>(HttpMethod.Put, "api/me/gamerpic", new GamerpicIdBody(gamerpicId), auth: true, ct);

    // Explicit type so a null id is still sent (the default options skip nulls).
    private sealed record GamerpicIdBody([property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] long? GamerpicId);

    public Task<DeviceList> GetDevicesAsync(CancellationToken ct = default) =>
        SendAsync<DeviceList>(HttpMethod.Get, "api/me/devices", null, auth: true, ct);

    public async Task RevokeDeviceAsync(long deviceId, CancellationToken ct = default)
    {
        using var _ = await SendRawAsync(HttpMethod.Delete, $"api/me/devices/{deviceId}", null, auth: true, ct).ConfigureAwait(false);
    }

    /// <summary>Everything held about the account (GDPR export), as JSON. Save it where the user chooses.</summary>
    public async Task ExportAccountAsync(Stream destination, CancellationToken ct = default)
    {
        using var res = await SendRawAsync(HttpMethod.Get, "api/me/export", null, auth: true, ct).ConfigureAwait(false);
        await res.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the account and everything linked to it. The user must type their username.</summary>
    public async Task DeleteAccountAsync(string confirmUsername, CancellationToken ct = default)
    {
        using var _ = await SendRawAsync(HttpMethod.Delete, "api/me", new { confirm = confirmUsername }, auth: true, ct).ConfigureAwait(false);
        AccessToken = null;
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (AccessToken is null) return;
        try { using var _ = await SendRawAsync(HttpMethod.Post, "api/me/logout", null, auth: true, ct).ConfigureAwait(false); }
        catch (HubException) { /* token already revoked on the website: fine */ }
        catch (HttpRequestException) { /* offline: forget locally anyway */ }
        AccessToken = null;
    }

    // ----------------------------------------------------------- achievements

    public Task<SyncResponse> SyncAchievementsAsync(IReadOnlyList<Unlock> unlocks, CancellationToken ct = default) =>
        SendAsync<SyncResponse>(HttpMethod.Post, "api/me/achievements/sync", new SyncRequest(unlocks), auth: true, ct);

    /// <summary>All unlocks on the account, or those changed since a previous ServerTime.</summary>
    public Task<RestoreResponse> GetAchievementsAsync(DateTimeOffset? since = null, CancellationToken ct = default) =>
        SendAsync<RestoreResponse>(HttpMethod.Get,
            since is null ? "api/me/achievements" : $"api/me/achievements?since={Uri.EscapeDataString(since.Value.ToString("O"))}",
            null, auth: true, ct);

    /// <summary>Public definitions for a game. Returns null if nobody has written them yet.</summary>
    public async Task<TitleDefinitions?> GetDefinitionsAsync(string titleId, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"api/titles/{Uri.EscapeDataString(TitleIds.Normalize(titleId))}/achievements", ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(res, ct).ConfigureAwait(false);
        return await ReadAsync<TitleDefinitions>(res, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ diagnostics

    /// <summary>
    /// Sends a report. Anonymous unless <paramref name="linkToAccount"/> and signed in.
    /// Always show the user <see cref="Diagnostics.Preview"/> first.
    /// </summary>
    public Task<DiagnosticReceipt> SendDiagnosticsAsync(DiagnosticReport report, bool linkToAccount = false, CancellationToken ct = default) =>
        SendAsync<DiagnosticReceipt>(HttpMethod.Post, "api/diagnostics", report, auth: linkToAccount, ct);

    /// <summary>Status of a sent report (open / fixed in X / known issue). Never includes its contents.</summary>
    public Task<ReportStatus> GetReportStatusAsync(string code, CancellationToken ct = default) =>
        SendAsync<ReportStatus>(HttpMethod.Get, $"api/diagnostics/{Uri.EscapeDataString(code)}", null, auth: false, ct);

    /// <summary>Optional follow-up: upload the full log (gzipped here) using the one-time token from the receipt.</summary>
    public async Task UploadLogAsync(DiagnosticReceipt receipt, Stream plainTextLog, CancellationToken ct = default)
    {
        using var gz = new MemoryStream();
        await using (var zip = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            await plainTextLog.CopyToAsync(zip, ct).ConfigureAwait(false);

        if (gz.Length > receipt.MaxLogBytes)
            throw new HubException($"Log is {gz.Length / 1024} KB compressed; the limit is {receipt.MaxLogBytes / 1024} KB.");

        gz.Position = 0;
        using var req = new HttpRequestMessage(HttpMethod.Put, receipt.LogUploadUrl) { Content = new StreamContent(gz) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        req.Headers.Add("X-Upload-Token", receipt.UploadToken);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(res, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- game files

    /// <summary>Signed-in only: does the server have this package, and would it like it?</summary>
    public Task<GameFileInfo> GetGameFileInfoAsync(string sha256, CancellationToken ct = default) =>
        SendAsync<GameFileInfo>(HttpMethod.Get, $"api/game-files/{sha256.ToLowerInvariant()}", null, auth: true, ct);

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Streams a game package to the server in chunks. Resumable: if the app
    /// closes or the connection drops, calling this again with the same offer
    /// (or while signed in as the same user) continues from where the server
    /// got to. Returns the final state: "complete", "failed" (e.g. the file
    /// didn't match its hash), "in_progress" (someone else is sending it) or
    /// "not_wanted" (storage full, or server logging off). Only used with server logging on.
    /// </summary>
    /// <param name="offer">From a crash receipt; null to use the signed-in account instead.</param>
    public async Task<GameFileUploadState> UploadGameFileAsync(
        string packagePath,
        GameFileOffer? offer = null,
        string? titleId = null,
        string? titleName = null,
        IProgress<(long Sent, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        var size = new FileInfo(packagePath).Length;
        var sha = offer?.Sha256 ?? await ComputeSha256Async(packagePath, ct).ConfigureAwait(false);
        var start = offer?.StartUrl ?? $"api/game-files/{sha}/uploads";
        var uploadToken = offer?.Wanted == true ? offer.Token : null;

        var state = await UploadRequestAsync(HttpMethod.Post, start, uploadToken, ct, json: new
        {
            size,
            file_name = Path.GetFileName(packagePath),
            title_id = titleId,
            title_name = titleName,
        }).ConfigureAwait(false);

        if (state.Status != "receiving" || state.UploadUrl is null)
            return state;

        // Fixed for the whole upload. Later responses repeat it, but an error-shaped 409/507 body
        // may not, and losing it mid-loop would strand the upload.
        string uploadUrl = state.UploadUrl;

        await using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var buffer = new byte[Math.Max(state.ChunkBytes, 64 * 1024)];
        var failures = 0;

        while (state.Status == "receiving" && state.Offset < size)
        {
            progress?.Report((state.Offset, size));

            file.Position = state.Offset;
            var want = (int)Math.Min(state.ChunkBytes, size - state.Offset);
            var read = 0;
            while (read < want)
            {
                var n = await file.ReadAsync(buffer.AsMemory(read, want - read), ct).ConfigureAwait(false);
                if (n == 0) throw new HubException("The game file changed while uploading.");
                read += n;
            }

            try
            {
                state = await UploadRequestAsync(HttpMethod.Patch, uploadUrl, uploadToken, ct,
                    body: new ReadOnlyMemory<byte>(buffer, 0, read), offset: state.Offset).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException { InnerException: TimeoutException } or HubException { Status: >= HttpStatusCode.InternalServerError })
            {
                // Network blip or server hiccup: back off, ask the server where it got to, carry on.
                if (++failures > 5) throw;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, failures)), ct).ConfigureAwait(false);
                state = await UploadRequestAsync(HttpMethod.Get, uploadUrl, uploadToken, ct).ConfigureAwait(false);
            }
        }

        // Last chunk sent: the server hashes the file before accepting it.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        while (state.Status == "verifying" && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            state = await UploadRequestAsync(HttpMethod.Get, uploadUrl, uploadToken, ct).ConfigureAwait(false);
        }

        progress?.Report((state.Offset, size));
        return state;
    }

    /// <summary>Like SendAsync, but 409/507 carry an upload state rather than being errors.</summary>
    private async Task<GameFileUploadState> UploadRequestAsync(HttpMethod method, string url, string? uploadToken, CancellationToken ct,
        object? json = null, ReadOnlyMemory<byte>? body = null, long? offset = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (json is not null)
            req.Content = JsonContent.Create(json, json.GetType(), options: HubJson.Options);
        if (body is { } bytes)
        {
            req.Content = new ReadOnlyMemoryContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        if (offset is { } o)
            req.Headers.Add("Upload-Offset", o.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (uploadToken is not null)
            req.Headers.Add("X-Upload-Token", uploadToken);
        else if (AccessToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (res.IsSuccessStatusCode || (int)res.StatusCode is 409 or 507)
        {
            var state = await res.Content.ReadFromJsonAsync<GameFileUploadState>(HubJson.Options, ct).ConfigureAwait(false);
            if (state is not null) return state;
        }

        await EnsureSuccessAsync(res, ct).ConfigureAwait(false);
        throw new HubException("Unexpected response from WPR Hub.", res.StatusCode);
    }

    // --------------------------------------------------------------- plumbing

    // Used by the extension methods in Social.cs.
    internal Task<T> SendAuthedAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendAsync<T>(method, path, body, auth: true, ct);

    /// <summary>
    /// A public file from the hub by absolute URL (gamerpic images). Sent without the token: those
    /// routes are cookie-less and cache-forever, and a bearer header would only defeat caching.
    /// </summary>
    public async Task<byte[]> GetPublicBytesAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(res, ct).ConfigureAwait(false);
        return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    // For the anonymous endpoints outside Social/Leaderboards' signed-in set.
    internal Task<T> SendPublicAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendAsync<T>(method, path, body, auth: false, ct);

    internal async Task SendAuthedNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var _ = await SendRawAsync(method, path, body, auth: true, ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        using var res = await SendRawAsync(method, path, body, auth, ct).ConfigureAwait(false);
        return await ReadAsync<T>(res, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body, body.GetType(), options: HubJson.Options);
        if (auth && AccessToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(res, ct).ConfigureAwait(false);
        }
        catch
        {
            res.Dispose();
            throw;
        }
        return res;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;

        if (res.StatusCode == HttpStatusCode.Unauthorized && AccessToken is not null)
        {
            AccessToken = null; // revoked from the website; caller should show "signed out"
            throw new HubException("Signed out. The device was removed from your account.", res.StatusCode, "unauthenticated");
        }

        var err = await TryReadErrorAsync(res, ct).ConfigureAwait(false);
        var message = (int)res.StatusCode switch
        {
            413 => "The report is too large.",
            429 => "Too many requests. Try again later.",
            _ => err?.ErrorDescription ?? err?.Message ?? $"WPR Hub returned {(int)res.StatusCode}.",
        };
        throw new HubException(message, res.StatusCode, err?.Error);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        await res.Content.ReadFromJsonAsync<T>(HubJson.Options, ct).ConfigureAwait(false)
        ?? throw new HubException("Empty response from WPR Hub.", res.StatusCode);

    private static async Task<HubError?> TryReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadFromJsonAsync<HubError>(HubJson.Options, ct).ConfigureAwait(false); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public static class TitleIds
{
    /// <summary>Same rule as the server: trim, drop braces, uppercase.</summary>
    public static string Normalize(string id) => id.Trim().Trim('{', '}').ToUpperInvariant();
}

/// <summary>Where the access token lives between runs. Implement per platform.</summary>
public interface ITokenStore
{
    string? Load();
    void Save(string? token);
}
