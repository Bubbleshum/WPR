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
            // Open popups are rendered above the visual tree at page-level bounds
            // (see SilverlightRenderer.RenderPage). Hit-test them first — if a
            // popup's child claims the hit, the chain is rooted there and the
            // tap never reaches elements behind. Even when nothing inside the
            // popup has a GestureListener, the popup blocks the tap: we return
            // the popup chain and the FrameView's "no handler in chain" branch
            // silently discards the tap. That matches Silverlight semantics —
            // an open splash overlay must not let clicks fall through.
            foreach (Popup pop in CollectOpenPopups(root))
            {
                if (!pop.IsHitTestVisible) continue;
                if (pop.Child is not UIElement popChild) continue;
                var popChain = new List<UIElement>();
                // Popup's Child is arranged in the popup's own (0,0)-based space,
                // and rendered at page-level bounds — so the incoming page-space
                // (x,y) is exactly the coordinate to walk into the child.
                Recurse(popChild, x, y, popChain);
                if (popChain.Count > 0)
                {
                    popChain.Reverse();
                    return popChain;
                }
                // The popup's Child covers the full page (480×800 logical), so
                // if Recurse returned no chain it means the Child's ArrangedRect
                // is degenerate — fall through to the page hit-test. (If the
                // popup ever sized down to less-than-full-page we'd need to
                // still block the tap *only* over the popup's visible area;
                // for the splash-overlay case Child always fills the screen.)
            }

            var chain = new List<UIElement>();
            Recurse(root, x, y, chain);
            chain.Reverse(); // leaf-first
            return chain;
        }

        private static IEnumerable<Popup> CollectOpenPopups(UIElement root)
        {
            // Same BFS shape as SilverlightRenderer.CollectOpenPopups so the
            // two passes agree about which popups are "live". Use
            // IsEffectivelyOpen so the minimum-display-time floor applies to
            // hit-testing too — while the splash is visually held, taps under
            // it must still be blocked.
            var q = new Queue<UIElement>();
            q.Enqueue(root);
            while (q.Count > 0)
            {
                UIElement el = q.Dequeue();
                if (el is Popup p && p.IsEffectivelyOpen) yield return p;
                switch (el)
                {
                    case Panel panel:
                        foreach (UIElement c in panel.Children) q.Enqueue(c);
                        break;
                    case ContentControl cc:
                        if (cc.Content is UIElement ccChild) q.Enqueue(ccChild);
                        if (cc.Presenter != null && !object.ReferenceEquals(cc.Presenter, cc.Content))
                            q.Enqueue(cc.Presenter);
                        break;
                    case Border b:
                        if (b.Child != null) q.Enqueue(b.Child);
                        break;
                    case Popup pop:
                        if (pop.Child != null) q.Enqueue(pop.Child);
                        break;
                }
            }
        }

        private static void Recurse(UIElement el, double x, double y, List<UIElement> chain)
        {
            if (!IsHitTestable(el)) return;

            // Translate to el's local coordinate space.
            double lx = x - el.ArrangedRect.X;
            double ly = y - el.ArrangedRect.Y;

            if (lx < 0 || ly < 0 || lx > el.ArrangedRect.Width || ly > el.ArrangedRect.Height)
                return;

            chain.Add(el);

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

        private static bool IsHitTestable(UIElement el)
        {
            if (!el.IsHitTestVisible) return false;
            if (el is FrameworkElement fe && fe.Visibility == Visibility.Collapsed) return false;
            return true;
        }
    }
}
