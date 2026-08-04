using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>Shim for FlickGestureEventArgs — supplies HorizontalVelocity / VerticalVelocity.</summary>
    public class FlickGestureEventArgs : GestureEventArgs
    {
        public double HorizontalVelocity { get; }
        public double VerticalVelocity { get; }
        /// <summary>Best-effort direction (Horizontal when |HV|>|VV|).</summary>
        public Orientation Direction =>
            Math.Abs(HorizontalVelocity) >= Math.Abs(VerticalVelocity)
                ? Orientation.Horizontal
                : Orientation.Vertical;

        public FlickGestureEventArgs(Point pos, UIElement origin, double hv, double vv)
            : base(pos, origin)
        {
            HorizontalVelocity = hv;
            VerticalVelocity = vv;
        }
    }
}
