using System.Net;
using WPR.Engine.Online;
using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// <see cref="ITitleDetailsSync"/> over the hub's anonymous <c>/api/titles/{id}/details</c>: an
/// installed game's name, publisher, developer, platform and icon, for titles the hub has not
/// been told about.
/// </summary>
/// <remarks>
/// <para><b>The flow.</b> <see cref="Offer"/> writes the details to <c>title-details/</c>, one file
/// per title. <see cref="FlushAsync"/> first asks the hub which fields it still wants, then sends
/// only those, reading the icon from the install at that moment. Either way the title is then
/// written to <c>title-details-sent.txt</c> and never offered to that hub again, so a second
/// install, a repatch or every later launch sends nothing.</para>
///
/// <para><b>Backfill.</b> Games installed before this existed, or while server logging was off,
/// are offered by the first flush of each process from <see cref="HubSettings.InstalledTitles"/>.
/// The sent log makes that cheap after the first time.</para>
///
/// <para><b>Consent.</b> Gated on server logging, like the install receipt this sits beside: it
/// tells the hub that this address installed the game. Nothing is queued or sent with it off.</para>
///
/// <para><b>Rate.</b> The hub allows 30 requests a minute and 300 a day per address, and a title
/// costs one or two. A flush handles at most <see cref="MaxTitlesPerFlush"/> and stops at the
/// first 429, so a library's worth of installs drains over several flushes.</para>
/// </remarks>
public sealed class HubTitleDetails : ITitleDetailsSync
{
    internal const int MaxTitlesPerFlush = 10;

    /// <summary>The hub refuses more (256 KB). WP icons are a few KB.</summary>
    internal const int MaxIconBytes = 256 * 1024;

    private readonly HubConnection _connection;
    private readonly DiskQueue<TitleDetails> _queue;
    private readonly string _sentPath;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private int _backfilled;

    public HubTitleDetails(HubSettings settings) : this(new HubConnection(settings)) { }

    internal HubTitleDetails(HubConnection connection)
    {
        _connection = connection;
        string root = connection.Settings.DataDirectory;
        _queue = new DiskQueue<TitleDetails>(Path.Combine(root, "title-details"), capacity: 500);
        _sentPath = Path.Combine(root, "title-details-sent.txt");
    }

    public void Offer(TitleDetails details)
    {
        try
        {
            if (!_connection.ServerLogging || details is null || string.IsNullOrWhiteSpace(details.TitleId)) return;
            if (_connection.Client() is not { } hub) return;

            string id = TitleIds.Normalize(details.TitleId);
            if (Sent().Contains(SentKey(hub, id))) return;
            // One file per title: the prefix is the id, so a second offer before a flush is a no-op.
            if (_queue.ReadAll(QueuePrefix(id)).Count > 0) return;

            _queue.Enqueue(details with { TitleId = id }, QueuePrefix(id));
            _connection.Log($"title details queued: {details.Name ?? id}");
        }
        catch (Exception e)
        {
            _connection.Log("could not queue title details: " + e.Message);
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
            foreach ((string path, TitleDetails details) in _queue.ReadAll())
            {
                if (handled++ >= MaxTitlesPerFlush) break;
                string id = TitleIds.Normalize(details.TitleId);
                if (sent.Contains(SentKey(hub, id))) { DiskQueue<TitleDetails>.Delete(path); continue; }

                try
                {
                    await SendAsync(hub, id, details).ConfigureAwait(false);
                    MarkSent(hub, id);
                    DiskQueue<TitleDetails>.Delete(path);
                }
                catch (HubException e) when (e.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound)
                {
                    // The hub will never take this as it is (an icon it refuses, an id it does not
                    // accept): retrying every flush would only spend the rate limit.
                    _connection.Log($"title details for {details.Name ?? id} refused: {e.Message}");
                    MarkSent(hub, id);
                    DiskQueue<TitleDetails>.Delete(path);
                }
                catch (Exception e)
                {
                    // Offline, rate-limited, the hub down: keep everything for the next flush.
                    _connection.Log($"title details not sent ({e.Message}); will retry");
                    break;
                }
            }
        }
        catch (Exception e)
        {
            _connection.Log("title details flush failed: " + e.Message);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task SendAsync(HubClient hub, string id, TitleDetails details)
    {
        TitleDetailsWants wants = (await hub.GetTitleDetailsAsync(id).ConfigureAwait(false)).Wants;
        TitleDetailsContribution body = Contribution(details, wants);
        if (body == Empty)
        {
            _connection.Log($"title details for {details.Name ?? id}: the hub has everything we have");
            return;
        }

        TitleDetailsResult result = await hub.ContributeTitleDetailsAsync(id, body).ConfigureAwait(false);
        _connection.Log($"title details for {details.Name ?? id}: hub kept [{string.Join(", ", result.Accepted)}]");
    }

    private static readonly TitleDetailsContribution Empty = new();

    /// <summary>Only what the hub wants and we have. An unusable icon is left out, not fatal.</summary>
    internal static TitleDetailsContribution Contribution(TitleDetails details, TitleDetailsWants wants) => new(
        Name: wants.Name ? Clean(details.Name) : null,
        Publisher: wants.Publisher ? Clean(details.Publisher) : null,
        Developer: wants.Developer ? Clean(details.Developer) : null,
        Platform: wants.Platform ? Clean(details.Platform) : null,
        Icon: wants.Icon ? ReadIcon(details.IconFile) : null);

    /// <summary>
    /// Manifests hold placeholders WPR resolves only sometimes: an unresolved resource reference
    /// (<c>@AppResLib.dll,-100</c>) is not a name.
    /// </summary>
    private static string? Clean(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) || value.StartsWith('@') ? null : value;
    }

    /// <summary>Base64 of the icon when it is a PNG or JPEG within the hub's size limit; else null.</summary>
    internal static string? ReadIcon(string? file)
    {
        try
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            if (new FileInfo(file).Length is 0 or > MaxIconBytes) return null;
            byte[] bytes = File.ReadAllBytes(file);
            bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';
            bool jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
            return png || jpeg ? Convert.ToBase64String(bytes) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Backfill(HubClient hub)
    {
        if (_connection.Settings.InstalledTitles is not { } installed) return;
        int offered = 0;
        try
        {
            HashSet<string> sent = Sent();
            foreach (TitleDetails details in installed())
            {
                if (string.IsNullOrWhiteSpace(details.TitleId)) continue;
                string id = TitleIds.Normalize(details.TitleId);
                if (sent.Contains(SentKey(hub, id)) || _queue.ReadAll(QueuePrefix(id)).Count > 0) continue;
                _queue.Enqueue(details with { TitleId = id }, QueuePrefix(id));
                offered++;
            }
        }
        catch (Exception e)
        {
            _connection.Log("could not list installed games for the hub: " + e.Message);
        }
        if (offered > 0) _connection.Log($"title details: {offered} installed game(s) queued for the hub");
    }

    /// <summary>Per hub host, so a test hub and the real one are told separately.</summary>
    private static string SentKey(HubClient hub, string id) => hub.BaseUri.Authority.ToLowerInvariant() + "|" + id;

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

    private void MarkSent(HubClient hub, string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sentPath)!);
            File.AppendAllLines(_sentPath, new[] { SentKey(hub, id) });
        }
        catch (IOException e)
        {
            _connection.Log("could not record sent title details: " + e.Message);
        }
    }
}
