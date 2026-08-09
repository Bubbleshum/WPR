using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.CompositeTransform</c>. Combined
    /// translate/rotate/scale/skew used everywhere by WP page transitions.</summary>
    public class CompositeTransform : Transform
    {
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        public double Rotation { get; set; }
        public double SkewX { get; set; }
        public double SkewY { get; set; }
        public double TranslateX { get; set; }
        public double TranslateY { get; set; }
    }
}
