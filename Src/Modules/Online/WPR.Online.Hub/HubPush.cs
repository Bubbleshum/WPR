using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// Push notifications from the hub (Firebase). The Android head asks for the client settings,
/// starts Firebase with them, and registers the token it gets; the hub then pushes new messages,
/// game invites and friend requests to the device, closed or not.
/// </summary>
public sealed class HubPush
{
    private readonly HubConnection _connection;

    internal HubPush(HubConnection connection) => _connection = connection;

    /// <summary>The Android client settings, or null when the hub has no push set up or isn't reachable.</summary>
    public async Task<AndroidPushConfig?> GetAndroidConfigAsync(CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is not { IsSignedIn: true }) return null;
        try
        {
            PushConfig config = await hub.GetPushConfigAsync(ct).ConfigureAwait(false);
            return config.Enabled ? config.Android : null;
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            return null;
        }
    }

    public async Task<bool> RegisterAsync(string token, string platform, CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is not { IsSignedIn: true }) return false;
        try
        {
            await hub.RegisterPushTokenAsync(token, platform, ct).ConfigureAwait(false);
            _connection.Log("push: device registered");
            return true;
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            return false;
        }
    }

    public async Task RemoveAsync(string token, CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is not { IsSignedIn: true }) return;
        try
        {
            await hub.RemovePushTokenAsync(token, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
        }
    }
}
