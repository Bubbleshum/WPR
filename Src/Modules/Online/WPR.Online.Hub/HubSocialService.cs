using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// Friends and the player's games, for the WPR Hub page of both heads. Every read returns null
/// when signed out; every call throws <see cref="HubException"/> / <see cref="HttpRequestException"/>
/// for the page to show, after telling the head if the token was revoked.
/// </summary>
public sealed class HubSocialService
{
    private readonly HubConnection _connection;

    internal HubSocialService(HubConnection connection) => _connection = connection;

    public bool IsSignedIn => _connection.IsSignedIn;

    public Task<FriendList?> GetFriendsAsync(CancellationToken ct = default) =>
        ReadAsync(hub => hub.GetFriendsAsync(ct));

    public Task<FriendProfile?> GetFriendAsync(string username, CancellationToken ct = default) =>
        ReadAsync(hub => hub.GetFriendAsync(username, ct));

    public Task<FriendRequests?> GetFriendRequestsAsync(CancellationToken ct = default) =>
        ReadAsync(hub => hub.GetFriendRequestsAsync(ct));

    public Task<HubGameList?> GetGamesAsync(CancellationToken ct = default) =>
        ReadAsync(hub => hub.GetGamesAsync(ct));

    public async Task<FriendRequestResult> SendFriendRequestAsync(string username, CancellationToken ct = default)
    {
        FriendRequestResult result = await CallAsync(hub => hub.SendFriendRequestAsync(username.Trim(), ct)).ConfigureAwait(false);
        _connection.Log($"friend request to {username.Trim()}: {result.Status}");
        return result;
    }

    public Task AcceptAsync(long requestId, CancellationToken ct = default) =>
        CallAsync(async hub => { await hub.AcceptFriendRequestAsync(requestId, ct).ConfigureAwait(false); return true; });

    public Task DeclineAsync(long requestId, CancellationToken ct = default) =>
        CallAsync(async hub => { await hub.DeclineFriendRequestAsync(requestId, ct).ConfigureAwait(false); return true; });

    public Task CancelRequestAsync(long requestId, CancellationToken ct = default) =>
        CallAsync(async hub => { await hub.CancelFriendRequestAsync(requestId, ct).ConfigureAwait(false); return true; });

    public Task RemoveFriendAsync(string username, CancellationToken ct = default) =>
        CallAsync(async hub => { await hub.UnfriendAsync(username, ct).ConfigureAwait(false); return true; });

    // ------------------------------------------------------------------ messages

    public Task<ConversationList?> GetConversationsAsync(CancellationToken ct = default) =>
        ReadAsync(hub => hub.GetConversationsAsync(ct));

    public Task<MessageThread?> GetThreadAsync(string username, long? after = null, long? before = null, CancellationToken ct = default) =>
        ReadAsync(hub => hub.GetThreadAsync(username, after, before, ct));

    public Task<ChatMessage> SendMessageAsync(string username, string body, CancellationToken ct = default) =>
        CallAsync(hub => hub.SendMessageAsync(username, body, ct));

    /// <summary>Best effort: a failed read receipt only leaves the unread count up. Never throws.</summary>
    public async Task MarkReadAsync(string username, long? upToId = null)
    {
        try { await CallAsync(async hub => { await hub.MarkReadAsync(username, upToId).ConfigureAwait(false); return true; }).ConfigureAwait(false); }
        catch (Exception e) { _connection.Log($"could not mark {username}'s messages read: {e.Message}"); }
    }

    public Task DeleteMessageAsync(long id, CancellationToken ct = default) =>
        CallAsync(async hub => { await hub.DeleteMessageAsync(id, ct).ConfigureAwait(false); return true; });

    public Task ReportMessageAsync(long id, string reason, bool block, CancellationToken ct = default) =>
        CallAsync(async hub => { await hub.ReportMessageAsync(id, reason, block, ct).ConfigureAwait(false); return true; });

    // ------------------------------------------------------------------ compatibility ratings

    /// <summary>"How did it run?" (see WPR.Shell.GameRatingPrompt). Signed in only.</summary>
    public Task SubmitCompatibilityAsync(CompatibilitySubmission report, CancellationToken ct = default) =>
        CallAsync(async hub => { await hub.SubmitCompatibilityAsync(report, ct).ConfigureAwait(false); return true; });

    /// <summary>
    /// A game's name as WPR Hub knows it, for a game WPR has no name for itself. Anonymous: works
    /// signed out. Null when the hub has no record of the title or cannot be reached; never throws.
    /// </summary>
    public async Task<string?> GetTitleNameAsync(string titleId, CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is null) return null;
        try
        {
            string? name = (await hub.GetDefinitionsAsync(titleId, ct).ConfigureAwait(false))?.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _connection.Log($"title name for {titleId} not available: {e.Message}");
            return null;
        }
    }

    private async Task<T?> ReadAsync<T>(Func<HubClient, Task<T>> read) where T : class
    {
        HubClient? hub = _connection.Client();
        if (hub is null || !hub.IsSignedIn) return null;
        try
        {
            return await read(hub).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            throw;
        }
    }

    private async Task<T> CallAsync<T>(Func<HubClient, Task<T>> call)
    {
        HubClient hub = _connection.Client() ?? throw new InvalidOperationException("No WPR Hub server is set.");
        if (!hub.IsSignedIn) throw new InvalidOperationException("Sign in to WPR Hub first.");
        try
        {
            return await call(hub).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            throw;
        }
    }
}
