using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Stub for <c>Microsoft.Phone.Shell.FlipTileData</c>. WP8 flip-template tile (the default):
    /// front and back faces with title/content/image. WPR has no live tile, so the data is
    /// just held.
    /// </summary>
    public class FlipTileData : ShellTileData
    {
        public Uri? BackgroundImage { get; set; }
        public Uri? BackBackgroundImage { get; set; }

        public Uri? SmallBackgroundImage { get; set; }
        public Uri? WideBackgroundImage { get; set; }
        public Uri? WideBackBackgroundImage { get; set; }

        public string? BackTitle { get; set; }
        public string? BackContent { get; set; }
        public string? WideBackContent { get; set; }

        public int Count { get; set; }
    }
}
