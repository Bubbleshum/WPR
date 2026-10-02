using WPR.Engine.Online;
using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// <see cref="IPresence"/> over the hub's <c>PUT /api/me/presence</c>: "online", plus what is being
/// played, about once a minute while signed in. The hub records each run of heartbeats as an
/// online session, so this is also how "when did they come online, and for how long" is logged.
/// </summary>
/// <remarks>
/// The interval comes from the hub (<c>next_heartbeat_seconds</c>). A missed beat is simply
/// retried at the next one: presence is best effort, and the hub treats a player as offline after a
/// couple of minutes of silence, which is also what happens if WPR is killed without saying so.
/// </remarks>
public sealed class HubPresence : IPresence
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SignedOutPoll = TimeSpan.FromSeconds(30);

    private readonly HubConnection _connection;
    private readonly object _gate = new();
    private CancellationTokenSource? _wake;
    private Task? _loop;
    private bool _suspended;
    private bool _stopped;
    private string? _titleId;
    private string? _titleName;
    // Bumped by every Wake. A wake that lands while a beat is on the wire has no delay to cut
    // short yet, so the loop compares this before it sleeps; without it "playing X" waited a
    // whole heartbeat (a minute) whenever the game started during the beat its own Start() sent.
    private int _changes;

    /// <summary>The hub's answer to the last heartbeat: friends online, unread messages, pending requests. Null until one lands, and again when signed out.</summary>
    public PresenceBeat? LastBeat { get; private set; }

    internal HubPresence(HubConnection connection) => _connection = connection;

    public void Start()
    {
        lock (_gate)
        {
            _stopped = false;
            _loop ??= Task.Run(LoopAsync);
        }
        Wake();
    }

    public void SetPlaying(string? titleId, string? titleName)
    {
        lock (_gate)
        {
            _titleId = titleId;
            _titleName = titleName;
        }
        Wake();
    }

    /// <summary>
    /// Beat now rather than at the next interval - after the appear-offline switch changes, say.
    /// Unlike <see cref="Resume"/> it leaves a suspension alone.
    /// </summary>
    public void Refresh() => Wake();

    public void Suspend()
    {
        lock (_gate) _suspended = true;
    }

    public void Resume()
    {
        lock (_gate) _suspended = false;
        Wake();
    }

    public async Task GoOfflineAsync()
    {
        lock (_gate) _stopped = true;
        Wake();
        HubClient? hub = _connection.Client();
        if (hub is null || !hub.IsSignedIn) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await hub.SendPresenceAsync(PresenceStatuses.Offline, null, null, timeout.Token).ConfigureAwait(false);
            _connection.Log("presence: offline");
        }
        catch (Exception)
        {
            // The hub times the session out on its own.
        }
    }

    private async Task LoopAsync()
    {
        string? lastTitle = null;
        bool wasOnline = false;
        bool lastHidden = false;
        while (true)
        {
            TimeSpan wait = SignedOutPoll;
            bool stopped, suspended;
            string? titleId, titleName;
            int changes;
            lock (_gate)
            {
                changes = _changes;
                stopped = _stopped;
                suspended = _suspended;
                titleId = _titleId;
                titleName = _titleName;
            }

            HubClient? hub = _connection.Client();
            if (!stopped && !suspended && hub is { IsSignedIn: true })
            {
                try
                {
                    bool hidden = _connection.AppearOffline;
                    string status = hidden ? PresenceStatuses.AppearOffline : PresenceStatuses.Online;
                    PresenceBeat beat = await hub.SendPresenceAsync(status, titleId, titleName).ConfigureAwait(false);
                    wait = TimeSpan.FromSeconds(Math.Clamp(beat.NextHeartbeatSeconds, 15, 300));
                    LastBeat = beat;
                    if (!wasOnline || lastTitle != titleId || lastHidden != hidden)
                        _connection.Log((hidden ? "presence: appearing offline" : "presence: online")
                            + (titleId is null ? "" : $", playing {titleName ?? titleId}"));
                    wasOnline = true;
                    lastTitle = titleId;
                    lastHidden = hidden;
                }
                catch (Exception e)
                {
                    _connection.Observe(e);
                    wait = DefaultInterval;
                    wasOnline = false;
                }
            }
            else
            {
                wasOnline = false;
                if (hub is not { IsSignedIn: true }) LastBeat = null;
            }

            CancellationTokenSource wake = new();
            lock (_gate)
            {
                _wake = wake;
                if (_changes != changes) continue; // woken during the beat: send the new state now
            }
            try { await Task.Delay(wait, wake.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* woken early: a title change, a resume, a sign-in */ }
        }
    }

    private void Wake()
    {
        CancellationTokenSource? wake;
        lock (_gate)
        {
            _changes++;
            wake = _wake;
        }
        try { wake?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
