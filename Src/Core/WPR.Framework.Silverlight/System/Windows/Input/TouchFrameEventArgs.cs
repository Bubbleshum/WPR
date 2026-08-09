using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Stub event args for a touch frame report. Carries an empty <see cref="TouchPointCollection"/>
    /// so user code that iterates touches sees no points (and skips its loop).
    /// </summary>
    public class TouchFrameEventArgs : EventArgs
    {
        public int Timestamp { get; }

        public TouchFrameEventArgs() { }
        public TouchFrameEventArgs(int timestamp) { Timestamp = timestamp; }

        public TouchPointCollection GetTouchPoints(UIElement? relativeTo) => new TouchPointCollection();
        public TouchPoint? GetPrimaryTouchPoint(UIElement? relativeTo) => null;

        public void SuspendMousePromotionUntilTouchUp() { /* no-op */ }
    }
}
