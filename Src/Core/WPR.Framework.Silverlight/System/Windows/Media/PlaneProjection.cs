using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.PlaneProjection</c>. 3-D rotation
    /// used by WP page transitions (turnstile, etc.).</summary>
    public class PlaneProjection : Projection
    {
        public double CenterOfRotationX { get; set; } = 0.5;
        public double CenterOfRotationY { get; set; } = 0.5;
        public double CenterOfRotationZ { get; set; }
        public double GlobalOffsetX { get; set; }
        public double GlobalOffsetY { get; set; }
        public double GlobalOffsetZ { get; set; }
        public double LocalOffsetX { get; set; }
        public double LocalOffsetY { get; set; }
        public double LocalOffsetZ { get; set; }
        public double RotationX { get; set; }
        public double RotationY { get; set; }
        public double RotationZ { get; set; }
    }
}
