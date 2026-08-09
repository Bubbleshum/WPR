using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Stub for <c>Microsoft.Phone.Shell.CycleTileData</c>. WP8 cycle-template tile that rotates
    /// through up to nine images. WPR has no live tile, so the data is just held.
    /// </summary>
    public class CycleTileData : ShellTileData
    {
        public Uri? SmallBackgroundImage { get; set; }
        public int Count { get; set; }
        public Uri[]? CycleImages { get; set; }
    }
}
