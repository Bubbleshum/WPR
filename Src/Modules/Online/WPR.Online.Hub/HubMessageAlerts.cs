using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>A conversation with something new in it: the latest message and how many are unread.</summary>
public sealed record MessageAlert(string From, string? GamerpicUrl, ChatMessage Message, int Unread);

/// <summary>
/// Notices new incoming messages (game invites included) and friend requests from the presence
/// heartbeat, so the platform can pop a notification. The heartbeat carries the newest message id
/// the player can see and the number of pending friend requests; when either moves, the
/// conversation list or the request list says what is new, and <see cref="Arrived"/> /
/// <see cref="FriendRequestsArrived"/> are raised.
/// </summary>
/// <remarks>
/// <para>What was last announced is kept in <c>message-alerts.txt</c> in the module's folder,
/// shared by Android's launcher and <c>:game</c> processes, so each thing is announced once however
/// many processes beat. The first time (no file) only records where things stand: old messages
/// are not news.</para>
///
/// <para>This is the fallback: it notices things at the next heartbeat (a minute by default) and
/// only while WPR is running. An Android device registered for push gets them from the hub
/// straight away instead, and the platform ignores these events there.</para>
/// </remarks>
public sealed class HubMessageAlerts
{
    private readonly HubConnection _connection;
    private readonly HubSocialService _social;
    private readonly string _file;
    private int _busy;

    internal HubMessageAlerts(HubConnection connection, HubSocialService social, string dataDirectory)
    {
        _connection = connection;
        _social = social;
        _file = Path.Combine(dataDirectory, "message-alerts.txt");
    }

    /// <summary>Raised on a worker thread with the conversations that have something new.</summary>
    public event Action<IReadOnlyList<MessageAlert>>? Arrived;

    /// <summary>Raised on a worker thread with friend requests not announced before.</summary>
    public event Action<IReadOnlyList<IncomingFriendRequest>>? FriendRequestsArrived;

    internal void OnBeat(PresenceBeat beat)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(beat).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _connection.Observe(e);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private async Task CheckAsync(PresenceBeat beat)
    {
        State? announced = Read();
        var next = new State(announced?.Message ?? beat.LatestMessageId ?? 0, Math.Max(announced?.Request ?? 0, 0));

        if (beat.LatestMessageId is { } latest && announced != null && latest > announced.Message && beat.UnreadMessages > 0)
        {
            ConversationList? list = await _social.GetConversationsAsync().ConfigureAwait(false);
            if (list != null)
            {
                var alerts = list.Conversations
                    .Where(c => c.Unread > 0 && !c.LastMessage.Mine && c.LastMessage.Id > announced.Message)
                    .Select(c => new MessageAlert(c.With, c.GamerpicUrl, c.LastMessage, c.Unread))
                    .ToList();
                if (alerts.Count > 0)
                {
                    _connection.Log($"messages: {alerts.Count} conversation(s) with something new");
                    Arrived?.Invoke(alerts);
                }
            }
        }
        if (beat.LatestMessageId is { } newest && newest > next.Message) next = next with { Message = newest };

        if (beat.PendingFriendRequests > 0)
        {
            FriendRequests? requests = await _social.GetFriendRequestsAsync().ConfigureAwait(false);
            if (requests != null && requests.Incoming.Count > 0)
            {
                long newestRequest = requests.Incoming.Max(r => r.Id);
                if (announced is { Request: >= 0 })
                {
                    var fresh = requests.Incoming.Where(r => r.Id > announced.Request).ToList();
                    if (fresh.Count > 0)
                    {
                        _connection.Log($"friends: {fresh.Count} new friend request(s)");
                        FriendRequestsArrived?.Invoke(fresh);
                    }
                }
                if (newestRequest > next.Request) next = next with { Request = newestRequest };
            }
        }

        if (announced == null || next != announced) Write(next);
    }

    /// <summary>The newest message id and friend request id already announced (-1: not recorded yet).</summary>
    private sealed record State(long Message, long Request);

    private State? Read()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            long message = 0, request = -1; // -1: never recorded, so the next beat only records
            foreach (string line in File.ReadAllLines(_file))
            {
                string[] parts = line.Split('=', 2);
                if (parts.Length == 1 && long.TryParse(parts[0].Trim(), out long legacy)) message = legacy; // first version: just the id
                else if (parts[0] == "messages") long.TryParse(parts[1], out message);
                else if (parts[0] == "requests") long.TryParse(parts[1], out request);
            }
            return new State(message, request);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Write(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, $"messages={state.Message}\nrequests={state.Request}\n");
        }
        catch (IOException)
        {
            // Worst case the next beat announces the same thing again.
        }
    }
}
