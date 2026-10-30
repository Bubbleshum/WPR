using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input.Touch;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Raises <see cref="Touch.FrameReported"/> from one frame of <c>TouchPanel</c> state. Called by
    /// the Silverlight host (<c>SilverlightGame</c>) once per update.
    /// </summary>
    /// <remarks>
    /// <para><b>Every finger, whoever draws the page.</b> Unlike the touch router, which feeds the
    /// visual tree only when the game is not reading <c>TouchPanel</c> itself, this is raised
    /// whenever something subscribes: on the phone FrameReported fired for every touch, and the WP
    /// Toolkit's GestureListener (the usual subscriber) needs it on pages a mixed-mode game draws.</para>
    ///
    /// <para><b>Coordinates.</b> <c>TouchPanel</c> positions are page coordinates in the
    /// Silverlight host (see <c>SilverlightGame.PumpSilverlightTouch</c>), so they are used as the
    /// root coordinates directly.</para>
    ///
    /// <para><b>A lift is always reported.</b> A finger that disappears from the state without a
    /// Released sample (the drain can clear the slot on the tick the lift arrives) gets an Up at
    /// its last position: the Toolkit ends a gesture only on a frame whose points are all Up, and a
    /// gesture that never ends leaves its timer and its hit list alive for ever.</para>
    /// </remarks>
    public static class TouchFrameReporter
    {
        private static readonly Dictionary<int, Point> _down = new();
        private static int _primaryId = -1;

        // The newest TouchPanel id seen. A Released sample of a newer id is a whole tap that went
        // down and up between two reports (TouchPanel advances twice in a tick when a page pumps
        // FrameworkDispatcher itself): its Down is reported now and its Up next frame, so the
        // gesture engine sees it as the phone did. An older id never seen down is a finger that
        // was down before anything subscribed, and its lift stays noise.
        private static int _newestId;
        private static readonly Dictionary<int, Point> _upNext = new();
        private static bool _failed;

        /// <summary>True when something listens; the host skips the work otherwise.</summary>
        public static bool HasSubscribers => Touch.HasSubscribers;

        public static void Report(UIElement? root, TouchCollection touches)
        {
            if (!Touch.HasSubscribers)
            {
                _down.Clear();
                _upNext.Clear();
                _primaryId = -1;
                return;
            }

            var points = new List<TouchFrameEventArgs.RawPoint>(touches.Count + _down.Count + _upNext.Count);
            var seen = new HashSet<int>();

            // Lifts owed from last frame's missed taps.
            foreach (KeyValuePair<int, Point> owed in _upNext)
            {
                seen.Add(owed.Key);
                _down.Remove(owed.Key);
                points.Add(new TouchFrameEventArgs.RawPoint(owed.Key, owed.Value, TouchAction.Up, TopElement(root, owed.Value)));
            }
            _upNext.Clear();

            foreach (TouchLocation touch in touches)
            {
                TouchAction action;
                switch (touch.State)
                {
                    case TouchLocationState.Pressed: action = TouchAction.Down; break;
                    case TouchLocationState.Moved: action = _down.ContainsKey(touch.Id) ? TouchAction.Move : TouchAction.Down; break;
                    case TouchLocationState.Released: action = TouchAction.Up; break;
                    default: continue;
                }

                var position = new Point(touch.Position.X, touch.Position.Y);
                bool unseen = touch.Id > _newestId;
                _newestId = Math.Max(_newestId, touch.Id);

                // A Released sample for a finger we never saw go down is noise, not a lift,
                // unless it is a whole tap that fell between two reports (see _newestId).
                if (action == TouchAction.Up && !_down.ContainsKey(touch.Id))
                {
                    if (!unseen || seen.Contains(touch.Id)) continue;
                    action = TouchAction.Down;
                    _upNext[touch.Id] = position;
                }

                seen.Add(touch.Id);
                if (action == TouchAction.Down && _down.Count == 0) _primaryId = touch.Id;
                if (action == TouchAction.Up) _down.Remove(touch.Id);
                else _down[touch.Id] = position;

                points.Add(new TouchFrameEventArgs.RawPoint(touch.Id, position, action, TopElement(root, position)));
            }

            // Fingers that vanished without a Released sample.
            if (_down.Count > 0)
            {
                List<int>? gone = null;
                foreach (KeyValuePair<int, Point> held in _down)
                {
                    if (seen.Contains(held.Key)) continue;
                    points.Add(new TouchFrameEventArgs.RawPoint(held.Key, held.Value, TouchAction.Up, TopElement(root, held.Value)));
                    (gone ??= new List<int>()).Add(held.Key);
                }
                if (gone != null) foreach (int id in gone) _down.Remove(id);
            }

            if (points.Count == 0) return;

            var args = new TouchFrameEventArgs(Environment.TickCount, points, _primaryId);
            if (_down.Count == 0) _primaryId = -1;

            try { Touch.Raise(root, args); }
            catch (Exception ex)
            {
                // A handler that throws is the game's; it must not take the frame down. Said once,
                // because a gesture engine that throws every frame would otherwise flood the log.
                if (!_failed)
                {
                    _failed = true;
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-touchframe] a Touch.FrameReported handler threw (reported once): {ex}");
                }
            }
        }

        private static UIElement? TopElement(UIElement? root, Point position)
        {
            if (root == null) return null;
            try
            {
                IReadOnlyList<UIElement> chain = HitTester.HitTest(root, position.X, position.Y);
                return chain.Count > 0 ? chain[0] : null;
            }
            catch (Exception) { return null; }
        }

        internal static void ResetForNewLaunch()
        {
            _down.Clear();
            _primaryId = -1;
            _failed = false;
            _newestId = 0;
            _upNext.Clear();
        }
    }
}
