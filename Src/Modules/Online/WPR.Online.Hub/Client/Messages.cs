namespace WPR.Online.Hub.Client;

// Direct messages, for the messages screens of both heads. Shapes follow the hub's
// MessageController and Message::toClientArray. The hub only lets friends message each other;
// a conversation with someone who is no longer a friend is read-only history (IsFriend false).

public sealed record ChatMessage(long Id, string? From, bool Mine, string Body, DateTimeOffset SentAt, DateTimeOffset? ReadAt);

public sealed record Conversation(string With, string? GamerpicUrl, bool IsFriend, ChatMessage LastMessage, int Unread);

public sealed record ConversationList(IReadOnlyList<Conversation> Conversations);

public sealed record MessageThread(string With, bool IsFriend, IReadOnlyList<ChatMessage> Messages, bool HasMore);

public static class MessageReportReasons
{
    public const string Harassment = "harassment";
    public const string Spam = "spam";
    public const string Inappropriate = "inappropriate";
    public const string Other = "other";
}

public static class HubMessageExtensions
{
    /// <summary>One row per conversation, newest first (the hub returns up to 100).</summary>
    public static Task<ConversationList> GetConversationsAsync(this HubClient hub, CancellationToken ct = default) =>
        hub.SendAuthedAsync<ConversationList>(HttpMethod.Get, "api/conversations", null, ct);

    /// <summary>
    /// The latest messages with <paramref name="username"/>, oldest first. <paramref name="after"/>
    /// returns only newer ones (polling an open conversation); <paramref name="before"/> pages back.
    /// </summary>
    public static Task<MessageThread> GetThreadAsync(this HubClient hub, string username, long? after = null, long? before = null,
        CancellationToken ct = default)
    {
        string path = $"api/conversations/{Uri.EscapeDataString(username)}";
        if (after is { } a) path += $"?after={a}";
        else if (before is { } b) path += $"?before={b}";
        return hub.SendAuthedAsync<MessageThread>(HttpMethod.Get, path, null, ct);
    }

    /// <summary>Throws HubException <c>not_friends</c> (403) unless you are friends.</summary>
    public static Task<ChatMessage> SendMessageAsync(this HubClient hub, string username, string body, CancellationToken ct = default) =>
        hub.SendAuthedAsync<ChatMessage>(HttpMethod.Post, $"api/conversations/{Uri.EscapeDataString(username)}", new { body }, ct);

    public static Task MarkReadAsync(this HubClient hub, string username, long? upToId = null, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Post, $"api/conversations/{Uri.EscapeDataString(username)}/read",
            new { up_to_id = upToId }, ct);

    /// <summary>Delete for me; the other player keeps their copy.</summary>
    public static Task DeleteMessageAsync(this HubClient hub, long id, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Delete, $"api/messages/{id}", null, ct);

    /// <summary>Report a message sent to you (the only way an admin ever sees a message). Optionally block the sender.</summary>
    public static Task ReportMessageAsync(this HubClient hub, long id, string reason, bool block, CancellationToken ct = default) =>
        hub.SendAuthedNoContentAsync(HttpMethod.Post, $"api/messages/{id}/report", new { reason, block }, ct);
}
