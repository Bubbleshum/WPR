using System.Windows;
using System.Windows.Media;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// The phone's host coordinates, which on a landscape page are not the page's.
    /// </summary>
    /// <remarks>
    /// On Windows Phone the frame never rotates: a landscape page is the frame's content turned a
    /// quarter, and everything Silverlight reports "in host coordinates" is in the device's portrait
    /// frame (480x800). That is <c>TouchFrameEventArgs.GetTouchPoints(null)</c>,
    /// <c>TransformToVisual(null)</c> and <c>VisualTreeHelper.FindElementsInHostCoordinates</c>.
    /// WPR lays a landscape page out as 800x480 and used to answer all three in that frame.
    /// Asphalt 7: Heat hands <c>GetTouchPoints(null)</c> to its engine, which turns the point
    /// itself, so every tap landed somewhere else and its menus took none. The WP Toolkit only
    /// ever combines the three, so it is unaffected as long as they agree.
    ///
    /// The turn is the one the host already uses for a background grid's pointers
    /// (<c>SilverlightGame.PumpManipulationInput</c>): LandscapeRight (and plain Landscape) is a
    /// quarter turn clockwise, page (x, y) = portrait (y, pageWidth - x); LandscapeLeft is
    /// anticlockwise, portrait (pageHeight - y, x). Set by the host on every navigation.
    /// </remarks>
    public static class HostFrame
    {
        private static Matrix _pageToHost = Matrix.Identity;
        private static Matrix _hostToPage = Matrix.Identity;

        /// <summary>Whether the page is landscape, i.e. host and page coordinates differ.</summary>
        public static bool Turned { get; private set; }

        /// <summary>Called by the host with the current page's orientation and its landscape size in DIPs.</summary>
        public static void Set(bool landscape, bool landscapeLeft, double pageWidth, double pageHeight)
        {
            Turned = landscape;
            _pageToHost = !landscape
                ? Matrix.Identity
                : landscapeLeft
                    ? new Matrix(0, 1, -1, 0, pageHeight, 0)
                    : new Matrix(0, -1, 1, 0, 0, pageWidth);
            _hostToPage = _pageToHost.TryInvert() ?? Matrix.Identity;
        }

        public static void Reset() => Set(false, false, 0, 0);

        /// <summary>The page-to-host turn, as a matrix to append to a page-space transform.</summary>
        public static Matrix PageToHost => _pageToHost;

        public static Point ToHost(Point page) => Turned ? _pageToHost.Transform(page) : page;

        /// <summary>
        /// An element's own coordinates to host coordinates. The root of the tree (the phone's
        /// frame, which never rotates) is the host frame itself once the page is turned; everything
        /// below it is laid out in the page and turned with it.
        /// </summary>
        internal static Matrix ElementToHost(UIElement element)
        {
            if (Turned && element.Parent == null)
            {
                return Matrix.Identity;
            }

            Point origin = HitTester.OriginInRoot(element);
            if (!Turned)
            {
                return new Matrix(1, 0, 0, 1, origin.X, origin.Y);
            }

            Point offset = _pageToHost.Transform(origin);
            return new Matrix(_pageToHost.M11, _pageToHost.M12, _pageToHost.M21, _pageToHost.M22, offset.X, offset.Y);
        }

        /// <summary><paramref name="first"/> then <paramref name="then"/> (row-vector convention).</summary>
        internal static Matrix Multiply(Matrix first, Matrix then) => new(
            first.M11 * then.M11 + first.M12 * then.M21,
            first.M11 * then.M12 + first.M12 * then.M22,
            first.M21 * then.M11 + first.M22 * then.M21,
            first.M21 * then.M12 + first.M22 * then.M22,
            first.OffsetX * then.M11 + first.OffsetY * then.M21 + then.OffsetX,
            first.OffsetX * then.M12 + first.OffsetY * then.M22 + then.OffsetY);

        public static Point ToPage(Point host) => Turned ? _hostToPage.Transform(host) : host;
    }
}
