using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.GeneralTransform</c>.</summary>
    public abstract class GeneralTransform : DependencyObject
    {
        public virtual Point Transform(Point point) => point;
        public virtual GeneralTransform? Inverse => null;

        /// <summary>The axis-aligned box around <paramref name="rect"/>'s four transformed corners.</summary>
        public virtual Rect TransformBounds(Rect rect)
        {
            Point a = Transform(new Point(rect.X, rect.Y));
            Point b = Transform(new Point(rect.X + rect.Width, rect.Y));
            Point c = Transform(new Point(rect.X, rect.Y + rect.Height));
            Point d = Transform(new Point(rect.X + rect.Width, rect.Y + rect.Height));
            double left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            double top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            double right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            double bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            return new Rect(left, top, right - left, bottom - top);
        }

        /// <summary>Silverlight's TryTransform: always succeeds here.</summary>
        public bool TryTransform(Point inPoint, out Point outPoint)
        {
            outPoint = Transform(inPoint);
            return true;
        }
    }
}
