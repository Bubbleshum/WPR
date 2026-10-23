using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WPR.Engine.Online;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>Guide.ShowGameInvite: the friend picker and the invite messages it sends.</summary>
[Collection(nameof(GameNetworkRoutesTests))] // OnlineBackend and the picker are process-wide
public class GameInvitesTests
{
    private const string PoolPro = "062d24c2-d398-4cff-9515-1f542495d8c0";

    private sealed class FakeInvites : IGameInviteService
    {
        public bool SignedIn { get; set; } = true;
        public List<InviteFriend> Friends { get; } = new();
        public List<(string To, string Title, string? Name)> Sent { get; } = new();

        public Task<IReadOnlyList<InviteFriend>> GetFriendsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InviteFriend>>(Friends);

        public Task SendAsync(string username, string titleId, string? titleName, CancellationToken ct)
        {
            if (username == "broken") throw new System.InvalidOperationException("hub said no");
            Sent.Add((username, titleId, titleName));
            return Task.CompletedTask;
        }
    }

    private static FakeInvites Use()
    {
        var fake = new FakeInvites();
        OnlineBackend.Set(new OnlineServices { Invites = fake });
        return fake;
    }

    [Fact]
    public async Task Picked_friends_get_an_invite_and_online_friends_are_listed_first()
    {
        FakeInvites hub = Use();
        hub.Friends.AddRange(new[] { new InviteFriend("zed", true, null), new InviteFriend("amy", false, null), new InviteFriend("bob", true, "Pool Pro Online 3") });
        IReadOnlyList<InviteFriend>? shown = null;
        bool closed = false;
        GameInvites.Picker = (name, friends, empty) =>
        {
            Assert.Equal("Pool Pro Online 3", name);
            Assert.Null(empty);
            shown = friends;
            return Task.FromResult<IReadOnlyList<string>?>(new[] { "bob", "broken", "amy" });
        };
        try
        {
            int sent = await GameInvites.ShowAsync(PoolPro, "Pool Pro Online 3", () => closed = true);

            Assert.Equal(new[] { "bob", "zed", "amy" }, shown!.Select(f => f.Username));
            Assert.Equal(2, sent); // one failure doesn't stop the rest
            Assert.Equal(new[] { ("bob", PoolPro, (string?)"Pool Pro Online 3"), ("amy", PoolPro, "Pool Pro Online 3") }, hub.Sent);
            Assert.True(closed);
        }
        finally
        {
            GameInvites.Picker = null;
            OnlineBackend.Set(new OnlineServices());
        }
    }

    [Fact]
    public async Task Cancel_signed_out_and_no_picker_send_nothing()
    {
        FakeInvites hub = Use();
        hub.Friends.Add(new InviteFriend("amy", true, null));
        try
        {
            GameInvites.Picker = (_, _, _) => Task.FromResult<IReadOnlyList<string>?>(null);
            Assert.Equal(0, await GameInvites.ShowAsync(PoolPro, "Pool Pro Online 3"));

            hub.SignedIn = false;
            string? message = null;
            int friendsShown = -1;
            GameInvites.Picker = (_, friends, empty) =>
            {
                message = empty;
                friendsShown = friends.Count;
                return Task.FromResult<IReadOnlyList<string>?>(new[] { "amy" });
            };
            bool closed = false;
            Assert.Equal(0, await GameInvites.ShowAsync(PoolPro, "Pool Pro Online 3", () => closed = true));
            Assert.Contains("Sign in", message);
            Assert.Equal(0, friendsShown);
            Assert.True(closed);

            GameInvites.Picker = null;
            closed = false;
            Assert.Equal(0, await GameInvites.ShowAsync(PoolPro, "Pool Pro Online 3", () => closed = true));
            Assert.True(closed); // Guide.IsVisible must not stay set when there is no picker
            Assert.Empty(hub.Sent);
        }
        finally
        {
            GameInvites.Picker = null;
            OnlineBackend.Set(new OnlineServices());
        }
    }
}
