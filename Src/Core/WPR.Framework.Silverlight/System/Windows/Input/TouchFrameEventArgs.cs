using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.TouchFrameEventArgs</c>.</summary>
    /// <remarks>
    /// Built by <see cref="TouchFrameReporter"/> from one frame of <c>TouchPanel</c> state. Points are
    /// held in root (host) coordinates and moved into an element's space on request, through the
    /// same layout walk hit testing uses, so a point and the element found under it agree.
    /// </remarks>
    public class TouchFrameEventArgs : EventArgs
    {
        internal readonly struct RawPoint
        {
            public RawPoint(int id, Point position, TouchAction action, UIElement? directlyOver)
            {
                Id = id;
                Position = position;
                Action = action;
                DirectlyOver = directlyOver;
            }

            public int Id { get; }
            public Point Position { get; }
            public TouchAction Action { get; }
            public UIElement? DirectlyOver { get; }
        }

        private readonly IReadOnlyList<RawPoint> _points = Array.Empty<RawPoint>();
        private readonly int _primaryId = -1;

        public int Timestamp { get; }

        public TouchFrameEventArgs() { }
        public TouchFrameEventArgs(int timestamp) { Timestamp = timestamp; }

        internal TouchFrameEventArgs(int timestamp, IReadOnlyList<RawPoint> points, int primaryId)
        {
            Timestamp = timestamp;
            _points = points;
            _primaryId = primaryId;
        }

        public TouchPointCollection GetTouchPoints(UIElement? relativeTo)
        {
            var collection = new TouchPointCollection();
            Point origin = Origin(relativeTo);
            foreach (RawPoint raw in _points) collection.Add(ToTouchPoint(raw, origin));
            return collection;
        }

        public TouchPoint? GetPrimaryTouchPoint(UIElement? relativeTo)
        {
            foreach (RawPoint raw in _points)
            {
                if (raw.Id == _primaryId) return ToTouchPoint(raw, Origin(relativeTo));
            }
            return null;
        }

        /// <summary>No mouse promotion exists to suspend: WPR delivers mouse events from the same samples.</summary>
        public void SuspendMousePromotionUntilTouchUp() { }

        private static Point Origin(UIElement? relativeTo) =>
            relativeTo == null ? new Point(0, 0) : HitTester.OriginInRoot(relativeTo);

        private static TouchPoint ToTouchPoint(RawPoint raw, Point origin) => new TouchPoint
        {
            Action = raw.Action,
            Position = new Point(raw.Position.X - origin.X, raw.Position.Y - origin.Y),
            TouchDevice = new TouchDevice { Id = raw.Id, DirectlyOver = raw.DirectlyOver },
        };
    }
}
