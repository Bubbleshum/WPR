using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.MouseEventArgs</c>.</summary>
    /// <remarks>
    /// The base of <see cref="MouseButtonEventArgs"/>, as in Silverlight. That matters for IL, not
    /// just tidiness: <c>GetPosition</c> is declared here, so a handler for
    /// <c>MouseLeftButtonDown</c> that reads the position compiles to
    /// <c>callvirt MouseEventArgs::GetPosition</c> on a <c>MouseButtonEventArgs</c> — Flowerz's
    /// game board does exactly that to find the tapped cell.
    /// </remarks>
    public class MouseEventArgs : RoutedEventArgs
    {
        /// <summary>Where the pointer was, in page coordinates.</summary>
        internal Point PagePosition { get; set; }

        /// <summary>
        /// The position relative to <paramref name="relativeTo"/>, or to the page when that is null.
        /// </summary>
        /// <remarks>
        /// This used to return <c>default</c> — (0,0) — for every call, which is indistinguishable
        /// from a press in the top-left corner and is what a game hit-testing its own layout would
        /// act on. Now that input is actually delivered, a wrong answer is worse than none.
        /// <para>The offset is accumulated from <see cref="UIElement.ArrangedRect"/> up the parent
        /// chain, which is the same model the renderers use to place an element.</para>
        /// </remarks>
        public Point GetPosition(UIElement? relativeTo)
        {
            if (relativeTo == null) return PagePosition;

            double offsetX = 0;
            double offsetY = 0;
            int guard = 0;
            for (UIElement? el = relativeTo; el != null && guard++ < 64; el = el.Parent)
            {
                Rect r = el.ArrangedRect;
                offsetX += r.X;
                offsetY += r.Y;
            }

            return new Point(PagePosition.X - offsetX, PagePosition.Y - offsetY);
        }
    }
}
