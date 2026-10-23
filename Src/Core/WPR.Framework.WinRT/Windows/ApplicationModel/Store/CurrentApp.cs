using global::System;
using global::System.Collections.Generic;
using global::System.Runtime.InteropServices;
using Windows.Foundation;
using WPR.WinRT;

namespace Windows.ApplicationModel.Store
{
    /// <summary>
    /// Shim for <c>Windows.ApplicationModel.Store.CurrentApp</c>. WPR has no store, so it answers
    /// as the WP8 native runtime's <c>CurrentApp</c> does: a purchased copy, and a store that
    /// answered with nothing for sale. Purchases fail as an unreachable store would.
    /// </summary>
    /// <remarks>
    /// WP7.1 titles reach this too: Microsoft's sample for in-app purchase on WP8 had a WP7 app
    /// ship a <c>StoreWrapper.dll</c> compiled against Windows.winmd, loaded only on a WP8 phone.
    /// iRunner calls <see cref="LoadListingInformationAsync"/> from its
    /// <c>applicationDidFinishLaunching</c>, so a missing member stopped its first scene.
    /// </remarks>
    public static class CurrentApp
    {
        /// <summary>
        /// What a purchase answers: <c>0x803F6107</c>, the store's "product not found" error, as
        /// a <see cref="COMException"/>, the projection's exception for a failed HRESULT.
        /// </summary>
        internal const int StoreUnavailable = unchecked((int)0x803F6107);

        private static readonly LicenseInformation _license = new();

        public static Guid AppId => Guid.TryParse(WinRtHost.ProductId, out Guid id) ? id : Guid.Empty;

        public static Uri LinkUri => new("http://windowsphone.com/s?appid=" + WinRtHost.ProductId);

        public static LicenseInformation LicenseInformation => _license;

        public static IAsyncOperation<ListingInformation> LoadListingInformationAsync() =>
            WinRtAsync.FromResult(new ListingInformation());

        public static IAsyncOperation<ListingInformation> LoadListingInformationByProductIdsAsync(IEnumerable<string> productIds) =>
            WinRtAsync.FromResult(new ListingInformation());

        public static IAsyncOperation<ListingInformation> LoadListingInformationByKeywordsAsync(IEnumerable<string> keywords) =>
            WinRtAsync.FromResult(new ListingInformation());

        public static IAsyncOperation<string> RequestAppPurchaseAsync(bool includeReceipt) =>
            WinRtAsync.Failed<string>(Unavailable());

        public static IAsyncOperation<string> RequestProductPurchaseAsync(string productId, bool includeReceipt) =>
            WinRtAsync.Failed<string>(Unavailable());

        public static IAsyncOperation<string> GetAppReceiptAsync() => WinRtAsync.Failed<string>(Unavailable());

        public static IAsyncOperation<string> GetProductReceiptAsync(string productId) => WinRtAsync.Failed<string>(Unavailable());

        /// <summary>Nothing was bought, so there is nothing to fulfil.</summary>
        public static void ReportProductFulfillment(string productId)
        {
        }

        private static Exception Unavailable() => new COMException("The store is not available.", StoreUnavailable);
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Store.LicenseInformation</c>: a purchased copy with no in-app products.</summary>
    public sealed class LicenseInformation
    {
        internal LicenseInformation()
        {
        }

        public IReadOnlyDictionary<string, ProductLicense> ProductLicenses { get; } = new Dictionary<string, ProductLicense>();

        public bool IsActive => true;

        public bool IsTrial => false;

        public DateTimeOffset ExpirationDate => DateTimeOffset.MaxValue;

        /// <summary>Never raised: the licence never changes.</summary>
        public event LicenseChangedEventHandler? LicenseChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Store.LicenseChangedEventHandler</c>.</summary>
    public delegate void LicenseChangedEventHandler();

    /// <summary>Shim for <c>Windows.ApplicationModel.Store.ProductLicense</c>.</summary>
    public sealed class ProductLicense
    {
        internal ProductLicense()
        {
        }

        public string ProductId { get; internal set; } = string.Empty;

        public bool IsActive { get; internal set; }

        public bool IsConsumable { get; internal set; }

        public DateTimeOffset ExpirationDate { get; internal set; }
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Store.ListingInformation</c>: the store answered, with nothing for sale.</summary>
    public sealed class ListingInformation
    {
        internal ListingInformation()
        {
        }

        public string CurrentMarket => string.Empty;

        public string Description => string.Empty;

        public IReadOnlyDictionary<string, ProductListing> ProductListings { get; } = new Dictionary<string, ProductListing>();

        public string FormattedPrice => string.Empty;

        public string Name => string.Empty;

        public uint AgeRating => 0;
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Store.ProductListing</c> (WP8's members included).</summary>
    public sealed class ProductListing
    {
        internal ProductListing()
        {
        }

        public string ProductId { get; internal set; } = string.Empty;

        public string FormattedPrice { get; internal set; } = string.Empty;

        public string Name { get; internal set; } = string.Empty;

        public string Description { get; internal set; } = string.Empty;

        public IEnumerable<string> Keywords { get; internal set; } = Array.Empty<string>();

        public ProductType ProductType { get; internal set; }

        public string Tag { get; internal set; } = string.Empty;

        public Uri? ImageUri { get; internal set; }
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Store.ProductType</c>.</summary>
    public enum ProductType
    {
        Unknown = 0,
        Durable = 1,
        Consumable = 2,
    }
}
