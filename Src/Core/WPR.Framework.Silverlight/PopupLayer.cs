using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Finds the open <see cref="Popup"/>s that float above a visual tree, where each one goes,
    /// and in what order — the one answer every renderer and the hit tester must agree on.
    /// </summary>
    /// <remarks>
    /// <para><b>Silverlight's rules, which this follows.</b> A popup is not clipped by its parent
    /// and does not take part in its layout: its child is drawn in a layer above the whole page.
    /// A popup declared in the tree is placed at its own position there plus
    /// <see cref="Popup.HorizontalOffset"/>/<see cref="Popup.VerticalOffset"/>; one that is in no
    /// tree at all (built in code and opened — the toolkit's <c>ContextMenu</c> and
    /// <c>CustomMessageBox</c> work that way) is placed relative to the root. Popups stack in the
    /// order they were opened, the last opened on top, regardless of where they are declared.</para>
    ///
    /// <para><b>A popup inside a collapsed subtree is not shown</b>, which is what Silverlight
    /// does: its position comes from layout that never ran.</para>
    ///
    /// <para><b>Positions are LAYOUT coordinates relative to <c>root</c></b>, without render
    /// transforms, matching <see cref="HitTester"/>. The CPU rasteriser prefers the device
    /// position it records while painting the popup's slot, which includes transforms; the two
    /// agree whenever nothing above a popup is transformed, which in WP7 page chrome is nearly
    /// always.</para>
    /// </remarks>
    internal static class PopupLayer
    {
        private const int MaxDepth = 64;

        internal readonly struct Entry
        {
            public Entry(Popup popup, Point origin, bool inTree)
            {
                Popup = popup;
                Origin = origin;
                InTree = inTree;
            }

            public Popup Popup { get; }

            /// <summary>Where the popup's child's (0,0) goes, offsets included, relative to root.</summary>
            public Point Origin { get; }

            /// <summary>Whether the popup was found in <c>root</c>'s tree, as opposed to the registry.</summary>
            public bool InTree { get; }
        }

        /// <summary>
        /// Every open popup over <paramref name="root"/>, bottom first.
        /// </summary>
        /// <param name="includeDetached">
        /// Whether popups that belong to no tree are included. They float above the whole
        /// application, so only a caller drawing the application's root should ask for them.
        /// </param>
        public static List<Entry> Collect(UIElement? root, bool includeDetached)
        {
            var result = new List<Entry>();
            if (root == null) return result;

            List<Popup> open = Popup.OpenInOrder();
            if (open.Count == 0) return result;

            // Where each open popup sits in this tree, if it is in it at all.
            var found = new Dictionary<Popup, Point>(ReferenceEqualityComparer.Instance);
            Walk(root, 0, 0, found, 0);

            Rect rootRect = root.ArrangedRect;
            var available = new Size(
                rootRect.Width > 0 ? rootRect.Width : 480,
                rootRect.Height > 0 ? rootRect.Height : 800);

            foreach (Popup popup in open)
            {
                if (popup.Child is not UIElement) continue;

                if (found.TryGetValue(popup, out Point at))
                {
                    popup.EnsureChildLaidOut(available);
                    result.Add(new Entry(
                        popup,
                        new Point(at.X + popup.HorizontalOffset, at.Y + popup.VerticalOffset),
                        inTree: true));
                }
                else if (includeDetached && IsDetached(popup))
                {
                    popup.EnsureChildLaidOut(available);
                    result.Add(new Entry(
                        popup,
                        new Point(popup.HorizontalOffset, popup.VerticalOffset),
                        inTree: false));
                }
            }

            // Popups nested inside other popups' children are positioned relative to them.
            // OpenInOrder is already open order, which is the stacking order.
            return result;
        }

        /// <summary>
        /// A popup that is in no tree: no parent, and visually its own root. One whose parent is
        /// set but which was not reached from the root belongs to some other tree (a page that
        /// was navigated away from) or to a collapsed subtree, and is not shown.
        /// </summary>
        private static bool IsDetached(Popup popup) => popup.Parent == null;

        private static void Walk(UIElement el, double x, double y, Dictionary<Popup, Point> found, int depth)
        {
            if (depth > MaxDepth) return;
            if (el is FrameworkElement fe && fe.Visibility == Visibility.Collapsed) return;

            // ArrangedRect is parent-relative, the root's included — the same model HitTester
            // uses, whose incoming coordinate is in the root's parent space.
            Rect r = el.ArrangedRect;
            double ox = x + r.X;
            double oy = y + r.Y;

            switch (el)
            {
                case Popup popup:
                    if (popup.IsEffectivelyOpen && !found.ContainsKey(popup))
                        found[popup] = new Point(ox, oy);
                    // A popup's child is in the popup layer, positioned from the popup, so its
                    // own popups are found relative to that.
                    if (popup.IsEffectivelyOpen && popup.Child != null)
                        Walk(popup.Child, ox + popup.HorizontalOffset, oy + popup.VerticalOffset, found, depth + 1);
                    break;

                case Panel panel:
                    foreach (UIElement c in panel.Children) Walk(c, ox, oy, found, depth + 1);
                    break;

                case Border border:
                    if (border.Child != null) Walk(border.Child, ox, oy, found, depth + 1);
                    break;

                case Frame frame:
                    if (frame.Content is UIElement framed) Walk(framed, ox, oy, found, depth + 1);
                    break;

                case ScrollViewer scroller:
                    if (scroller.Presenter != null)
                        Walk(scroller.Presenter, ox - scroller.HorizontalOffset, oy - scroller.VerticalOffset, found, depth + 1);
                    break;

                case ContentControl cc:
                    if (cc.Presenter != null)
                        Walk(cc.Presenter, ox, oy, found, depth + 1);
                    else if (cc.Content is UIElement content)
                        Walk(content, ox, oy, found, depth + 1);
                    break;
            }
        }
    }
}
