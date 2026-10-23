using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xbox.Foundation;
using Microsoft.Xbox.Multiplayer;
using Windows.Foundation;
using WPR.WinRT;
using WPR.XboxLive;

namespace Microsoft.Xbox
{
    public interface IUser
    {
        UserIdentity Identity { get; }
        UserStatus Status { get; }
        IAsyncOperation<UserProfile> GetProfileAsync();
        IAsyncOperation<UserProfile> SetProfileAsync(UserProfile profile);
        IAsyncOperation<UserCollection> GetFriendsAsync();
        IAsyncOperation<AchievementCollection> GetAchievementsAsync(uint skipItems, uint maxItems, bool unlockedOnly, AchievementCollection? previousPage);
        IAsyncAction UnlockAchievementAsync(uint achievementId);
        IAsyncOperation<AvatarManifest> GetAvatarManifestAsync();
        IAsyncAction PostUserMessageAsync(UserMessage message);
        IAsyncOperation<UserMessageCollection> GetUserMessagesAsync(uint skipItems, uint maxItems, UserMessageCollection? previousPage);
        IAsyncOperation<UserMessageCollection> GetUserMessagesForTitleAsync(uint titleId, uint skipItems, uint maxItems, UserMessageCollection? previousPage);
        IAsyncAction DeleteUserMessageAsync(string messageId);
        IAsyncOperation<MultiplayerSession> CreateMultiplayerSessionAsync(MultiplayerSessionSettings settings, PlayerAcl playersCanBeRemovedBy);
        IAsyncOperation<MultiplayerSession> JoinMultiplayerSessionAsync(string sessionId, int seatIndex, object? customUserData);
        IAsyncOperation<MultiplayerSessionSummaryCollection> GetMultiplayerSessionSummariesAsync(uint titleId, Guid titleGroupId, int mode, MultiplayerSessionState includeSessions, MultiplayerMessageQueue includeLastMsgInQueue);
        IAsyncOperation<int> CanPerformActionAsync(string action, ulong targetXuid);
    }

    /// <summary>
    /// Shim for <c>Microsoft.Xbox.User</c>. <c>new User()</c> is the signed-in player; achievements
    /// are theirs (achievements.db, WPR Hub). Another player has a profile with their gamertag and
    /// nothing else, and no friends are listed.
    /// </summary>
    public sealed class User : IUser
    {
        private readonly bool _isMe;

        public User()
        {
            _isMe = true;
            Identity = XboxLIVEService.SignedInUserIdentity;
        }

        public User(ulong xuid, string gamertag)
        {
            _isMe = xuid == XboxHub.PlayerXuid || string.Equals(gamertag, XboxHub.Gamertag, StringComparison.Ordinal);
            Identity = new UserIdentity(xuid, gamertag);
        }

        public UserIdentity Identity { get; }

        public UserStatus Status => new(_isMe);

        public IAsyncOperation<UserProfile> GetProfileAsync() =>
            WinRtAsync.FromResult(new UserProfile(Identity.Gamertag, _isMe ? XboxHub.GamerPictureUrl : string.Empty, _isMe ? Gamerscore() : 0));

        public IAsyncOperation<UserProfile> SetProfileAsync(UserProfile profile) => WinRtAsync.FromResult(profile);

        public IAsyncOperation<UserCollection> GetFriendsAsync() => WinRtAsync.FromResult(new UserCollection(Array.Empty<User>()));

        public IAsyncOperation<AchievementCollection> GetAchievementsAsync(uint skipItems, uint maxItems, bool unlockedOnly, AchievementCollection? previousPage)
        {
            if (!_isMe || XboxHub.Host is not { } host)
            {
                return WinRtAsync.FromResult(new AchievementCollection(Array.Empty<Achievement>(), 0));
            }

            return XboxHub.Run($"GetAchievementsAsync({skipItems}, {maxItems}, unlockedOnly={unlockedOnly})", () =>
            {
                List<XboxAchievement> all = host.GetAchievements().Where(a => !unlockedOnly || a.IsEarned).ToList();
                Achievement[] page = XboxHub.Page(all, skipItems, maxItems).Select(a => new Achievement(a)).ToArray();
                XboxHub.Log($"GetAchievementsAsync -> {page.Length} of {all.Count}");
                return new AchievementCollection(page, (uint)all.Count);
            });
        }

        public IAsyncAction UnlockAchievementAsync(uint achievementId)
        {
            if (!_isMe || XboxHub.Host is not { } host)
            {
                XboxHub.Log($"UnlockAchievementAsync({achievementId}): no Xbox Live host; not recorded");
                return WinRtAsync.Completed();
            }

            XboxHub.Log($"UnlockAchievementAsync({achievementId})");
            return XboxHub.Run($"UnlockAchievementAsync({achievementId})", () => host.UnlockAchievement(achievementId));
        }

        public IAsyncOperation<AvatarManifest> GetAvatarManifestAsync() => XboxHub.Unavailable<AvatarManifest>("GetAvatarManifestAsync");

        public IAsyncAction PostUserMessageAsync(UserMessage message) => XboxHub.Unavailable("PostUserMessageAsync");

        public IAsyncOperation<UserMessageCollection> GetUserMessagesAsync(uint skipItems, uint maxItems, UserMessageCollection? previousPage) =>
            WinRtAsync.FromResult(new UserMessageCollection());

        public IAsyncOperation<UserMessageCollection> GetUserMessagesForTitleAsync(uint titleId, uint skipItems, uint maxItems, UserMessageCollection? previousPage) =>
            WinRtAsync.FromResult(new UserMessageCollection());

        public IAsyncAction DeleteUserMessageAsync(string messageId) => WinRtAsync.Completed();

        public IAsyncOperation<MultiplayerSession> CreateMultiplayerSessionAsync(MultiplayerSessionSettings settings, PlayerAcl playersCanBeRemovedBy) =>
            XboxHub.Unavailable<MultiplayerSession>("CreateMultiplayerSessionAsync");

        public IAsyncOperation<MultiplayerSession> JoinMultiplayerSessionAsync(string sessionId, int seatIndex, object? customUserData) =>
            XboxHub.Unavailable<MultiplayerSession>("JoinMultiplayerSessionAsync");

        public IAsyncOperation<MultiplayerSessionSummaryCollection> GetMultiplayerSessionSummariesAsync(uint titleId, Guid titleGroupId, int mode, MultiplayerSessionState includeSessions, MultiplayerMessageQueue includeLastMsgInQueue) =>
            XboxHub.Unavailable<MultiplayerSessionSummaryCollection>("GetMultiplayerSessionSummariesAsync");

        /// <summary>Every privilege is allowed (UserAction.Allowed), as the native runtime answers.</summary>
        public IAsyncOperation<int> CanPerformActionAsync(string action, ulong targetXuid) =>
            WinRtAsync.FromResult((int)UserAction.Allowed);

        private static int Gamerscore()
        {
            try
            {
                return XboxHub.Host?.GetAchievements().Where(a => a.IsEarned).Sum(a => a.Gamerscore) ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public interface IUserProfile
    {
        string AvatarImageUrl { get; }
        string Bio { get; }
        void put_Bio(string bio);
        string GamerPictureUrl { get; }
        int Gamerscore { get; }
        string Gamertag { get; }
        bool HasAvatar { get; }
        MembershipType MembershipLevel { get; }
        string Location { get; }
        void put_Location(string location);
        string Motto { get; }
        void put_Motto(string mottoText);
        string Name { get; }
        void put_Name(string name);
        double Reputation { get; }
        string SmallGamerPictureUrl { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.UserProfile</c>. Setters are kept locally (WPR Hub has no bio or motto).</summary>
    public sealed class UserProfile : IUserProfile
    {
        internal UserProfile(string gamertag, string pictureUrl, int gamerscore)
        {
            Gamertag = gamertag;
            GamerPictureUrl = pictureUrl;
            SmallGamerPictureUrl = pictureUrl;
            Gamerscore = gamerscore;
            Name = gamertag;
        }

        public string AvatarImageUrl => string.Empty;

        public string Bio { get; private set; } = string.Empty;

        public void put_Bio(string bio) => Bio = bio ?? string.Empty;

        public string GamerPictureUrl { get; }

        public int Gamerscore { get; }

        public string Gamertag { get; }

        public bool HasAvatar => false;

        public MembershipType MembershipLevel => MembershipType.Gold;

        public string Location { get; private set; } = string.Empty;

        public void put_Location(string location) => Location = location ?? string.Empty;

        public string Motto { get; private set; } = string.Empty;

        public void put_Motto(string mottoText) => Motto = mottoText ?? string.Empty;

        public string Name { get; private set; }

        public void put_Name(string name) => Name = name ?? string.Empty;

        public double Reputation => 5.0;

        public string SmallGamerPictureUrl { get; }
    }

    public interface IUserStatus
    {
        bool IsOnline { get; }
        DateTimeOffset LastActivity { get; }
        uint TitleId { get; }
        string RichPresence { get; }
        UserRelation Relation { get; }
        string DeviceType { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.UserStatus</c>.</summary>
    public sealed class UserStatus : IUserStatus
    {
        private readonly bool _isMe;

        internal UserStatus(bool isMe) => _isMe = isMe;

        public bool IsOnline => _isMe;

        public DateTimeOffset LastActivity => DateTimeOffset.Now;

        public uint TitleId => 0;

        public string RichPresence => string.Empty;

        public UserRelation Relation => UserRelation.None;

        public string DeviceType => "WindowsPhone";
    }

    public interface IUserCollection
    {
        IReadOnlyList<User> Items { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.UserCollection</c>.</summary>
    public sealed class UserCollection : IUserCollection
    {
        internal UserCollection(IReadOnlyList<User> items) => Items = items;

        public IReadOnlyList<User> Items { get; }
    }

    public interface IAchievement
    {
        uint Id { get; }
        bool IsEarned { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Achievement</c>, one row of achievements.db.</summary>
    public sealed class Achievement : IAchievement
    {
        private readonly XboxAchievement _row;

        internal Achievement(XboxAchievement row) => _row = row;

        public uint Id => _row.Id;

        public uint TitleId => 0;

        public string Description => _row.Description;

        public bool IsSecret => _row.IsSecret;

        public DateTimeOffset TimeUnlocked => _row.Unlocked is { } when ? new DateTimeOffset(when) : default;

        public bool UnlockedOnline => _row.IsEarned;

        public int Gamerscore => _row.Gamerscore;

        public string LockedDescription => _row.IsSecret ? string.Empty : _row.Description;

        public string Name => _row.Name;

        public uint ImageId => _row.Id;

        public PlatformType Platform => PlatformType.Mobile;

        public uint Sequence => _row.Id;

        public int Flags => 0;

        public uint Type => 0;

        public bool IsEarned => _row.IsEarned;

        public string PictureUrl => string.Empty;
    }

    public interface IAchievementCollection
    {
        IReadOnlyList<Achievement> Items { get; }
        uint TotalRecords { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.AchievementCollection</c>.</summary>
    public sealed class AchievementCollection : IAchievementCollection
    {
        internal AchievementCollection(IReadOnlyList<Achievement> items, uint totalRecords)
        {
            Items = items;
            TotalRecords = totalRecords;
        }

        public IReadOnlyList<Achievement> Items { get; }

        public uint TotalRecords { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.AvatarManifest</c>. Avatars are not provided.</summary>
    public sealed class AvatarManifest
    {
        public string Manifest { get; private set; } = string.Empty;

        public void put_Manifest(string manifest) => Manifest = manifest ?? string.Empty;
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Title</c>.</summary>
    public sealed class Title
    {
        internal Title()
        {
        }

        public uint TitleId => 0;

        public string Name => string.Empty;

        public uint CurrentAchievements => 0;

        public uint CurrentGamerscore => 0;

        public DateTimeOffset LastPlayed => default;

        public uint TotalAchievements => 0;

        public uint TotalGamerscore => 0;

        public uint Sequence => 0;

        public TitleType TitleType => TitleType.Standard;

        public IReadOnlyList<uint> PlatformTypes => Array.Empty<uint>();
    }

    /// <summary>Shim for <c>Microsoft.Xbox.ResourceLoader</c>: Xbox Live's error strings, in English.</summary>
    public static class ResourceLoader
    {
        public static string GetStringById(StringId eStringId) => eStringId switch
        {
            StringId.ErrorAccountCreationFailed => "We couldn't create your Xbox account. Try again later.",
            StringId.ErrorAccountNeedsManagement => "Your Xbox account needs attention. Sign in at xbox.com to fix it.",
            StringId.ErrorProofOfPurchase => "We couldn't verify that you own this game.",
            StringId.ErrorRegionNotSupported => "Xbox isn't available in your region.",
            StringId.ErrorServiceVersionOld => "This game needs an update to use Xbox.",
            StringId.ErrorSignInFailed => "We couldn't sign you in to Xbox. Try again later.",
            StringId.ErrorTermsOfServiceUpdate => "The Xbox terms of use have changed. Sign in at xbox.com to accept them.",
            _ => "Something went wrong with Xbox. Try again later.",
        };
    }

    public interface IRecipient
    {
        string Id { get; }
        RecipientIdType IdType { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Recipient</c>.</summary>
    public sealed class Recipient : IRecipient
    {
        public Recipient(string recipientId, RecipientIdType recipientIdType)
        {
            Id = recipientId ?? string.Empty;
            IdType = recipientIdType;
        }

        public string Id { get; }

        public RecipientIdType IdType { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.RecipientCollection</c>.</summary>
    public sealed class RecipientCollection
    {
        internal RecipientCollection()
        {
        }

        public IReadOnlyList<Recipient> Items => Array.Empty<Recipient>();
    }

    public interface IUserMessage
    {
        string Id { get; }
        string SenderGamertag { get; }
        DateTimeOffset Sent { get; }
        DateTimeOffset Expiration { get; }
        IReadOnlyList<Recipient> Recipients { get; }
        uint TitleId { get; }
        MultiplayerMessageData GetMultiplayerMessageData();
    }

    /// <summary>Shim for <c>Microsoft.Xbox.UserMessage</c>. Messages are never delivered.</summary>
    public sealed class UserMessage : IUserMessage
    {
        private readonly MultiplayerMessageData _data;

        public UserMessage(IReadOnlyList<Recipient> recipients, MultiplayerMessageType messageType, DateTimeOffset expirationDateTime, string sessionId, object? customData)
        {
            Recipients = recipients ?? Array.Empty<Recipient>();
            Expiration = expirationDateTime;
            _data = new MultiplayerMessageData(messageType, sessionId, customData);
        }

        public string Id => string.Empty;

        public string SenderGamertag => XboxHub.Gamertag;

        public DateTimeOffset Sent => DateTimeOffset.Now;

        public DateTimeOffset Expiration { get; }

        public IReadOnlyList<Recipient> Recipients { get; }

        public uint TitleId => 0;

        public MultiplayerMessageData GetMultiplayerMessageData() => _data;
    }

    /// <summary>Shim for <c>Microsoft.Xbox.UserMessageCollection</c>: always empty.</summary>
    public sealed class UserMessageCollection
    {
        internal UserMessageCollection()
        {
        }

        public IReadOnlyList<UserMessage> Items => Array.Empty<UserMessage>();
    }

    public interface IMultiplayerMessageData
    {
        MultiplayerMessageType Type { get; }
        string SessionId { get; }
        object? CustomData { get; }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Xbox.MultiplayerMessageData</c>. CustomData is a WinRT IBuffer on a
    /// phone; it is typed object here because WPR has no IBuffer of its own (the three titles that
    /// read it are multiplayer-only paths, which fail before getting here).
    /// </summary>
    public sealed class MultiplayerMessageData : IMultiplayerMessageData
    {
        internal MultiplayerMessageData(MultiplayerMessageType type, string sessionId, object? customData)
        {
            Type = type;
            SessionId = sessionId ?? string.Empty;
            CustomData = customData;
        }

        public MultiplayerMessageType Type { get; }

        public string SessionId { get; }

        public object? CustomData { get; }
    }
}
