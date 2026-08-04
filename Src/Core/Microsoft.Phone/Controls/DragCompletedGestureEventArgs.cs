using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    public class DragCompletedGestureEventArgs : GestureEventArgs
    {
        public double HorizontalChange { get; }
        public double VerticalChange { get; }
        public Orientation Direction { get; }
        public DragCompletedGestureEventArgs(Point pos, UIElement origin, double hc, double vc, Orientation direction)
            : base(pos, origin)
        {
            HorizontalChange = hc;
            VerticalChange = vc;
            Direction = direction;
        }
    }
}
