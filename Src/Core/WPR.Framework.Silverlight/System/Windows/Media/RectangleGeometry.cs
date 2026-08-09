using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.RectangleGeometry</c>.</summary>
    public class RectangleGeometry : Geometry
    {
        public Rect Rect { get; set; }
        public override Rect Bounds => Rect;
    }
}
