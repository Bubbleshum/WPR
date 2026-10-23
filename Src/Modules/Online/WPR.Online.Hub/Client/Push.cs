namespace WPR.Online.Hub.Client;

/// <summary>The Firebase client settings the Android app starts Firebase with.</summary>
public sealed record AndroidPushConfig(string ApiKey, string AppId, string ProjectId, string? SenderId);

/// <summary><c>GET /api/push/config</c>. <see cref="Enabled"/> false: the hub sends no pushes.</summary>
public sealed record PushConfig(bool Enabled, AndroidPushConfig? Android);

public static class HubPushExtensions
{
    public static Task<PushConfig> GetPushConfigAsync(this HubClient hub, CancellationToken ct = default) =>
        hub.SendAuthedAsync<PushConfig>(HttpMethod.Get, "api/push/config", null, ct);

    /// <summary>This device's push token, for the signed-in player.</summary>
    public static Task RegisterPushTokenAsync(this HubClient hub, string token, string platform, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Put, "api/me/push-token", new { token, platform }, ct);

    public static Task RemovePushTokenAsync(this HubClient hub, string token, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Delete, "api/me/push-token", new { token }, ct);
}
