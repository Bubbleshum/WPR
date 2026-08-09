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
        public static int GetChildrenCount(DependencyObject reference)
        {
            if (reference is Panel p) return p.Children.Count;
            if (reference is ContentControl cc) return cc.Content is UIElement ? 1 : 0;
            return 0;
        }

        public static DependencyObject? GetChild(DependencyObject reference, int childIndex)
        {
            if (reference is Panel p) return childIndex >= 0 && childIndex < p.Children.Count ? p.Children[childIndex] : null;
            if (reference is ContentControl cc && childIndex == 0) return cc.Content as DependencyObject;
            return null;
        }

        public static DependencyObject? GetParent(DependencyObject reference)
            => (reference as UIElement)?.Parent;

        public static IEnumerable<UIElement> FindElementsInHostCoordinates(Point intersectingPoint, UIElement subtree)
            => Array.Empty<UIElement>();

        public static IEnumerable<UIElement> FindElementsInHostCoordinates(Rect intersectingRect, UIElement subtree)
            => Array.Empty<UIElement>();
    }
}
