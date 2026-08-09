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
    }
}
