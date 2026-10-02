using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WPR.Engine.Online;
using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary>
/// <see cref="ICrashReporter"/> over the hub's <c>/api/diagnostics</c> and
/// <c>/api/game-files</c>, for players with server logging on.
/// </summary>
/// <remarks>
/// <para><b>The flow.</b> <see cref="Report"/> writes the crash to <c>reports/</c>.
/// <see cref="FlushAsync"/> sends each one with the package's SHA-256 from
/// <see cref="GamePackageRecord"/>. If the hub has never seen that build, the receipt carries an
/// upload offer; it is written to <c>uploads/</c> and the package is streamed up, resumably,
/// through <see cref="HubClient.UploadGameFileAsync"/>. Server logging is the consent for both,
/// so neither prompts.</para>
///
/// <para><b>Works signed out.</b> The diagnostics endpoint is anonymous and the upload is
/// authorised by the one-time token in the receipt, so none of this waits for sign-in. Signed
/// in, the report is linked to the account and the hub applies the account's own server-logging
/// setting rather than the one sent.</para>
///
/// <para><b>The package is re-checked before it is sent.</b> The hub only knows the hash the
/// report named; a file that no longer hashes to it (the library copy was replaced by another
/// build) would be uploaded in full and then thrown away by the hub's verifier. It is dropped
/// here instead.</para>
/// </remarks>
public sealed class HubCrashReporter : ICrashReporter
{
    /// <summary>Anything older is dropped unsent: the build it names has almost certainly moved on.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private const int MaxLogTailChars = Diagnostics.MaxLogTailChars;

    internal sealed record PendingUpload(GameFileOffer Offer, GamePackageRecord Package, string? TitleId, string? TitleName, DateTimeOffset QueuedAt);

    private readonly HubConnection _connection;
    private readonly DiskQueue<CrashReport> _reports;
    private readonly DiskQueue<PendingUpload> _uploads;
    private readonly string _installIdPath;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    /// <summary>Upload progress for a status indicator: (title, sent, total).</summary>
    public event Action<string?, long, long>? UploadProgress;

    public HubCrashReporter(HubSettings settings) : this(new HubConnection(settings)) { }

    internal HubCrashReporter(HubConnection connection)
    {
        _connection = connection;
        string root = connection.Settings.DataDirectory;
        _reports = new DiskQueue<CrashReport>(Path.Combine(root, "reports"), capacity: 20);
        // Generous: every install can queue one, and the hub allows only a few upload starts per
        // hour per IP (WPR_GAME_FILES_STARTS_PER_HOUR, default 6), so installing a library's worth
        // of new games drains this over several flushes. Each item is a few hundred bytes.
        _uploads = new DiskQueue<PendingUpload>(Path.Combine(root, "uploads"), capacity: 100);
        _installIdPath = Path.Combine(root, "install-id.txt");
    }

    public void Report(CrashReport report)
    {
        try
        {
            if (!_connection.ServerLogging) return;

            // A managed exception that kills the process on a worker thread is queued by the
            // process that died, and then its launcher sees a process with no exit reason and
            // reports a native crash. Keep the managed one: it has the stack.
            if (report.Source == CrashReport.Sources.NativeCrash
                && _reports.ReadAll().Any(r => r.Item.TitleId == report.TitleId
                                               && (report.OccurredAt - r.Item.OccurredAt).Duration() < TimeSpan.FromMinutes(2)))
            {
                _connection.Log($"native crash for {report.TitleName ?? report.TitleId} already covered by a managed report");
                return;
            }

            report.LogTail ??= ReadLogTail(report.InstallFolder) ?? RecentSessionLog();
            _reports.Enqueue(report);
            _connection.Log($"crash queued: {report.TitleName ?? report.TitleId} {report.ExceptionType} ({report.Source})");
        }
        catch (Exception e)
        {
            // Never let crash reporting become a second crash.
            _connection.Log("could not queue a crash report: " + e.Message);
        }
    }

    /// <summary>
    /// A report the player asked to send, from the settings page: their description plus the
    /// recent log. Sent now rather than queued, so the page can show the code to quote.
    /// </summary>
    /// <remarks>
    /// <para><b>Not gated on server logging.</b> Pressing the button is the consent for this one
    /// report. It is a hub <c>manual</c> report, which is never grouped as a crash and never asks for
    /// a game package.</para>
    ///
    /// <para>The log goes twice: its last ~60 KB inside the report, where the admin page shows it,
    /// and the whole window gzipped through the receipt's one-time log upload (up to the hub's 5 MB).
    /// Both are scrubbed of user names, home paths, emails and IP addresses first.</para>
    /// </remarks>
    /// <returns>The report code, e.g. <c>WPR-XZA4AVX2</c>.</returns>
    /// <param name="game">The game the report is about, when there is one (a logging run). It fills the
    /// hub's own title fields, which are what name the report on the admin pages, and the package hash
    /// from the install's <c>wpr-package.json</c>, which links it to that exact build.</param>
    public async Task<string> SendManualAsync(string? comment, string log, IReadOnlyDictionary<string, string>? extra = null,
        ManualReportGame? game = null, CancellationToken ct = default)
    {
        HubClient hub = _connection.Client() ?? throw new InvalidOperationException("No WPR Hub server is set.");

        string scrubbed = Diagnostics.Scrub(log) ?? "";
        GamePackageRecord? package = GamePackageRecord.TryLoad(game?.InstallFolder);
        var fields = new Dictionary<string, string> { ["source"] = "manual" };
        if (extra != null) foreach (var kv in extra) fields[kv.Key] = kv.Value;

        var report = new DiagnosticReport
        {
            Kind = "manual",
            InstallId = Diagnostics.GetOrCreateInstallId(_installIdPath),
            WprVersion = Truncate(_connection.Settings.WprVersion, 32)!,
            Platform = _connection.Settings.Platform,
            OsVersion = Truncate(Environment.OSVersion.Version.ToString(), 64),
            Arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            UserComment = Truncate(Diagnostics.Scrub(comment?.Trim()), 2000),
            TitleId = game?.TitleId is null ? null : TitleIds.Normalize(game.TitleId),
            TitleName = Truncate(game?.TitleName, 255),
            XapHash = package?.Sha256,
            XapSize = package?.Size,
            RuntimePath = game?.RuntimePath,
            GraphicsBackend = Truncate(game?.GraphicsBackend?.ToLowerInvariant(), 16),
            LogTail = string.IsNullOrEmpty(scrubbed) ? null : TruncateStart(scrubbed, MaxLogTailChars),
            Extra = fields,
            ServerLogging = _connection.ServerLogging,
        };

        DiagnosticReceipt receipt;
        try
        {
            receipt = await hub.SendDiagnosticsAsync(report, linkToAccount: hub.IsSignedIn, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            throw;
        }
        _connection.Log($"manual report sent: {receipt.Code} ({scrubbed.Length} chars of log)");

        if (scrubbed.Length > MaxLogTailChars)
        {
            try
            {
                // 16M characters of log compresses far under the hub's 5 MB; past that, keep the end.
                using var body = new MemoryStream(Encoding.UTF8.GetBytes(TruncateStart(scrubbed, 16_000_000)!));
                await hub.UploadLogAsync(receipt, body, ct).ConfigureAwait(false);
                _connection.Log($"full log uploaded for {receipt.Code}");
            }
            catch (Exception e) when (e is HubException or HttpRequestException or TaskCanceledException)
            {
                // The report itself is in; the full log is a bonus.
                _connection.Log($"full log upload for {receipt.Code} failed: {e.Message}");
            }
        }

        return receipt.Code;
    }

    public async Task FlushAsync()
    {
        if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!_connection.ServerLogging)
            {
                // Consent withdrawn: forget everything, including uploads that were half done.
                _reports.Clear();
                _uploads.Clear();
                return;
            }

            HubClient? hub = _connection.Client();
            if (hub is null) return; // no hub configured: keep the queue, capped, for when there is one

            await SendReportsAsync(hub).ConfigureAwait(false);
            await SendUploadsAsync(hub).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _connection.Log("crash flush failed: " + e.Message);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task SendReportsAsync(HubClient hub)
    {
        foreach ((string path, CrashReport crash) in _reports.ReadAll())
        {
            if (crash.OccurredAt < DateTimeOffset.UtcNow - MaxAge)
            {
                DiskQueue<CrashReport>.Delete(path);
                continue;
            }

            GamePackageRecord? package = GamePackageRecord.TryLoad(crash.InstallFolder);
            if (crash.Source == CrashReport.Sources.Install && package is null)
            {
                // An install report exists only to carry the fingerprint; without one (the game
                // was uninstalled before the flush) there is nothing to say.
                DiskQueue<CrashReport>.Delete(path);
                continue;
            }

            DiagnosticReceipt receipt;
            try
            {
                receipt = await hub.SendDiagnosticsAsync(Build(crash, package), linkToAccount: hub.IsSignedIn).ConfigureAwait(false);
            }
            catch (HubException e) when (e.Status is System.Net.HttpStatusCode.UnprocessableEntity or System.Net.HttpStatusCode.RequestEntityTooLarge)
            {
                // The hub will never accept this one; retrying it for ever helps nobody.
                _connection.Log($"crash report rejected ({(int)e.Status!}): {e.Message}");
                DiskQueue<CrashReport>.Delete(path);
                continue;
            }
            catch (Exception e) when (e is HubException or HttpRequestException or TaskCanceledException)
            {
                _connection.Observe(e);
                _connection.Log("crash report not sent, will retry: " + e.Message);
                return; // offline or throttled: stop, keep the rest queued in order
            }

            DiskQueue<CrashReport>.Delete(path);
            _connection.Log($"crash report sent: {receipt.Code} ({crash.TitleName ?? crash.TitleId})"
                + (receipt.GameFile.Wanted ? " - the hub asked for the game package" : receipt.GameFile.Reason is { } why ? $" - package not wanted: {why}" : ""));

            if (receipt.GameFile.Wanted && package is not null)
            {
                _uploads.Enqueue(new PendingUpload(receipt.GameFile, package, crash.TitleId, crash.TitleName, DateTimeOffset.UtcNow),
                    prefix: package.Sha256 + "-");
            }
        }
    }

    private async Task SendUploadsAsync(HubClient hub)
    {
        IGamePackageSource? source = _connection.Settings.Packages;

        // One upload per build: several crashes of the same game each bring an offer.
        foreach (var group in _uploads.ReadAll().GroupBy(u => u.Item.Package.Sha256))
        {
            var newest = group.Last();
            foreach (var stale in group.SkipLast(1)) DiskQueue<PendingUpload>.Delete(stale.Path);

            PendingUpload upload = newest.Item;
            if (source is null || upload.QueuedAt < DateTimeOffset.UtcNow - MaxAge)
            {
                DiskQueue<PendingUpload>.Delete(newest.Path);
                continue;
            }

            string? file = await Task.Run(() => source.Acquire(upload.Package)).ConfigureAwait(false);
            if (file is null)
            {
                // Not reachable right now (library drive unplugged, grant revoked). Kept until
                // it ages out; the offer resumes where it left off whenever the file is back.
                _connection.Log($"game package for {upload.TitleName} is not reachable; upload deferred");
                continue;
            }

            try
            {
                if (!await MatchesAsync(file, upload.Package).ConfigureAwait(false))
                {
                    _connection.Log($"game package for {upload.TitleName} no longer matches the build that crashed; not uploading it");
                    DiskQueue<PendingUpload>.Delete(newest.Path);
                    continue;
                }

                var progress = new Progress<(long Sent, long Total)>(p => UploadProgress?.Invoke(upload.TitleName, p.Sent, p.Total));
                GameFileUploadState state = await hub.UploadGameFileAsync(file, upload.Offer, upload.TitleId, upload.TitleName, progress)
                    .ConfigureAwait(false);

                _connection.Log($"game package upload for {upload.TitleName}: {state.Status} ({state.Offset}/{state.Size}){(state.Error is { } err ? " " + err : "")}");

                // Everything but "still receiving" is final: complete, failed (hash mismatch on
                // the hub), in_progress (another player is sending it), not_wanted.
                if (state.Status != "receiving") DiskQueue<PendingUpload>.Delete(newest.Path);
            }
            catch (HubException e) when (e.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // Server logging switched off on the account, the token spent, or the report pruned.
                _connection.Log($"game package upload for {upload.TitleName} refused: {e.Message}");
                DiskQueue<PendingUpload>.Delete(newest.Path);
            }
            catch (Exception e) when (e is HubException or HttpRequestException or IOException or TaskCanceledException)
            {
                _connection.Observe(e);
                _connection.Log($"game package upload for {upload.TitleName} interrupted, will resume: {e.Message}");
            }
            finally
            {
                try { source.Release(upload.Package, file); } catch { /* best effort */ }
            }
        }
    }

    private DiagnosticReport Build(CrashReport crash, GamePackageRecord? package)
    {
        var extra = new Dictionary<string, string> { ["source"] = crash.Source, ["occurred_at"] = crash.OccurredAt.ToString("O") };

        return new DiagnosticReport
        {
            // An install is not a crash, and must not be grouped or counted as one.
            Kind = crash.Source == CrashReport.Sources.Install ? "manual" : "crash",
            InstallId = Diagnostics.GetOrCreateInstallId(_installIdPath),
            WprVersion = Truncate(_connection.Settings.WprVersion, 32)!,
            Platform = _connection.Settings.Platform,
            OsVersion = Truncate(Environment.OSVersion.Version.ToString(), 64),
            Arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            TitleId = crash.TitleId is null ? null : TitleIds.Normalize(crash.TitleId),
            TitleName = Truncate(crash.TitleName, 255),
            XapHash = package?.Sha256,
            XapSize = package?.Size,
            RuntimePath = crash.RuntimePath,
            GraphicsBackend = Truncate(crash.GraphicsBackend?.ToLowerInvariant(), 16),
            ExceptionType = Truncate(crash.ExceptionType, 255),
            ExceptionMessage = Truncate(Diagnostics.Scrub(crash.ExceptionMessage), 4000),
            StackTrace = Truncate(Diagnostics.Scrub(crash.StackTrace), 65536),
            LogTail = string.IsNullOrEmpty(crash.LogTail) ? null : TruncateStart(Diagnostics.Scrub(crash.LogTail), MaxLogTailChars),
            Extra = extra,
            ServerLogging = true,
        };
    }

    private static async Task<bool> MatchesAsync(string file, GamePackageRecord package)
    {
        try
        {
            if (new FileInfo(file).Length != package.Size) return false;
            await using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The app's session log for the last ten minutes, trimmed to the report's limit.</summary>
    private string? RecentSessionLog()
    {
        try
        {
            string? text = _connection.Settings.RecentLog?.Invoke(TimeSpan.FromMinutes(10));
            return string.IsNullOrEmpty(text) ? null : TruncateStart(text, MaxLogTailChars);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The last <see cref="MaxLogTailChars"/> characters of the per-game log, if there is one.</summary>
    private static string? ReadLogTail(string? installFolder)
    {
        if (string.IsNullOrEmpty(installFolder)) return null;
        string path = Path.Combine(installFolder, "wpr_game_debug.log");
        try
        {
            if (!File.Exists(path)) return null;
            // FileShare.ReadWrite: the game's trace listener may still hold it open.
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - MaxLogTailChars * 2L);
            fs.Position = start;
            using StreamReader reader = new(fs, Encoding.UTF8);
            string text = reader.ReadToEnd();
            return TruncateStart(text, MaxLogTailChars);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static string? TruncateStart(string? s, int max) => s is null || s.Length <= max ? s : s[^max..];
}

/// <summary>What a manual report is about, for <see cref="HubCrashReporter.SendManualAsync"/>.</summary>
public sealed record ManualReportGame(string TitleId, string? TitleName, string? InstallFolder = null,
    string? RuntimePath = null, string? GraphicsBackend = null);
