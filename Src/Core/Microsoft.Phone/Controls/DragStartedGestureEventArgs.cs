using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    public class DragStartedGestureEventArgs : GestureEventArgs
    {
        public Orientation Direction { get; }
        public DragStartedGestureEventArgs(Point pos, UIElement origin, Orientation direction)
            : base(pos, origin) { Direction = direction; }
    }
}
