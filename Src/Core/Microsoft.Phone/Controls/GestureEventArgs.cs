using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>Shim for the WP toolkit gesture event-args base.</summary>
    public class GestureEventArgs : EventArgs
    {
        public Point Position { get; }
        public UIElement OriginalSource { get; }
        public bool Handled { get; set; }
        public GestureEventArgs(Point position, UIElement originalSource)
        {
            Position = position;
            OriginalSource = originalSource;
        }
        public Point GetPosition(UIElement? relativeTo) => Position;
    }
}
