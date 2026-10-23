using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Walks a Silverlight tree to find which elements are at a given point.
    /// Returns the chain leaf-first (topmost on top); callers walk it to find
    /// the closest interactive ancestor (e.g. a Button containing a TextBlock).
    /// </summary>
    internal static class HitTester
    {
        /// <param name="x">X in <paramref name="root"/>'s parent coordinate space.</param>
        /// <param name="y">Y in <paramref name="root"/>'s parent coordinate space.</param>
        public static IReadOnlyList<UIElement> HitTest(UIElement root, double x, double y)
        {
            // Open popups are drawn above the whole tree (see PopupLayer), so they are hit-tested
            // first, TOPMOST first — the reverse of the order they are drawn in, which is the
            // order they opened. If a popup's child claims the hit, the chain is rooted there and
            // the tap never reaches anything behind. Even when nothing inside the popup has a
            // handler the popup still takes the hit and the tap is discarded, which is
            // Silverlight's behaviour: an open splash overlay or dialog must not let taps through.
            // Popups that belong to no tree are included: they are on screen over whatever this
            // tree is, so a tap on one must reach it.
            List<PopupLayer.Entry> popups = PopupLayer.Collect(root, includeDetached: true);
            for (int i = popups.Count - 1; i >= 0; i--)
            {
                Popup pop = popups[i].Popup;
                if (!pop.IsHitTestVisible) continue;
                if (pop.Child is not UIElement popChild) continue;
                var popChain = new List<UIElement>();
                // The child is arranged in the popup's own (0,0)-based space; the layer places
                // that origin, so the descent coordinate is relative to it.
                Recurse(popChild, x - popups[i].Origin.X, y - popups[i].Origin.Y, popChain);
                if (popChain.Count > 0)
                {
                    // Already leaf-first: Recurse adds descendants before their parent.
                    return popChain;
                }
                // Nothing in the popup paints at this point: the tap falls through to whatever
                // is below, as it does in Silverlight.
            }

            var chain = new List<UIElement>();
            Recurse(root, x, y, chain);

            // NO reverse: Recurse adds an element only after its descendants, so the list is
            // already leaf-first — which is the order routed events bubble in and the order
            // callers scan to find the innermost handler. It used to reverse here because the old
            // Recurse added each element on the way DOWN; changing that made this a double flip,
            // and a root-first chain means the frame is consulted before the button inside it.
            return chain;
        }

        private static void Recurse(UIElement el, double x, double y, List<UIElement> chain)
        {
            if (!IsHitTestable(el)) return;

            // Translate to el's local coordinate space.
            double lx = x - el.ArrangedRect.X;
            double ly = y - el.ArrangedRect.Y;

            if (lx < 0 || ly < 0 || lx > el.ArrangedRect.Width || ly > el.ArrangedRect.Height)
                return;

            // CHILDREN FIRST, then self — so an element that paints nothing at this point does not
            // claim a hit that belongs to whatever is behind it.
            //
            // This is Silverlight's own rule, not a simplification: a Panel with a null Background
            // is transparent to input, which is exactly why WP7 XAML is full of
            // Background="Transparent" on layout roots that want taps. Adding every element on the
            // way down — which is what this did — means the topmost element covering the point wins
            // whether or not it draws anything.
            //
            // Carcassonne's main menu is the measured case: a full-page LoadingPopup sits last in
            // the layout root's children, so it is topmost, and with nothing painted it was
            // invisible on screen while swallowing every tap on the menu beneath it.
            int childrenBefore = chain.Count;
            RecurseChildren(el, lx, ly, chain);
            if (chain.Count > childrenBefore)
            {
                chain.Add(el);
                return;
            }

            if (PaintsAt(el)) chain.Add(el);
        }

        /// <summary>
        /// Whether <paramref name="el"/> itself puts anything on screen — and therefore whether it
        /// can take a hit that none of its children wanted.
        /// </summary>
        /// <remarks>
        /// Deliberately generous for leaf content (text, images, shapes, buttons) and strict for
        /// containers, where a null <c>Background</c> is the signal that the element is a layout
        /// device rather than a surface. A <c>SolidColorBrush</c> of <c>Transparent</c> still
        /// counts — that is the whole point of writing it.
        /// </remarks>
        private static bool PaintsAt(UIElement el)
        {
            switch (el)
            {
                case TextBlock text:
                    return !string.IsNullOrEmpty(text.Text);

                case Image image:
                    return image.Source != null;

                case Shape shape:
                    return shape.Fill != null || shape.Stroke != null;

                // A button is chrome in its own right, and a game hooks its Click rather than
                // painting a background to catch the tap. ButtonBase, so toggles, check boxes and
                // radio buttons are equally tappable.
                case ButtonBase:
                    return true;

                case Border border:
                    return border.Background != null || border.BorderBrush != null;

                case FrameworkElement fe:
                    return fe.Background != null;

                default:
                    return false;
            }
        }

        private static void RecurseChildren(UIElement el, double lx, double ly, List<UIElement> chain)
        {
            switch (el)
            {
                case Panel panel:
                    // Later children are visually on top → walk in reverse.
                    for (int i = panel.Children.Count - 1; i >= 0; i--)
                    {
                        int before = chain.Count;
                        Recurse(panel.Children[i], lx, ly, chain);
                        if (chain.Count > before) return; // child claimed the hit
                    }
                    break;

                // A Frame is NOT a ContentControl here — it derives straight from
                // FrameworkElement and exposes the current page as Content — so without its own
                // case the descent stopped at the frame and every hit chain came back one element
                // long. That was invisible while the only host was Avalonia, whose FrameView hit
                // tests the PAGE rather than the frame; the mixed-mode host has nothing above the
                // frame to start from, so it hit exactly this.
                case Frame frame when frame.Content is UIElement framedPage:
                    Recurse(framedPage, lx, ly, chain);
                    break;

                // Border wraps a single child and is common in WP7 chrome; a tap on anything
                // inside one stopped at the border.
                case Border border when border.Child != null:
                    Recurse(border.Child, lx, ly, chain);
                    break;

                // Before the general ContentControl case it derives from. The content is arranged
                // at full extent and shown through a window, so the descent coordinate carries the
                // scroll offset — otherwise a tap on a scrolled list hits whatever was originally
                // arranged at that position, which after any scrolling is the wrong row.
                case ScrollViewer scroller when scroller.Presenter != null:
                    Recurse(
                        scroller.Presenter,
                        lx + scroller.HorizontalOffset,
                        ly + scroller.VerticalOffset,
                        chain);
                    break;

                case ContentControl cc when cc.Presenter != null:
                    // PanoramaItem can be scrolled by the pointer pipeline; the
                    // renderer paints its content with a -ScrollY translation
                    // and a viewport clip. To make taps land on what the user
                    // actually sees, fold the same scroll offset into the
                    // descent coordinate so deeper-down content becomes
                    // reachable at the visible position. Without this, tapping
                    // an item in a scrolled-up list would hit a child arranged
                    // below the viewport — usually nothing.
                    double scrollY = 0;
                    if (cc.GetType().FullName == "Microsoft.Phone.Controls.PanoramaItem")
                    {
                        var st = PanoramaItemScrollTable.TryGet(cc);
                        if (st != null) scrollY = st.ScrollY;
                    }
                    Recurse(cc.Presenter, lx, ly + scrollY, chain);
                    break;
            }
        }

        /// <summary>
        /// Where <paramref name="element"/>'s (0,0) is in the coordinates of the root of its tree,
        /// by the same rules <see cref="HitTest"/> descends with: each ArrangedRect's offset, less a
        /// ScrollViewer's scroll and a PanoramaItem's vertical scroll over the content they show.
        /// </summary>
        /// <remarks>
        /// Shared with <see cref="UIElement.TransformToVisual"/> on purpose: a position the WP
        /// Toolkit maps into an element and the element a hit test found there must agree, and they
        /// only can if one walk decides both. RenderTransforms are not included, which is also
        /// what hit testing does.
        /// </remarks>
        internal static Point OriginInRoot(UIElement element)
        {
            double x = 0, y = 0;
            int guard = 0;
            for (UIElement? el = element; el != null && guard++ < 256; el = el.Parent)
            {
                x += el.ArrangedRect.X;
                y += el.ArrangedRect.Y;

                switch (el.Parent)
                {
                    case ScrollViewer scroller when ReferenceEquals(scroller.Presenter, el):
                        x -= scroller.HorizontalOffset;
                        y -= scroller.VerticalOffset;
                        break;
                    case ContentControl cc when ReferenceEquals(cc.Presenter, el)
                                                && cc.GetType().FullName == "Microsoft.Phone.Controls.PanoramaItem":
                        var st = PanoramaItemScrollTable.TryGet(cc);
                        if (st != null) y -= st.ScrollY;
                        break;
                }
            }
            return new Point(x, y);
        }

        private static bool IsHitTestable(UIElement el)
        {
            if (!el.IsHitTestVisible) return false;
            if (el is FrameworkElement fe && fe.Visibility == Visibility.Collapsed) return false;
            return true;
        }
    }
}
