using System;
using System.Collections.Generic;
using WPR.SilverlightCompability;
using Point = WPR.SilverlightCompability.Point;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Turns a stream of touch positions into Silverlight input on a page — hit test, routed mouse
    /// events, toolkit gestures and <c>Button.Click</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Host-neutral on purpose.</b> It takes plain coordinates and a page root, so the
    /// mixed-mode XNA host can drive it from <c>TouchPanel</c> exactly as
    /// <see cref="PhoneApplicationFrameView"/> drives the same logic from Avalonia pointer events.
    /// Before this, a mixed-mode title's Silverlight page rendered and could not be touched at all:
    /// the only pointer pipeline WPR had ran on Avalonia, which that host never initialises.</para>
    ///
    /// <para><b>It lives in this assembly rather than beside the rasteriser</b> because dispatching
    /// a tap means knowing about <see cref="GestureService"/> and <see cref="GestureListener"/>,
    /// which are WP toolkit types declared here. <c>WPR.Framework.Silverlight</c> cannot name them
    /// without inverting the reference.</para>
    ///
    /// <para><b>One finger.</b> WP7 menus are single-touch and the recogniser
    /// (<c>PointerInteraction</c>) models one interaction; a second simultaneous finger is ignored
    /// rather than interleaved into the first. Multi-touch belongs to the game's own
    /// <c>TouchPanel</c> reading, which is untouched by any of this.</para>
    /// </remarks>
    public static class SilverlightTouchRouter
    {
        /// <summary>
        /// Ceiling on a flick's speed, in pixels/second. Roughly a screen and a half per second —
        /// faster than any deliberate throw, slow enough that a bad time delta cannot teleport.
        /// </summary>
        private const double MaxFlickVelocity = 4000.0;

        private static PointerInteraction? _interaction;

        /// <summary>The scroll viewer the live interaction is dragging, if any.</summary>
        private static ScrollViewer? _scroller;

        /// <summary>The one coasting after a flick — ticked by the host until it settles.</summary>
        private static ScrollViewer? _coasting;

        /// <summary>Forgets any in-flight interaction. Called at teardown.</summary>
        public static void ResetForNewLaunch()
        {
            _interaction = null;
            _scroller = null;
            _coasting = null;
        }

        /// <summary>
        /// Advances a flick still coasting after the finger left. Returns true while it moves.
        /// </summary>
        /// <remarks>
        /// The host calls this once a frame. It is here rather than inside <see cref="ScrollViewer"/>
        /// because nothing in the Silverlight tree has a clock — the whole point of the mixed-mode
        /// host is that it supplies one.
        /// </remarks>
        public static bool TickInertia(double seconds)
        {
            ScrollViewer? coasting = _coasting;
            if (coasting == null) return false;

            if (!coasting.TickInertia(seconds))
            {
                _coasting = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// The innermost <see cref="ScrollViewer"/> in the hit chain, or null when the drag is not
        /// over one.
        /// </summary>
        /// <remarks>
        /// Innermost wins, which is what nesting means: a list inside a scrollable page scrolls
        /// itself rather than the page.
        /// </remarks>
        private static ScrollViewer? FindScroller(IReadOnlyList<UIElement> chain)
        {
            foreach (UIElement element in chain)
            {
                if (element is ScrollViewer scroller && scroller.ScrollableHeight > 0)
                    return scroller;
            }
            return null;
        }

        /// <summary>
        /// Converts the end of a drag into a coast, when the finger was still moving as it left.
        /// </summary>
        private static void HandOffFlick(PointerInteraction interaction, Point position)
        {
            ScrollViewer? scroller = _scroller;
            _scroller = null;
            if (scroller == null) return;

            // Velocity over the last MOVE, not the whole gesture: a slow drag that ends in a
            // flick should coast, and a fast drag that ends stationary should not.
            double seconds = Math.Max(0.001, (DateTime.UtcNow - interaction.LastMoveTime).TotalSeconds);
            if (seconds > 0.12)
            {
                // The finger rested before lifting — no throw.
                return;
            }

            // CLAMPED. Two samples arriving in the same millisecond — a stutter, a slow frame, a
            // synthesised gesture — divide a real distance by almost no time and yield a velocity
            // of tens of thousands of pixels per second, which lands the list at the far end on the
            // first inertia tick. That reads as the list teleporting rather than coasting, and it
            // is indistinguishable from a broken clamp.
            double velocityX = Clamp(
                (interaction.LastPos.X - position.X) / seconds, -MaxFlickVelocity, MaxFlickVelocity);
            double velocityY = Clamp(
                (interaction.LastPos.Y - position.Y) / seconds, -MaxFlickVelocity, MaxFlickVelocity);

            scroller.BeginFlick(velocityX, velocityY);
            _coasting = scroller.IsCoasting ? scroller : null;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>A finger went down at (<paramref name="x"/>, <paramref name="y"/>) in page coordinates.</summary>
        public static void Press(UIElement? root, double x, double y)
        {
            if (root == null) return;

            var position = new Point(x, y);
            IReadOnlyList<UIElement> chain;
            try { chain = HitTester.HitTest(root, x, y); }
            catch (Exception hex)
            {
                System.Diagnostics.Trace.WriteLine($"[wpr-tap] hit test threw: {hex.Message}");
                return;
            }

            _interaction = new PointerInteraction
            {
                StartPos = position,
                LastPos = position,
                StartTime = DateTime.UtcNow,
                LastMoveTime = DateTime.UtcNow,
                HitChain = chain,
            };

            // A touch takes control of a coasting list immediately, which is what makes a flick
            // feel catchable rather than something you have to wait out.
            _scroller = FindScroller(chain);
            _scroller?.StopFlick();

            // Routed: the innermost hit element first, then outward, stopping at the first handler
            // that marks it handled.
            foreach (UIElement element in chain)
            {
                if (element.RaiseMouseLeftButtonDown(position)) break;
            }
        }

        /// <summary>The finger moved. Promotes the interaction to a drag once it passes the slop.</summary>
        public static void Move(double x, double y)
        {
            PointerInteraction? interaction = _interaction;
            if (interaction == null) return;

            var position = new Point(x, y);
            double totalX = position.X - interaction.StartPos.X;
            double totalY = position.Y - interaction.StartPos.Y;

            if (!interaction.IsDragging &&
                (Math.Abs(totalX) > PointerInteraction.TapSlop ||
                 Math.Abs(totalY) > PointerInteraction.TapSlop))
            {
                interaction.IsDragging = true;
            }

            // The content follows the finger, so the viewport moves the OTHER way — drag down and
            // the offset decreases. Getting this sign wrong produces a list that scrolls away from
            // the finger, which reads as broken rather than inverted.
            if (interaction.IsDragging && _scroller != null)
            {
                _scroller.ScrollBy(
                    interaction.LastPos.X - position.X,
                    interaction.LastPos.Y - position.Y);
            }

            interaction.LastPos = position;
            interaction.LastMoveTime = DateTime.UtcNow;
        }

        /// <summary>
        /// The finger lifted. Delivers the release, and a tap when the interaction never became a
        /// drag.
        /// </summary>
        public static void Release(double x, double y)
        {
            PointerInteraction? interaction = _interaction;
            _interaction = null;
            if (interaction == null) return;

            var position = new Point(x, y);
            IReadOnlyList<UIElement> chain = interaction.HitChain ?? Array.Empty<UIElement>();

            foreach (UIElement element in chain)
            {
                if (element.RaiseMouseLeftButtonUp(position)) break;
            }

            // A drag is not a tap. Panorama/Pivot paging is deliberately NOT handled here: that
            // needs the live drag offset the Avalonia view repaints against, and no mixed-mode
            // title uses a panorama. It belongs with whatever hosts those next.
            if (interaction.IsDragging)
            {
                HandOffFlick(interaction, position);
                return;
            }

            _scroller = null;
            DispatchTap(chain, position);
        }

        /// <summary>
        /// Fires the first thing in the hit chain that wants a tap: a toolkit
        /// <see cref="GestureListener"/>, else a <see cref="Button"/>.
        /// </summary>
        /// <remarks>
        /// Innermost first, and it STOPS at the first taker. Firing every button in the chain would
        /// mean a tap on a button nested inside another activating both.
        /// </remarks>
        private static void DispatchTap(IReadOnlyList<UIElement> chain, Point position)
        {
            foreach (UIElement element in chain)
            {
                GestureListener? listener = GestureService.GetGestureListener(element);
                if (listener != null)
                {
                    try { listener.RaiseTap(position, attachedTo: element, origin: element); }
                    catch (Exception ex)
                    {
                        // Reported, not swallowed: a tap handler that throws is how a menu looks
                        // dead while input is working perfectly, and it is indistinguishable from
                        // a missed tap unless the failure is said out loud.
                        System.Diagnostics.Trace.WriteLine(
                            $"[wpr-tap] GestureListener.Tap on {element.GetType().Name} threw " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }

                // ButtonBase, not Button: a ToggleButton, CheckBox or RadioButton is equally
                // tappable and Silverlight declares Click on their shared base.
                if (element is ButtonBase button)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-tap] Click on {button.GetType().Name} (handlers: {button.HasClickHandler})");
                    try { button.RaiseClick(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[wpr-tap] Button.Click handler threw {ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }
            }
        }
    }
}
