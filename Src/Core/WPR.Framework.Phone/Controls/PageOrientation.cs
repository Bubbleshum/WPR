using System;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// The current physical orientation of a phone page.
    /// </summary>
    [Flags]
    public enum PageOrientation
    {
        None = 0,
        Portrait = 1,
        Landscape = 2,
        PortraitUp = 5,
        PortraitDown = 9,
        LandscapeLeft = 18,
        LandscapeRight = 34,
    }

    /// <summary>
    /// Which orientations a page is allowed to render in.
    /// </summary>
    [Flags]
    public enum SupportedPageOrientation
    {
        Portrait = 1,
        Landscape = 2,
        PortraitOrLandscape = 3,
    }

    public class OrientationChangedEventArgs : EventArgs
    {
        public OrientationChangedEventArgs() { }
        public OrientationChangedEventArgs(PageOrientation orientation) { Orientation = orientation; }
        public PageOrientation Orientation { get; }
    }
}
