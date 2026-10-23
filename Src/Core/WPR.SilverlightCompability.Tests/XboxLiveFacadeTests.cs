using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xbox;
using Microsoft.Xbox.Foundation;
using Microsoft.Xbox.Leaderboards;
using Windows.Foundation;
using WPR.WinRT;
using WPR.XboxLive;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Microsoft.Xbox for managed WP8 titles (WPR.Framework.Xbox) over a fake IXboxLiveHost, and the
/// WinRT async objects it returns (WPR.Framework.WinRT): what a title's await, Completed handler
/// and += on a WinRT event actually do.
/// </summary>
[Collection("XboxLiveBackend")]
public class XboxLiveFacadeTests : IDisposable
{
    private readonly FakeHost _host = new();

    public XboxLiveFacadeTests() => XboxLiveBackend.SetHost(_host);

    public void Dispose() => XboxLiveBackend.SetHost(null);

    [Fact]
    public async Task SignIn_is_the_hosts_player()
    {
        UserIdentity identity = await XboxLIVEService.SignInAsync();

        Assert.Equal("HubPlayer", identity.Gamertag);
        Assert.Equal(identity.Xuid, XboxLIVEService.SignedInUserIdentity.Xuid);
        Assert.Equal("HubPlayer", new User().Identity.Gamertag);
        Assert.False(string.IsNullOrEmpty(XboxLIVEService.ServiceClient.Token));
    }

    [Fact]
    public void Without_a_host_sign_in_still_succeeds()
    {
        XboxLiveBackend.SetHost(null);

        IAsyncOperation<UserIdentity> op = XboxLIVEService.SignInAsync();

        Assert.Equal(AsyncStatus.Completed, op.Status);
        Assert.Equal("Player", op.GetResults().Gamertag);
    }

    [Fact]
    public async Task Achievements_page_and_filter_the_hosts_list()
    {
        var user = new User();

        AchievementCollection all = await user.GetAchievementsAsync(0, 0, false, null);
        AchievementCollection page = await user.GetAchievementsAsync(1, 1, false, null);
        AchievementCollection earned = await user.GetAchievementsAsync(0, 10, true, null);

        Assert.Equal(new uint[] { 1, 2, 3 }, all.Items.Select(a => a.Id));
        Assert.Equal(3u, all.TotalRecords);
        Assert.Equal(2u, Assert.Single(page.Items).Id);
        Assert.Equal(2u, Assert.Single(earned.Items).Id);
        Assert.True(earned.Items[0].IsEarned);
        Assert.Equal(20, earned.Items[0].Gamerscore);
        Assert.Equal(20, (await user.GetProfileAsync()).Gamerscore);
    }

    [Fact]
    public async Task Unlock_reaches_the_host()
    {
        await new User().UnlockAchievementAsync(3);

        Assert.Equal(new uint[] { 3 }, _host.Unlocked);
    }

    [Fact]
    public async Task Another_player_has_no_achievements_and_unlocks_nothing()
    {
        var other = new User(42, "SomeoneElse");

        Assert.Empty((await other.GetAchievementsAsync(0, 0, false, null)).Items);
        await other.UnlockAchievementAsync(1);
        Assert.Empty(_host.Unlocked);
    }

    [Fact]
    public async Task Leaderboard_posts_map_verb_and_columns()
    {
        var service = new LeaderboardService();

        await service.PostResultAsync(7, LeaderboardAggregation.Min, 1234);
        var score = new LeaderboardAttribute();
        score.put_AttributeId(0);
        score.put_Verb(LeaderboardAggregation.Max);
        score.put_Type(LeaderboardValueType.Int64);
        score.put_Value("900");
        await service.PostResultsAsync(8, new[] { score, new LeaderboardAttribute(1, LeaderboardAggregation.Replace, LeaderboardValueType.Int32, "5") });

        Assert.Equal((7u, 2, 1234L), _host.Posted[0]);
        Assert.Equal(8u, _host.PostedColumns[0].Board);
        Assert.Equal(new[] { "0=900/3", "1=5/1" }, _host.PostedColumns[0].Columns.Select(c => $"{c.AttributeId}={c.Value}/{c.Verb}"));
    }

    [Fact]
    public async Task Leaderboard_read_has_rows_and_always_a_user_row()
    {
        var service = new LeaderboardService();

        Leaderboard board = await service.GetLeaderboardAsync(0, 10, 7, true, null, null);
        Leaderboard empty = await service.GetLeaderboardAsync(0, 10, 99, true, null, null);

        Assert.Equal(new[] { "Top", "HubPlayer" }, board.UserList.Select(r => r.Gamertag));
        Assert.Equal("500", board.UserList[0].Rating);
        Assert.Equal(2u, board.UserRow.Rank);
        Assert.Equal(XboxLIVEService.SignedInUserIdentity.Xuid, board.UserRow.Xuid);
        Assert.NotEqual(board.UserRow.Xuid, board.UserList[0].Xuid);
        Assert.Equal(5u, board.TotalRecords);
        Assert.Empty(empty.UserList);
        Assert.Equal(0u, empty.UserRow.Rank);
        Assert.Equal(new uint[] { 7 }, (await service.GetLeaderboardsAsync()).Items.Select(m => m.LeaderboardId));
    }

    [Fact]
    public void Completed_set_after_completion_fires_at_once_and_once()
    {
        IAsyncAction action = WinRtAsync.Completed();
        int calls = 0;
        AsyncStatus seen = AsyncStatus.Started;

        action.put_Completed((_, status) => { calls++; seen = status; });

        Assert.Equal(1, calls);
        Assert.Equal(AsyncStatus.Completed, seen);
    }

    [Fact]
    public void Completed_set_before_completion_fires_when_the_task_ends()
    {
        var tcs = new TaskCompletionSource<int>();
        var op = new TaskAsyncOperation<int>(tcs.Task);
        using var fired = new ManualResetEventSlim();
        int result = 0;

        op.put_Completed((o, _) => { result = o.GetResults(); fired.Set(); });
        Assert.Throws<InvalidOperationException>(() => op.GetResults());
        tcs.SetResult(9);

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(9, result);
    }

    [Fact]
    public async Task A_failed_operation_reports_error_and_rethrows()
    {
        IAsyncOperation<int> op = WinRtAsync.Failed<int>(new InvalidOperationException("nope"));

        Assert.Equal(AsyncStatus.Error, op.Status);
        Assert.Equal("nope", op.ErrorCode?.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await op);
    }

    [Fact]
    public async Task Await_bridges_a_foreign_operation_through_Completed()
    {
        var foreign = new ForeignOperation();

        Task<string> pending = foreign.AsTask();
        foreign.Finish("done");

        Assert.Equal("done", await pending);
    }

    [Fact]
    public async Task Awaiting_a_null_operation_completes_with_a_default()
    {
        IAsyncOperation<string> stubbed = null!;
        IAsyncAction stubbedAction = null!;

        Assert.Null(await stubbed);
        await stubbedAction;
    }

    [Fact]
    public void Event_handlers_round_trip_through_WindowsRuntimeMarshal()
    {
        int removed = 0;
        var table = new EventRegistrationTokenTable<EventHandler>();
        EventHandler handler = (_, _) => { };

        WindowsRuntimeMarshal.AddEventHandler<EventHandler>(table.AddEventHandler, Remove, handler);
        Assert.NotNull(table.InvocationList);
        WindowsRuntimeMarshal.RemoveEventHandler<EventHandler>(Remove, handler);

        Assert.Equal(1, removed);
        Assert.Null(table.InvocationList);

        void Remove(EventRegistrationToken token)
        {
            removed++;
            table.RemoveEventHandler(token);
        }
    }

    [Fact]
    public void SignedOut_can_be_subscribed_and_unsubscribed()
    {
        EventHandler<SignedOutEventArgs> handler = (_, _) => { };

        WindowsRuntimeMarshal.AddEventHandler<EventHandler<SignedOutEventArgs>>(XboxLIVEService.add_SignedOut, XboxLIVEService.remove_SignedOut, handler);
        WindowsRuntimeMarshal.RemoveEventHandler<EventHandler<SignedOutEventArgs>>(XboxLIVEService.remove_SignedOut, handler);
    }

    [Fact]
    public void Multiplayer_fails_as_an_unreachable_service()
    {
        IAsyncOperation<Microsoft.Xbox.Multiplayer.MultiplayerSession> op =
            new User().CreateMultiplayerSessionAsync(new Microsoft.Xbox.Multiplayer.MultiplayerSessionSettings(), Microsoft.Xbox.Multiplayer.PlayerAcl.All);

        Assert.Equal(AsyncStatus.Error, op.Status);
        Assert.Equal((int)ErrorCode.ServerConnectionFailure, op.ErrorCode!.HResult);
    }

    [Fact]
    public void Projected_value_types_behave_as_on_a_phone()
    {
        var size = new global::Windows.Foundation.Size(3, 4);
        var rect = new global::Windows.Foundation.Rect(new global::Windows.Foundation.Point(1, 2), size);

        Assert.Equal(3, size.Width);
        Assert.Equal(6, rect.Bottom);
        Assert.True(rect.Contains(new global::Windows.Foundation.Point(2, 3)));
        Assert.True(global::Windows.Foundation.Size.Empty.IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => new global::Windows.Foundation.Size(-1, 0));
        Assert.Equal(global::Windows.UI.Color.FromArgb(1, 2, 3, 4), new global::Windows.UI.Color { A = 1, R = 2, G = 3, B = 4 });
    }

    private sealed class FakeHost : IXboxLiveHost
    {
        public List<uint> Unlocked { get; } = new();
        public List<(uint Board, int Aggregation, long Value)> Posted { get; } = new();
        public List<(uint Board, IReadOnlyList<XboxLeaderboardAttribute> Columns)> PostedColumns { get; } = new();

        public string Gamertag => "HubPlayer";

        public IReadOnlyList<XboxAchievement> GetAchievements() => new[]
        {
            new XboxAchievement(1, "One", "first", 10, false, false, null),
            new XboxAchievement(2, "Two", "second", 20, false, true, new DateTime(2026, 10, 1)),
            new XboxAchievement(3, "Three", "third", 30, true, false, null),
        };

        public void UnlockAchievement(uint achievementId) => Unlocked.Add(achievementId);

        public void PostResult(uint leaderboardId, int aggregation, long value) => Posted.Add((leaderboardId, aggregation, value));

        public void PostResults(uint leaderboardId, IReadOnlyList<XboxLeaderboardAttribute> attributes) => PostedColumns.Add((leaderboardId, attributes));

        public XboxLeaderboardPage? ReadLeaderboard(uint leaderboardId, uint skip, uint max, bool titleView) =>
            leaderboardId == 7
                ? new XboxLeaderboardPage(new[] { new XboxLeaderboardRow(1, "Top", 500, false), new XboxLeaderboardRow(2, "HubPlayer", 300, true) }, 5)
                : null;

        public IReadOnlyList<uint> KnownLeaderboards() => new uint[] { 7 };
    }

    /// <summary>An IAsyncOperation WPR did not make, completed later through its Completed handler.</summary>
    private sealed class ForeignOperation : IAsyncOperation<string>
    {
        private AsyncOperationCompletedHandler<string>? _completed;
        private string? _result;

        public uint Id => 1;
        public AsyncStatus Status { get; private set; } = AsyncStatus.Started;
        public Exception? ErrorCode => null;
        public void Cancel() { }
        public void Close() { }
        public AsyncOperationCompletedHandler<string>? get_Completed() => _completed;
        public void put_Completed(AsyncOperationCompletedHandler<string>? handler) => _completed = handler;
        public string GetResults() => _result!;

        public void Finish(string result)
        {
            _result = result;
            Status = AsyncStatus.Completed;
            _completed?.Invoke(this, Status);
        }
    }
}
