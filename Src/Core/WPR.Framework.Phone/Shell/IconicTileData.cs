using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Stub for <c>Microsoft.Phone.Shell.IconicTileData</c>. WP8 iconic-template tile data:
    /// small/medium icon URIs, a wide-tile content list, count, and background colour. WPR
    /// has no live tile, so the data is only stored — apps that branch on the type's
    /// presence (e.g. <c>if (...) new IconicTileData { ... }</c>) will succeed.
    /// </summary>
    public class IconicTileData : ShellTileData
    {
        public Uri? IconImage { get; set; }
        public Uri? SmallIconImage { get; set; }

        public int Count { get; set; }

        public string? WideContent1 { get; set; }
        public string? WideContent2 { get; set; }
        public string? WideContent3 { get; set; }

        // Real WP exposes this as System.Windows.Media.Color; we type as object so the property
        // exists for XAML/code-behind without dragging the full Silverlight Color into this assembly.
        public object? BackgroundColor { get; set; }
    }
}
