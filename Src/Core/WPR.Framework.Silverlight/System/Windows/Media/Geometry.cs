using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Geometry</c>.</summary>
    public abstract class Geometry : DependencyObject
    {
        public virtual Rect Bounds => default;
    }
}
