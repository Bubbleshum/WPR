using System;
using System.Collections.Generic;
using Windows.Foundation;

// Marketplace and multiplayer: WPR Hub provides neither. Every type exists with its full surface,
// so a title's code that merely NAMES them still compiles under the JIT. The marketplace READS
// answer an empty marketplace (no offers, receipts or balances: XboxHub.EmptyMarketplace), and
// every other call fails as an unreachable Xbox Live did (XboxHub.Unavailable). WinRT IBuffer members are typed
// object: WPR has no IBuffer of its own, and the three titles that touch them only do so on
// multiplayer paths that fail first.

namespace Microsoft.Xbox.Marketplace
{
    /// <summary>Shim for <c>Microsoft.Xbox.Marketplace.MarketplaceService</c>.</summary>
    public static class MarketplaceService
    {
        public static IAsyncOperation<OfferCollection> EnumerateOffersByOfferIdsAsync(IReadOnlyList<string> offerIds) =>
            XboxHub.EmptyMarketplace("EnumerateOffersByOfferIdsAsync", new OfferCollection());

        public static IAsyncOperation<OfferCollection> EnumerateOffersAsync(uint titleId, uint skipItems, uint maxItems, ItemType desiredItemTypes, uint publisherCategoryMask, OrderingOption order, bool orderAscending) =>
            XboxHub.EmptyMarketplace("EnumerateOffersAsync", new OfferCollection());

        public static IAsyncAction ShowPurchaseAsync(Guid offerId) => XboxHub.Unavailable("ShowPurchaseAsync");

        public static IAsyncOperation<ReceiptCollection> GetReceiptsAsync(uint titleId, uint skipItems, uint maxItems, ReceiptCollection? previousPage) =>
            XboxHub.EmptyMarketplace("GetReceiptsAsync", new ReceiptCollection());

        public static IAsyncOperation<ReceiptCollection> GetReceiptsWithDateFilterAsync(uint titleId, uint skipItems, uint maxItems, DateTimeOffset utcStartDateFilter, ReceiptCollection? previousPage) =>
            XboxHub.EmptyMarketplace("GetReceiptsWithDateFilterAsync", new ReceiptCollection());

        public static IAsyncOperation<Receipt> GetReceiptAsync(uint titleId, Guid offerId) => XboxHub.Unavailable<Receipt>("GetReceiptAsync");

        public static IAsyncOperation<AssetBalanceCollection> GetAssetsAsync(uint titleId, uint skipItems, uint maxItems, AssetBalanceCollection? previousPage) =>
            XboxHub.EmptyMarketplace("GetAssetsAsync", new AssetBalanceCollection());

        public static IAsyncAction ConsumeAssetsAsync(uint titleId, IReadOnlyList<AssetBalance> assets) => XboxHub.Unavailable("ConsumeAssetsAsync");
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Marketplace.AssetBalance</c>.</summary>
    public sealed class AssetBalance
    {
        public AssetBalance()
        {
        }

        public AssetBalance(uint uId, uint uQuanity)
        {
            Id = uId;
            Quantity = uQuanity;
        }

        public uint Id { get; private set; }

        public void put_Id(uint value) => Id = value;

        public uint Quantity { get; private set; }

        public void put_Quantity(uint value) => Quantity = value;

        public ulong Xuid => 0;

        public uint TitleId => 0;

        public DateTimeOffset Timestamp => default;

        public string Json => string.Empty;
    }

    public sealed class AssetBalanceCollection
    {
        internal AssetBalanceCollection()
        {
        }

        public IReadOnlyList<AssetBalance> Items => Array.Empty<AssetBalance>();
    }

    public sealed class Offer
    {
        internal Offer()
        {
        }

        public uint ContentCategory => 0;
        public ulong DownloadSizeInBytes => 0;
        public Guid OfferId => Guid.Empty;
        public string OfferName => string.Empty;
        public ItemType OfferType => ItemType.MobileGame;
        public IReadOnlyList<OfferImage> OfferImages => Array.Empty<OfferImage>();
        public string DisplayPrice => string.Empty;
        public DateTimeOffset ReleaseDate => default;
        public string SellText => string.Empty;
        public uint TitleId => 0;
        public string TitleName => string.Empty;
        public double AverageUserRating => 0;
        public uint UserRatingCount => 0;
    }

    public sealed class OfferCollection
    {
        internal OfferCollection()
        {
        }

        public IReadOnlyList<Offer> Items => Array.Empty<Offer>();
    }

    public sealed class OfferImage
    {
        internal OfferImage()
        {
        }

        public string FileUrl => string.Empty;
        public uint Height => 0;
        public uint Width => 0;
        public string Purpose => string.Empty;
    }

    public sealed class Receipt
    {
        internal Receipt()
        {
        }

        public Guid TransactionId => Guid.Empty;
        public Guid OfferId => Guid.Empty;
        public ulong Xuid => 0;
        public uint PurchasingPartnerId => 0;
        public uint TitleId => 0;
        public string Title => string.Empty;
        public DateTimeOffset PurchaseDate => default;
        public string Status => string.Empty;
        public IReadOnlyList<string> MediaInstanceUrls => Array.Empty<string>();
        public string Json => string.Empty;
    }

    public sealed class ReceiptCollection
    {
        internal ReceiptCollection()
        {
        }

        public IReadOnlyList<Receipt> Items => Array.Empty<Receipt>();
    }
}

namespace Microsoft.Xbox.Multiplayer
{
    public sealed class GameMessage
    {
        internal GameMessage()
        {
        }

        public int QueueIndex => 0;
        public int SequenceId => 0;
        public ulong SenderXuid => 0;
        public DateTimeOffset Timestamp => default;
        public object? MessageData => null;
    }

    public sealed class GameMessageCollection
    {
        internal GameMessageCollection()
        {
        }

        public IReadOnlyList<GameMessage> Items => Array.Empty<GameMessage>();
    }

    public sealed class MatchCriteria
    {
        public MatchRestriction Restriction { get; private set; }
        public void put_Restriction(MatchRestriction value) => Restriction = value;
        public string CustomMatchType { get; private set; } = string.Empty;
        public void put_CustomMatchType(string value) => CustomMatchType = value ?? string.Empty;
        public IList<MatchProperty> Properties { get; } = new List<MatchProperty>();
        public IList<MatchProperty> GreaterThan { get; } = new List<MatchProperty>();
        public IList<MatchProperty> LessThan { get; } = new List<MatchProperty>();
        public IList<MatchProperty> NotEqual { get; } = new List<MatchProperty>();
        public IList<MatchProperty> Equal { get; } = new List<MatchProperty>();
    }

    public sealed class MatchmakingService
    {
        public IAsyncOperation<MatchRequestResult> PostRequestAsync(MatchRequest request) => XboxHub.Unavailable<MatchRequestResult>("PostRequestAsync");
        public IAsyncOperation<MatchRequestStatus> GetRequestStatusAsync(string requestId) => XboxHub.Unavailable<MatchRequestStatus>("GetRequestStatusAsync");
        public IAsyncAction DeleteRequestAsync(string requestId) => XboxHub.Unavailable("DeleteRequestAsync");
    }

    public sealed class MatchProperty
    {
        public MatchProperty()
        {
        }

        public MatchProperty(string propId, string propValue, MatchType propMatchType)
        {
            Id = propId ?? string.Empty;
            Value = propValue ?? string.Empty;
            Type = propMatchType;
        }

        public string Id { get; private set; } = string.Empty;
        public void put_Id(string value) => Id = value ?? string.Empty;
        public string Value { get; private set; } = string.Empty;
        public void put_Value(string value) => Value = value ?? string.Empty;
        public MatchType Type { get; private set; }
        public void put_Type(MatchType value) => Type = value;
    }

    public sealed class MatchRequest
    {
        public string SessionId { get; private set; } = string.Empty;
        public void put_SessionId(string value) => SessionId = value ?? string.Empty;
        public uint TitleId { get; private set; }
        public void put_TitleId(uint value) => TitleId = value;
        public Guid TitleGroupId { get; private set; }
        public void put_TitleGroupId(Guid value) => TitleGroupId = value;
        public uint TimeoutSeconds { get; private set; }
        public void put_TimeoutSeconds(uint value) => TimeoutSeconds = value;
        public string PostbackUrl { get; private set; } = string.Empty;
        public void put_PostbackUrl(string value) => PostbackUrl = value ?? string.Empty;
        public uint SeatsOccupied { get; private set; }
        public void put_SeatsOccupied(uint value) => SeatsOccupied = value;
        public uint MinSeats { get; private set; }
        public void put_MinSeats(uint value) => MinSeats = value;
        public uint MaxSeats { get; private set; }
        public void put_MaxSeats(uint value) => MaxSeats = value;
        public MatchCriteria? MatchCriteria { get; private set; }
        public void put_MatchCriteria(MatchCriteria value) => MatchCriteria = value;
    }

    public sealed class MatchRequestResult
    {
        internal MatchRequestResult()
        {
        }

        public string RequestId => string.Empty;
    }

    public sealed class MatchRequestStatus
    {
        internal MatchRequestStatus()
        {
        }

        public string SessionId => string.Empty;
        public MatchStatus Status => MatchStatus.MatchNotFound;
        public IReadOnlyList<MatchUser> Users => Array.Empty<MatchUser>();
    }

    public sealed class MatchUser
    {
        internal MatchUser()
        {
        }

        public string Gamertag => string.Empty;
        public uint TitleId => 0;
    }

    public sealed class MatchUserCollection
    {
        internal MatchUserCollection()
        {
        }

        public IReadOnlyList<MatchUser> Items => Array.Empty<MatchUser>();
    }

    public sealed class MultiplayerSession
    {
        public MultiplayerSession(string sessionId) => SessionId = sessionId ?? string.Empty;

        public IAsyncOperation<MultiplayerSessionInfo> GetInfoAsync() => XboxHub.Unavailable<MultiplayerSessionInfo>("MultiplayerSession.GetInfoAsync");
        public IAsyncOperation<MultiplayerSessionInfo> UpdateInfoAsync(MultiplayerSessionSettings settings) => XboxHub.Unavailable<MultiplayerSessionInfo>("MultiplayerSession.UpdateInfoAsync");
        public IAsyncAction RemovePlayerAsync(ulong playerXuid) => XboxHub.Unavailable("MultiplayerSession.RemovePlayerAsync");
        public IAsyncOperation<GameMessage> PostGameMessageAsync(int queueIndex, bool replaceAll, int mustFollowSequenceNum, object? messageData) => XboxHub.Unavailable<GameMessage>("MultiplayerSession.PostGameMessageAsync");
        public IAsyncOperation<GameMessageCollection> GetGameMessagesAsync(int queueIndex, int startAtSequenceNum, int maxCount, int timeoutSeconds) => XboxHub.Unavailable<GameMessageCollection>("MultiplayerSession.GetGameMessagesAsync");
        public IAsyncAction DeleteGameMessagesAsync(int queueIndex, int throughSequenceNum) => XboxHub.Unavailable("MultiplayerSession.DeleteGameMessagesAsync");
        public string SessionId { get; }
    }

    public sealed class MultiplayerSessionInfo
    {
        internal MultiplayerSessionInfo()
        {
        }

        public string Id => string.Empty;
        public uint TitleId => 0;
        public DateTimeOffset CreationTime => default;
        public int SeatsAvailable => 0;
        public bool HasEnded => true;
        public IReadOnlyList<Player> Roster => Array.Empty<Player>();
        public object? CustomData => null;
        public string DisplayName => string.Empty;
        public bool IsClosed => true;
        public int MaxPlayers => 0;
        public int Mode => 0;
        public VisibilityLevel Visibility => VisibilityLevel.Everyone;
        public PlayerAcl PlayersCanBeRemovedBy => PlayerAcl.None;
    }

    public interface IMultiplayerSessionSettings
    {
        void put_CustomData(object? value);
        string DisplayName { get; }
        void put_DisplayName(string value);
        bool IsClosed { get; }
        void put_IsClosed(bool value);
        int MaxPlayers { get; }
        void put_MaxPlayers(int value);
        int Mode { get; }
        void put_Mode(int value);
        VisibilityLevel Visibility { get; }
        void put_Visibility(VisibilityLevel value);
        bool HasEnded { get; }
        void put_HasEnded(bool value);
        Guid TitleGroupId { get; }
        void put_TitleGroupId(Guid value);
    }

    public sealed class MultiplayerSessionSettings : IMultiplayerSessionSettings
    {
        private object? _customData;

        public void put_CustomData(object? value) => _customData = value;
        public string DisplayName { get; private set; } = string.Empty;
        public void put_DisplayName(string value) => DisplayName = value ?? string.Empty;
        public bool IsClosed { get; private set; }
        public void put_IsClosed(bool value) => IsClosed = value;
        public int MaxPlayers { get; private set; }
        public void put_MaxPlayers(int value) => MaxPlayers = value;
        public int Mode { get; private set; }
        public void put_Mode(int value) => Mode = value;
        public VisibilityLevel Visibility { get; private set; } = VisibilityLevel.Everyone;
        public void put_Visibility(VisibilityLevel value) => Visibility = value;
        public bool HasEnded { get; private set; }
        public void put_HasEnded(bool value) => HasEnded = value;
        public Guid TitleGroupId { get; private set; }
        public void put_TitleGroupId(Guid value) => TitleGroupId = value;
    }

    public sealed class MultiplayerSessionSummary
    {
        internal MultiplayerSessionSummary()
        {
        }

        public string Id => string.Empty;
        public uint TitleId => 0;
        public bool HasEnded => true;
        public string DisplayName => string.Empty;
        public int Mode => 0;
        public IReadOnlyList<GameMessage> LastMessages => Array.Empty<GameMessage>();
    }

    public sealed class MultiplayerSessionSummaryCollection
    {
        internal MultiplayerSessionSummaryCollection()
        {
        }

        public IReadOnlyList<MultiplayerSessionSummary> Items => Array.Empty<MultiplayerSessionSummary>();
    }

    public sealed class Player
    {
        internal Player()
        {
        }

        public ulong Xuid => 0;
        public string Gamertag => string.Empty;
        public int SeatIndex => 0;
        public bool IsCurrentlyInSession => false;
        public object? CustomData => null;
    }

    public sealed class PlayerCollection
    {
        internal PlayerCollection()
        {
        }

        public IReadOnlyList<Player> Items => Array.Empty<Player>();
    }
}
