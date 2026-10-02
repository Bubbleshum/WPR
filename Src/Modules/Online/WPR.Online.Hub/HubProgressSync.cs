using WPR.Engine.Online;
using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// <see cref="IProgressSync"/> over the hub's <c>/api/me/achievements/sync</c> and
/// <c>/api/me/playtime</c>. Reads what the local database holds as awaiting upload, sends it, and
/// marks it uploaded only once the hub has answered.
/// </summary>
/// <remarks>
/// <para><b>The local database is the queue.</b> An unlock is recorded there first, always
/// (<c>EarnedOnline = 0</c>), whether or not anyone is signed in. This class is the only thing that
/// flips it to uploaded, so a failed or interrupted send leaves it exactly where it was, and the
/// next flush tries again. Both endpoints are idempotent, which is what makes "send, then mark" safe
/// against a crash between the two.</para>
///
/// <para>Each unlock carries the player's total playtime in that game at the moment it unlocked,
/// from the local play sessions, which is what gives the hub its "took 2 minutes" figures.</para>
/// </remarks>
public sealed class HubProgressSync : IProgressSync
{
    private const int UnlockBatch = 500; // the hub's max_sync_batch
    private const int SessionBatch = 200;

    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    internal HubProgressSync(HubConnection connection) => _connection = connection;

    public void Unlocked()
    {
        if (!_connection.IsSignedIn) return;
        _ = Task.Run(FlushAsync);
    }

    public async Task FlushAsync()
    {
        IOnlineLocalStore? store = _connection.Settings.Local;
        HubClient? hub = _connection.Client();
        if (store is null || hub is null || !hub.IsSignedIn) return;
        if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            await SendSessionsAsync(hub, store).ConfigureAwait(false);
            await SendUnlocksAsync(hub, store).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            _connection.Log("progress sync failed, will retry: " + e.Message);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>
    /// Bring the account's achievements down from the hub into the local database: everything
    /// earned on any device, so a brand-new install shows the player's real gamerscore and
    /// achievements list the moment they sign in. Sends what is waiting first, so the local
    /// unlocks and the hub's agree. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>The first pull after a sign-in is the whole account; later ones ask only for what
    /// changed since the hub's own clock last answered. That mark is kept per device token (a
    /// hash of it, never the token), so a new sign-in, which mints a new token, always starts
    /// with a full pull.</para>
    ///
    /// <para>Call it from the launcher (sign-in, start-up), not from inside a game run: it seeds
    /// catalogues, which is launcher work.</para>
    /// </remarks>
    public async Task RestoreAsync()
    {
        IOnlineLocalStore? store = _connection.Settings.Local;
        HubClient? hub = _connection.Client();
        string? token = _connection.Settings.AccessToken();
        if (store is null || hub is null || !hub.IsSignedIn || string.IsNullOrEmpty(token)) return;

        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SendSessionsAsync(hub, store).ConfigureAwait(false);
            await SendUnlocksAsync(hub, store).ConfigureAwait(false);

            string markKey = "hub-restore-since:" + TokenMark(token!);
            DateTimeOffset? since = DateTimeOffset.TryParse(await store.GetMetaAsync(markKey).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset s)
                ? s : null;

            RestoreResponse remote = await hub.GetAchievementsAsync(since).ConfigureAwait(false);
            if (remote.Unlocks.Count > 0)
            {
                List<string> titles = remote.Unlocks.Select(u => u.TitleId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (_connection.Settings.PrepareTitles is { } prepare)
                {
                    try { await prepare(titles).ConfigureAwait(false); }
                    catch (Exception e) { _connection.Log("could not seed catalogues for restored games: " + e.Message); }
                }

                int earned = await store.RestoreUnlocksAsync(remote.Unlocks
                    .Select(u => new RestoredUnlock(u.TitleId, u.Key, u.UnlockedAt, u.Name, u.Points ?? 0))
                    .ToList()).ConfigureAwait(false);

                _connection.Log($"achievements restored from the hub: {remote.Unlocks.Count} on the account across {titles.Count} game(s), {earned} new here"
                                + (since is null ? " (full)" : ""));
                if (earned > 0)
                {
                    try { _connection.Settings.ProgressRestored?.Invoke(earned); } catch { /* the head's problem */ }
                }
            }

            await store.SetMetaAsync(markKey, remote.ServerTime.ToString("O")).ConfigureAwait(false);
            await RestoreTitleArtAsync(hub).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            _connection.Log("achievement restore failed, will retry: " + e.Message);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>
    /// Tile art for every game on the account that this device has none for: a game restored
    /// from the hub was never installed here, so nothing captured its icon. Uses the hub's own
    /// title icons (only those served from the hub's host). Failures leave the placeholder.
    /// </summary>
    private async Task RestoreTitleArtAsync(HubClient hub)
    {
        if (_connection.Settings.HasTitleArt is not { } has || _connection.Settings.SaveTitleArt is not { } save) return;
        try
        {
            HubGameList games = await hub.GetGamesAsync().ConfigureAwait(false);
            int saved = 0;
            foreach (HubGame game in games.Games)
            {
                if (string.IsNullOrEmpty(game.IconUrl) || has(game.TitleId)) continue;
                if (!Uri.TryCreate(game.IconUrl, UriKind.Absolute, out Uri? uri)
                    || !string.Equals(uri.Host, hub.BaseUri.Host, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    byte[] png = await hub.GetPublicBytesAsync(game.IconUrl!).ConfigureAwait(false);
                    save(game.TitleId, png);
                    saved++;
                }
                catch (Exception e) when (e is HubException or HttpRequestException or TaskCanceledException)
                {
                    _connection.Log($"could not fetch the icon for {game.Name}: {e.Message}");
                }
            }
            if (saved > 0)
            {
                _connection.Log($"game icons restored from the hub: {saved}");
                try { _connection.Settings.ProgressRestored?.Invoke(0); } catch { /* the head's problem */ }
            }
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            _connection.Log("could not restore game icons: " + e.Message);
        }
    }

    private static string TokenMark(string token)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash, 0, 8);
    }

    private async Task SendUnlocksAsync(HubClient hub, IOnlineLocalStore store)
    {
        while (true)
        {
            IReadOnlyList<PendingUnlock> pending = await store.GetPendingUnlocksAsync(UnlockBatch).ConfigureAwait(false);
            if (pending.Count == 0) return;

            List<Unlock> unlocks = new();
            foreach (PendingUnlock p in pending)
            {
                long playtime = await store.GetPlaytimeSecondsAsync(p.TitleId, p.UnlockedAt).ConfigureAwait(false);
                unlocks.Add(new Unlock(TitleIds.Normalize(p.TitleId), p.Key, p.UnlockedAt, p.TitleName, playtime > 0 ? playtime : null));
            }

            SyncResponse result = await hub.SyncAchievementsAsync(unlocks).ConfigureAwait(false);
            await store.MarkUnlocksUploadedAsync(pending).ConfigureAwait(false);
            _connection.Log($"achievements uploaded: {pending.Count} (hub: {result.Accepted} new, {result.Updated} updated, {result.Unchanged} unchanged)");

            if (pending.Count < UnlockBatch) return;
        }
    }

    private async Task SendSessionsAsync(HubClient hub, IOnlineLocalStore store)
    {
        IReadOnlyList<PlaySessionRecord> pending = await store.GetPendingPlaySessionsAsync(SessionBatch).ConfigureAwait(false);
        if (pending.Count == 0) return;

        // A running session is sent as it stands (ended = last seen) and sent again later; the hub
        // keeps the longest copy. Sessions of a few seconds are launch failures, not play.
        List<PlaySessionRecord> worth = pending.Where(s => s.Seconds >= 5 || s.EndedAt is null).ToList();
        List<PlaySession> sessions = worth.Select(s => new PlaySession(
            s.SessionId.ToString(), TitleIds.Normalize(s.TitleId), s.TitleName, s.StartedAt,
            s.EndedAt ?? s.LastSeenAt, Math.Min(s.Seconds, 24 * 3600))).ToList();

        if (sessions.Count > 0) await hub.SyncPlaytimeAsync(sessions).ConfigureAwait(false);

        // Mark every ended one done, including the too-short ones that were never sent.
        await store.MarkPlaySessionsUploadedAsync(pending.Where(s => s.EndedAt is not null).Select(s => s.SessionId).ToList())
            .ConfigureAwait(false);
        if (sessions.Count > 0) _connection.Log($"playtime uploaded: {sessions.Count} session(s)");
    }
}
