// Every enum Microsoft.Xbox.winmd declares, values as in the winmd.

namespace Microsoft.Xbox
{
    public enum CacheGroup { None = 0, Profile = 1, FriendProfiles = 2, Achievements = 3, Leaderboards = 4, Friends = 5, UserStatus = 6, UserMessages = 7, AvatarManifest = 8, MatchmakingRequestStatus = 9, MultiplayerSessionSummaries = 10, MultiplayerSessionInfo = 11, MultiplayerSessionGameMessages = 12, GetReceipts = 13, GetAssets = 14, GamerContext = 15 }

    public enum FileType { GlobalTitleData = 0, TitleData = 1 }

    public enum FriendType { IsFriend = 0, Pending = 1, Requesting = 2, NotAFriend = 3 }

    public enum LaunchAction { ShowHome = 0, ShowGameDetails = 1, ShowSigninFlow = 2 }

    public enum MembershipType { None = 0, Silver = 3, Gold = 6, FamilyGold = 9 }

    public enum MultiplayerMessageQueue { QueueNone = 0, Queue0 = 1, Queue1 = 2, Queue2 = 4, Queue3 = 8, QueueAll = 65535 }

    public enum MultiplayerMessageType { Unknown = 0, Invitation = 1, YourTurn = 2, Nudge = 3, GameOver = 4, YouWin = 5, YouLose = 6, GameTie = 7 }

    public enum MultiplayerSessionState { Ended = 0, NotEnded = 1, Any = 2 }

    public enum PlatformType { Xbox360 = 1, WindowsPC = 2, Mobile = 15, WebGames = 16, XboxLIVEOnWindows = 17 }

    public enum RecipientIdType { Xuid = 0, Gamertag = 1 }

    public enum StringId { ErrorAccountCreationFailed = 0, ErrorAccountNeedsManagement = 1, ErrorProofOfPurchase = 2, ErrorRegionNotSupported = 3, ErrorServiceVersionOld = 4, ErrorSignInFailed = 5, ErrorTermsOfServiceUpdate = 6, ErrorUnknown = 7 }

    public enum TitleType { System = 0, Standard = 1, Demo = 2, Arcade = 3, Application = 5 }

    public enum UserAction { Blocked = 0, Allowed = 1 }

    public enum UserRelation { None = 0, AnyFriend = 1, MutualFriend = 2, FriendRequester = 3, FriendRequestee = 4 }
}

namespace Microsoft.Xbox.Foundation
{
    public enum ErrorCode { SpaFileError = -2101608447, NotSignedIn = -2101608446, AccountManagementRequired = -2101608445, WindowsLiveSignInFailure = -2101608444, XboxLIVESignInFailure = -2101608443, NoXboxLIVEAccount = -2101608442, UserBanned = -2101608441, ServerConnectionFailure = -2101608440, WindowsLiveUserChanged = -2101608439, AppReceiptError = -2101608438, ServiceDeprecated = -2101608437, NoAppReceipt = -2101608436, XboxLIVENeedsUpdate = -2101608435, TermsOfServiceError = -2101608434, AgeVerificationRequired = -2101608433, BlockedByAge = -2101608432, OutOfGame = -2101608431, ApplicationUpdateRequired = -2101608430, NotAvailableInTrialMode = -2101608429, OperationCancelled = -2147023673 }
}

namespace Microsoft.Xbox.Leaderboards
{
    public enum LeaderboardAggregation { Add = 0, Replace = 1, Min = 2, Max = 3 }

    public enum LeaderboardValueType { Unknown = 0, Int32 = 1, Int64 = 2, Float = 3 }
}

namespace Microsoft.Xbox.Marketplace
{
    public enum ItemType { MetroGame = 1, MetroGameContent = 2, MetroGameConsumable = 4, AvatarItems = 8, MobileGame = 16, XboxMobilePDLC = 32, XboxMobileConsumable = 64 }

    public enum OrderingOption { UserRatings = 0, FreeAndPaidCountDaily = 1, PaidCountAllTime = 2, PaidCountDaily = 3, DigitalReleaseDate = 4, ReleaseDate = 5 }
}

namespace Microsoft.Xbox.Multiplayer
{
    public enum MatchRestriction { PreserveSession = 0, NewSessionOnly = 1, MatchAny = 2 }

    public enum MatchStatus { MatchFound = 0, Searching = 1, MatchNotFound = 2, Cancelled = 3 }

    public enum MatchType { Integer = 0, String = 1 }

    public enum PlayerAcl { All = 1, Self = 2, None = 3 }

    public enum VisibilityLevel { Everyone = 1, PlayersEverInSession = 2, PlayersCurrentlyInSession = 3 }
}
