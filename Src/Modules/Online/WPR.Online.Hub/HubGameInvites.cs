using WPR.Engine.Online;
using WPR.Online.Hub.Client;

namespace WPR.Online.Hub;

/// <summary><see cref="IGameInviteService"/> over the hub's friends list and messages.</summary>
public sealed class HubGameInvites : IGameInviteService
{
    private readonly HubSocialService _social;

    internal HubGameInvites(HubSocialService social) => _social = social;

    public bool SignedIn => _social.IsSignedIn;

    public async Task<IReadOnlyList<InviteFriend>> GetFriendsAsync(CancellationToken ct)
    {
        FriendList? list = await _social.GetFriendsAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("WPR Hub did not answer");
        return list.Friends
            .Select(f => new InviteFriend(f.Username, f.Presence?.Status == PresenceStatuses.Online, f.Presence?.TitleName, f.GamerpicUrl))
            .ToList();
    }

    public Task SendAsync(string username, string titleId, string? titleName, CancellationToken ct) =>
        _social.SendInviteAsync(username, titleId, titleName, ct);
}
