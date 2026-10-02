using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WPR.Engine.Online;
using WPR.Online.Hub.Client;
using LeaderboardPage = WPR.Engine.Online.LeaderboardPage;

namespace WPR.Online.Hub;

/// <summary>
/// <see cref="ILeaderboardService"/> over the hub's <c>/api/leaderboards</c>.
/// </summary>
/// <remarks>
/// <para><b>Every write is queued, including while signed out.</b> Scores posted before the player
/// signs in are sent the first time a token appears, so nothing played offline is lost. The queue
/// keeps the newest <see cref="MaxPendingPerBoard"/> per board: the hub keeps each player's best,
/// so an old score only matters if it was the best one, and a game that posts every frame would
/// otherwise grow this without bound.</para>
///
/// <para>A write also kicks a background send when signed in, so a score appears on the board
/// during the session rather than at the next launcher start.</para>
/// </remarks>
public sealed class HubLeaderboards : ILeaderboardService
{
    private const int MaxPendingPerBoard = 20;

    internal sealed record PendingScore(string TitleId, string? TitleName, string Key, long Score,
        DateTimeOffset AchievedAt, Dictionary<string, string>? Columns);

    private readonly HubConnection _connection;
    private readonly DiskQueue<PendingScore> _queue;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public HubLeaderboards(HubSettings settings) : this(new HubConnection(settings)) { }

    internal HubLeaderboards(HubConnection connection)
    {
        _connection = connection;
        // Capacity is per board (enforced by prefix), this is only the whole-queue backstop.
        _queue = new DiskQueue<PendingScore>(Path.Combine(connection.Settings.DataDirectory, "scores"), capacity: 5000);
    }

    public bool CanRead => _connection.IsSignedIn;

    public void Submit(string titleId, string? titleName, string boardKey, long score, IReadOnlyDictionary<string, string>? columns = null)
    {
        if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(boardKey)) return;

        try
        {
            string id = TitleIds.Normalize(titleId);
            var item = new PendingScore(id, titleName, boardKey, score, DateTimeOffset.UtcNow,
                columns is null || columns.Count == 0 ? null : new Dictionary<string, string>(columns));
            _queue.Enqueue(item, prefix: BoardPrefix(id, boardKey));
            TrimBoard(id, boardKey);
            _connection.Log($"score queued: {id} {boardKey} = {score}");
        }
        catch (Exception e)
        {
            _connection.Log($"could not queue a score for {titleId} {boardKey}: {e.Message}");
            return;
        }

        if (_connection.IsSignedIn) _ = Task.Run(FlushAsync);
    }

    // Boards registered (or being registered) in this process. A game reads the same board
    // every time its leaderboard screen opens; one call per board per launch is enough.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _registered = new();

    public void EnsureBoard(string titleId, string? titleName, string boardKey)
    {
        if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(boardKey)) return;
        HubClient? hub = _connection.Client();
        if (hub is null) return;

        string id = TitleIds.Normalize(titleId);
        string key = id + "\n" + boardKey;
        if (!_registered.TryAdd(key, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                BoardRegistrations result = await hub.RegisterBoardsAsync(id, new[] { boardKey }, titleName).ConfigureAwait(false);
                foreach (BoardRegistration r in result.Leaderboards)
                    _connection.Log($"board {id} {r.Key}: {r.Status}{(r.Defined ? " (defined)" : "")}{(r.Reason is { } why ? $" ({why})" : "")}");
            }
            catch (Exception e)
            {
                // Offline or throttled: forget it, so the next read of this board tries again.
                _registered.TryRemove(key, out _);
                _connection.Log($"board registration failed for {id} {boardKey}: {e.Message}");
            }
        });
    }

    public async Task<LeaderboardPage?> ReadAsync(string titleId, string boardKey, LeaderboardView view, int offset, int limit, CancellationToken ct = default)
    {
        HubClient? hub = _connection.Client();
        if (hub is null || !hub.IsSignedIn) return null;

        string hubView = view switch
        {
            LeaderboardView.AroundMe => LeaderboardViews.AroundMe,
            LeaderboardView.Friends => LeaderboardViews.Friends,
            _ => LeaderboardViews.Top,
        };

        try
        {
            // Send first: a game that writes a score and immediately reads the board back
            // expects to find itself on it.
            await FlushAsync().ConfigureAwait(false);

            Client.LeaderboardPage page = await hub.GetLeaderboardAsync(titleId, boardKey, hubView,
                Math.Clamp(limit, 1, 100), Math.Max(0, offset), ct).ConfigureAwait(false);

            List<LeaderboardRow> rows = page.Entries
                .Select(e => new LeaderboardRow(e.Rank, e.Username, e.Score, e.Me, Flatten(e.Extra)))
                .ToList();
            return new LeaderboardPage(rows, page.Leaderboard.Players);
        }
        catch (HubException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
        {
            // No board with that key yet: nobody has posted to it. An empty board, not an error.
            return new LeaderboardPage(Array.Empty<LeaderboardRow>(), 0);
        }
        catch (Exception e) when (e is HubException or HttpRequestException or TaskCanceledException or JsonException)
        {
            _connection.Observe(e);
            _connection.Log($"leaderboard read failed for {titleId} {boardKey}: {e.Message}");
            return null;
        }
    }

    public async Task FlushAsync()
    {
        HubClient? hub = _connection.Client();
        if (hub is null || !hub.IsSignedIn) return;
        if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            foreach (var title in _queue.ReadAll().GroupBy(p => p.Item.TitleId))
            {
                foreach (var chunk in title.Chunk(100))
                {
                    List<ScoreSubmission> scores = chunk
                        .Select(p => new ScoreSubmission(p.Item.Key, p.Item.Score, p.Item.AchievedAt,
                            p.Item.Columns?.ToDictionary(kv => kv.Key, kv => (object)kv.Value)))
                        .ToList();

                    ScoreResults results = await hub.SubmitScoresAsync(title.Key, scores, chunk[0].Item.TitleName)
                        .ConfigureAwait(false);

                    // Sent, whatever the verdict: "not_better" and "rejected" are final answers too.
                    foreach (var p in chunk) DiskQueue<PendingScore>.Delete(p.Path);

                    foreach (ScoreResult r in results.Results)
                        _connection.Log($"score {title.Key} {r.Key}: {r.Status}{(r.Rank is { } rank ? $" rank {rank}" : "")}{(r.Reason is { } why ? $" ({why})" : "")}");
                }
            }
        }
        catch (Exception e)
        {
            _connection.Observe(e);
            _connection.Log($"score sync failed, will retry: {e.Message}");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void TrimBoard(string titleId, string key)
    {
        var board = _queue.ReadAll(BoardPrefix(titleId, key));
        for (int i = 0; i < board.Count - MaxPendingPerBoard; i++) DiskQueue<PendingScore>.Delete(board[i].Path);
    }

    /// <summary>A filename-safe per-board prefix: board keys are game data and may hold anything.</summary>
    private static string BoardPrefix(string titleId, string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(titleId + "\n" + key));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant() + "-";
    }

    private static IReadOnlyDictionary<string, string>? Flatten(Dictionary<string, JsonElement>? extra) =>
        extra?.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.GetRawText());
}
