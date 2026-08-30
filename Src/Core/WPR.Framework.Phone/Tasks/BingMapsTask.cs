using System;

namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for the WP7 <c>BingMapsTask</c>. Real WP7 leaves the game and opens the Maps app at
    /// the requested centre/search. WPR has nowhere to hand off to, so <see cref="Show"/> does
    /// nothing and the game stays where it is — the same treatment
    /// <see cref="WebBrowserTask"/> already gets, and much better than tearing the game out of
    /// focus for a launch that can't complete.
    ///
    /// Crimson Dragon: Side Story holds one in a field, so the type must resolve even though the
    /// map feature is unreachable.
    /// </summary>
    public class BingMapsTask
    {
        /// <summary>
        /// Map centre. Stored, never used — WPR has no Maps app to hand off to.
        /// Typed as the real <see cref="System.Device.Location.GeoCoordinate"/> rather than
        /// <see cref="object"/>: Microsoft.Phone references System.Device on the real platform for
        /// exactly this, so a game that assigns and reads the property back gets its own type out,
        /// and one that stores it in a GeoCoordinate-typed local still compiles.
        /// </summary>
        public System.Device.Location.GeoCoordinate? Center { get; set; }

        /// <summary>Search term the Maps app would run. Stored, never used.</summary>
        public string? SearchTerm { get; set; }

        /// <summary>Zoom level, 1–20 on the real API. Stored, never used.</summary>
        public double ZoomLevel { get; set; }

        public void Show()
        {
        }
    }
}
