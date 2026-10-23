using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.VisualTreeHelper</c>. The few
    /// methods user code touches are static enumerators we satisfy with
    /// no-children/null returns.</summary>
    public static class VisualTreeHelper
    {
        // The same walk layout and Loaded use, so a Frame, Border or Popup has its child here too.
        public static int GetChildrenCount(DependencyObject reference) =>
            reference is UIElement el ? System.Linq.Enumerable.Count(FrameworkElement.EnumerateLogicalChildren(el)) : 0;

        public static DependencyObject? GetChild(DependencyObject reference, int childIndex) =>
            reference is UIElement el && childIndex >= 0
                ? System.Linq.Enumerable.ElementAtOrDefault(FrameworkElement.EnumerateLogicalChildren(el), childIndex)
                : null;

        public static DependencyObject? GetParent(DependencyObject reference)
            => (reference as UIElement)?.Parent;

        /// <summary>
        /// The elements of <paramref name="subtree"/> under a point in host (root) coordinates,
        /// topmost first, as Silverlight returns them.
        /// </summary>
        /// <remarks>
        /// The WP Toolkit's GestureListener finds the elements a gesture applies to with exactly
        /// this call. It returned nothing until patcher version 47, which is one of the reasons the
        /// Toolkit's own gesture engine could never raise an event here. It is the same walk the
        /// touch router hit-tests with, so the two agree on what was touched.
        /// </remarks>
        public static IEnumerable<UIElement> FindElementsInHostCoordinates(Point intersectingPoint, UIElement subtree)
        {
            if (subtree == null) return Array.Empty<UIElement>();
            // HitTest takes the point in the subtree's PARENT's space.
            Point parentOrigin = subtree.Parent == null ? new Point(0, 0) : HitTester.OriginInRoot(subtree.Parent);
            return HitTester.HitTest(subtree, intersectingPoint.X - parentOrigin.X, intersectingPoint.Y - parentOrigin.Y);
        }

        public static IEnumerable<UIElement> FindElementsInHostCoordinates(Rect intersectingRect, UIElement subtree)
            => Array.Empty<UIElement>();
    }
}
