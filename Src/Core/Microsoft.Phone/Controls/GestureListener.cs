using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Per-element gesture event source. User XAML wires the events:
    ///   <c>&lt;toolkit:GestureListener Tap="OnTap" Hold="..." Flick="..." DragStarted="..." DragDelta="..." DragCompleted="..."/&gt;</c>.
    /// We invoke them from the FrameView's pointer pipeline.
    /// </summary>
    public class GestureListener : DependencyObject
    {
        // Suppress CS0067 — these events are referenced from XAML at parse time
        // but not all are raised by every code path; that's fine for ABI.
#pragma warning disable CS0067
        public event EventHandler<GestureEventArgs>? Tap;
        public event EventHandler<GestureEventArgs>? DoubleTap;
        public event EventHandler<GestureEventArgs>? Hold;
        public event EventHandler<FlickGestureEventArgs>? Flick;
        public event EventHandler<DragStartedGestureEventArgs>? DragStarted;
        public event EventHandler<DragDeltaGestureEventArgs>? DragDelta;
        public event EventHandler<DragCompletedGestureEventArgs>? DragCompleted;
        public event EventHandler<GestureEventArgs>? GestureBegin;
        public event EventHandler<GestureEventArgs>? GestureCompleted;
        public event EventHandler<MouseEventArgs>? MouseMove;
        public event EventHandler<MouseEventArgs>? MouseEnter;
        public event EventHandler<MouseEventArgs>? MouseLeftButtonDown;
        public event EventHandler<MouseEventArgs>? MouseLeftButtonUp;
        public event EventHandler<MouseEventArgs>? MouseLeave;
#pragma warning restore CS0067

        // CRITICAL: the gesture events fire with `sender` = the element the
        // GestureListener is attached to (the ListBox, StackPanel, etc.), NOT
        // the GestureListener itself. Real WP7 toolkit user code routinely
        // does `(ListBox)sender` inside a Tap handler — passing `this` here
        // would surface an <see cref="InvalidCastException"/> in their first
        // line. The FrameView's tap pipeline supplies the attached element
        // via the <paramref name="attachedTo"/> argument.

        internal void RaiseTap(Point screenPos, UIElement attachedTo, UIElement origin)
            => Tap?.Invoke(attachedTo, new GestureEventArgs(screenPos, origin));

        internal void RaiseFlick(Point screenPos, UIElement attachedTo, UIElement origin, double horizontal, double vertical)
            => Flick?.Invoke(attachedTo, new FlickGestureEventArgs(screenPos, origin, horizontal, vertical));

        internal void RaiseHold(Point screenPos, UIElement attachedTo, UIElement origin)
            => Hold?.Invoke(attachedTo, new GestureEventArgs(screenPos, origin));
    }
}
