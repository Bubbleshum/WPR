using System;
using System.Collections.Generic;

namespace Microsoft.Phone.Shell
{
    public class ShellTile
    {
        private static readonly List<ShellTile> _ActiveTiles;

        static ShellTile()
        {
            // Real WP guarantees the primary app tile is always present in ActiveTiles. User
            // code commonly does `ShellTile.ActiveTiles.First()` to get the primary tile, so we
            // seed one entry to avoid Sequence-Contains-No-Elements crashes.
            _ActiveTiles = new List<ShellTile>
            {
                new ShellTile { NavigationUri = new Uri("/", UriKind.Relative) }
            };
        }

        public static IEnumerable<ShellTile> ActiveTiles => _ActiveTiles;
        public Uri? NavigationUri { get; private set; }

        public static ShellTile? ActiveTile => _ActiveTiles.Count > 0 ? _ActiveTiles[0] : null;

        public void Update(ShellTileData data) { /* no-op — WPR has no live tile */ }

        public static void Create(Uri navigationUri, ShellTileData initialData) { /* no-op */ }
        public static void Create(Uri navigationUri, ShellTileData initialData, bool supportsWideTile) { /* no-op */ }

        public bool Delete() => false;
    }
}
