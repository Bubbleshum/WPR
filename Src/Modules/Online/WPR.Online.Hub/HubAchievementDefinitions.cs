using System.Net;
using WPR.Engine.Online;
using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// <see cref="IAchievementDefinitionsSync"/> over the hub's anonymous
/// <c>/api/titles/{id}/achievement-details</c>: a game's achievement definitions and icons, from
/// the catalogue WPR has for it (shipped, or generated from the package), for the hub to keep
/// whatever it lacks.
/// </summary>
/// <remarks>
/// <para><b>The flow</b> is <see cref="HubTitleDetails"/>'s. <see cref="Offer"/> writes the
/// catalogue to <c>achievement-details/</c>, one file per title. <see cref="FlushAsync"/> asks the
/// hub which rows it has and what each still wants, then posts only those definitions and icons
/// (icons read from disk at that moment), in requests of at most <see cref="MaxBytesPerRequest"/>.
/// After that, or after a refusal, the title goes into <c>achievement-details-sent.txt</c> per hub
/// host and is never offered to that hub again.</para>
///
/// <para><b>What is sent for a row the hub has.</b> Details only when the hub says it wants them
/// (it named the row after its key) and WPR has a real name: a name that is only the key made
/// readable never replaces anything. An icon only when the hub has none.</para>
///
/// <para><b>Backfill, consent, rate</b>: as for title details. Installed games come from
/// <see cref="HubSettings.InstalledAchievementCatalogues"/> at the first flush of a process;
/// nothing is queued or sent with server logging off; at most <see cref="MaxTitlesPerFlush"/>
/// titles a flush, stopping at the first failure that is not a refusal.</para>
/// </remarks>
public sealed class HubAchievementDefinitions : IAchievementDefinitionsSync
{
    internal const int MaxTitlesPerFlush = 10;

    /// <summary>Base64 icon bytes per request; the hub's body limit is above this.</summary>
    internal const int MaxBytesPerRequest = 900 * 1024;

    internal const int MaxItemsPerRequest = 100;

    private readonly HubConnection _connection;
    private readonly DiskQueue<TitleAchievements> _queue;
    private readonly string _sentPath;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private int _backfilled;

    public HubAchievementDefinitions(HubSettings settings) : this(new HubConnection(settings)) { }

    internal HubAchievementDefinitions(HubConnection connection)
    {
        _connection = connection;
        string root = connection.Settings.DataDirectory;
        _queue = new DiskQueue<TitleAchievements>(Path.Combine(root, "achievement-details"), capacity: 500);
        _sentPath = Path.Combine(root, "achievement-details-sent.txt");
    }

    public void Offer(TitleAchievements catalogue)
    {
        try
        {
            if (!_connection.ServerLogging || catalogue is null || string.IsNullOrWhiteSpace(catalogue.TitleId)) return;
            if (catalogue.Achievements is not { Count: > 0 }) return;
            if (_connection.Client() is not { } hub) return;

            string id = TitleIds.Normalize(catalogue.TitleId);
            if (Sent().Contains(SentKey(hub, id, catalogue.Source))) return;
            if (_queue.ReadAll(QueuePrefix(id)).Count > 0) return;

            _queue.Enqueue(catalogue with { TitleId = id }, QueuePrefix(id));
            _connection.Log($"achievement definitions queued: {id} ({catalogue.Achievements.Count}, {catalogue.Source})");
        }
        catch (Exception e)
        {
            _connection.Log("could not queue achievement definitions: " + e.Message);
        }
    }

    public async Task FlushAsync()
    {
        if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!_connection.ServerLogging || _connection.Client() is not { } hub) return;

            if (Interlocked.Exchange(ref _backfilled, 1) == 0) Backfill(hub);

            HashSet<string> sent = Sent();
            int handled = 0;
            foreach ((string path, TitleAchievements catalogue) in _queue.ReadAll())
            {
                if (handled++ >= MaxTitlesPerFlush) break;
                string id = TitleIds.Normalize(catalogue.TitleId);
                if (sent.Contains(SentKey(hub, id, catalogue.Source))) { DiskQueue<TitleAchievements>.Delete(path); continue; }

                try
                {
                    await SendAsync(hub, id, catalogue).ConfigureAwait(false);
                    MarkSent(hub, id, catalogue.Source);
                    DiskQueue<TitleAchievements>.Delete(path);
                }
                catch (HubException e) when (e.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound)
                {
                    _connection.Log($"achievement definitions for {id} refused: {e.Message}");
                    MarkSent(hub, id, catalogue.Source);
                    DiskQueue<TitleAchievements>.Delete(path);
                }
                catch (Exception e)
                {
                    _connection.Log($"achievement definitions not sent ({e.Message}); will retry");
                    break;
                }
            }
        }
        catch (Exception e)
        {
            _connection.Log("achievement definitions flush failed: " + e.Message);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task SendAsync(HubClient hub, string id, TitleAchievements catalogue)
    {
        AchievementDetailsStatus status = await hub.GetAchievementDetailsAsync(id).ConfigureAwait(false);
        List<AchievementDetailsItem> items = Contribution(catalogue, status);
        if (items.Count == 0)
        {
            _connection.Log($"achievement definitions for {id}: the hub has everything we have");
            return;
        }

        int created = 0, filled = 0, icons = 0;
        foreach (List<AchievementDetailsItem> chunk in Chunks(items))
        {
            AchievementDetailsResult result = await hub.ContributeAchievementDetailsAsync(id, new AchievementDetailsContribution(chunk)).ConfigureAwait(false);
            created += result.Created;
            filled += result.Filled;
            icons += result.Icons;
        }
        _connection.Log($"achievement definitions for {id}: hub created {created}, filled {filled}, took {icons} icon(s)");
    }

    /// <summary>What to send: only what the hub wants and we have (see the class remarks).</summary>
    internal static List<AchievementDetailsItem> Contribution(TitleAchievements catalogue, AchievementDetailsStatus status)
    {
        Dictionary<string, AchievementDetailsWant> existing = new(StringComparer.Ordinal);
        foreach (AchievementDetailsWant want in status.Achievements ?? (IReadOnlyList<AchievementDetailsWant>)Array.Empty<AchievementDetailsWant>())
        {
            existing.TryAdd(want.Key, want);
        }

        List<AchievementDetailsItem> items = new();
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (OfferedAchievement a in catalogue.Achievements)
        {
            if (string.IsNullOrEmpty(a.Key) || a.Key.Length > 128 || !keys.Add(a.Key)) continue;
            existing.TryGetValue(a.Key, out AchievementDetailsWant? want);

            bool details = want is null || (want.WantsDetails && !a.NameFromKey);
            string? icon = want is null || want.WantsIcon ? HubTitleDetails.ReadIcon(a.IconFile) : null;
            if (!details && icon is null) continue;

            string? description = string.IsNullOrWhiteSpace(a.Description) ? null : a.Description.Trim();
            items.Add(new AchievementDetailsItem(
                a.Key,
                Name: details && !a.NameFromKey ? a.Name : null,
                Description: details ? description : null,
                Points: details && a.Points > 0 ? a.Points : null,
                Secret: details && a.Secret ? true : null,
                Icon: icon));
        }
        return items;
    }

    private static IEnumerable<List<AchievementDetailsItem>> Chunks(List<AchievementDetailsItem> items)
    {
        List<AchievementDetailsItem> chunk = new();
        long bytes = 0;
        foreach (AchievementDetailsItem item in items)
        {
            int size = item.Icon?.Length ?? 0;
            if (chunk.Count > 0 && (chunk.Count >= MaxItemsPerRequest || bytes + size > MaxBytesPerRequest))
            {
                yield return chunk;
                chunk = new();
                bytes = 0;
            }
            chunk.Add(item);
            bytes += size;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    private void Backfill(HubClient hub)
    {
        if (_connection.Settings.InstalledAchievementCatalogues is not { } installed) return;
        int offered = 0;
        try
        {
            HashSet<string> sent = Sent();
            foreach (TitleAchievements catalogue in installed())
            {
                if (string.IsNullOrWhiteSpace(catalogue.TitleId) || catalogue.Achievements is not { Count: > 0 }) continue;
                string id = TitleIds.Normalize(catalogue.TitleId);
                if (sent.Contains(SentKey(hub, id, catalogue.Source)) || _queue.ReadAll(QueuePrefix(id)).Count > 0) continue;
                _queue.Enqueue(catalogue with { TitleId = id }, QueuePrefix(id));
                offered++;
            }
        }
        catch (Exception e)
        {
            _connection.Log("could not list installed games' achievements for the hub: " + e.Message);
        }
        if (offered > 0) _connection.Log($"achievement definitions: {offered} installed game(s) queued for the hub");
    }

    /// <summary>
    /// Per hub host (a test hub and the real one are told separately) and per catalogue source,
    /// so a catalogue WPR starts shipping for a title it had generated one for is offered again.
    /// </summary>
    private static string SentKey(HubClient hub, string id, string? source) =>
        hub.BaseUri.Authority.ToLowerInvariant() + "|" + id + "|" + (source ?? "");

    private static string QueuePrefix(string id) => string.Concat(id.Where(char.IsLetterOrDigit)) + "-";

    private HashSet<string> Sent()
    {
        try
        {
            return File.Exists(_sentPath)
                ? new HashSet<string>(File.ReadAllLines(_sentPath), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void MarkSent(HubClient hub, string id, string? source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sentPath)!);
            File.AppendAllLines(_sentPath, new[] { SentKey(hub, id, source) });
        }
        catch (IOException e)
        {
            _connection.Log("could not record sent achievement definitions: " + e.Message);
        }
    }
}
