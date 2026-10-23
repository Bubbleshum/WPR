using System;
using System.Collections.Generic;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Shell.ShellTile</c>: the app's tiles on the Start screen.
    /// </summary>
    /// <remarks>
    /// WPR draws no Start screen, so tile content (<see cref="Update"/>) is not shown, but the set
    /// of tiles behaves as on the phone: the primary tile is always first in
    /// <see cref="ActiveTiles"/> (apps do <c>ActiveTiles.First()</c>), <see cref="Create(Uri, ShellTileData)"/>
    /// adds a secondary tile and refuses a second one for the same URI, and <see cref="Delete"/>
    /// removes a secondary tile and refuses the primary one. Both void, as on the phone; Delete
    /// used to return bool and was a MissingMethodException (YouTube's <c>ClearDownloadData</c>).
    /// Tiles last for the run.
    /// </remarks>
    public class ShellTile
    {
        private static readonly List<ShellTile> _ActiveTiles;

        static ShellTile()
        {
            _ActiveTiles = new List<ShellTile>
            {
                new ShellTile { NavigationUri = new Uri("/", UriKind.Relative), IsPrimary = true }
            };
        }

        public static IEnumerable<ShellTile> ActiveTiles
        {
            get { lock (_ActiveTiles) return _ActiveTiles.ToArray(); }
        }

        public Uri? NavigationUri { get; private set; }

        private bool IsPrimary { get; set; }

        public static ShellTile? ActiveTile => _ActiveTiles.Count > 0 ? _ActiveTiles[0] : null;

        /// <summary>Stored only: there is no Start screen to show the tile on.</summary>
        public void Update(ShellTileData data) { }

        public static void Create(Uri navigationUri, ShellTileData initialData) => Create(navigationUri, initialData, false);

        public static void Create(Uri navigationUri, ShellTileData initialData, bool supportsWideTile)
        {
            if (navigationUri == null) throw new ArgumentNullException(nameof(navigationUri));
            lock (_ActiveTiles)
            {
                foreach (ShellTile tile in _ActiveTiles)
                {
                    if (!tile.IsPrimary && Equals(tile.NavigationUri, navigationUri))
                        throw new InvalidOperationException("Tiles can only be created once for each NavigationUri.");
                }
                _ActiveTiles.Add(new ShellTile { NavigationUri = navigationUri });
            }
        }

        public void Delete()
        {
            if (IsPrimary) throw new InvalidOperationException("The application tile cannot be deleted.");
            lock (_ActiveTiles) _ActiveTiles.Remove(this);
        }
    }
}
