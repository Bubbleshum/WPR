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
