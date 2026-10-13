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
    /// <para><b>Host-neutral on purpose.</b> It takes plain coordinates and a page root, and the
    /// Silverlight host (<c>SilverlightGame</c>) drives it from <c>TouchPanel</c>. It is the only
    /// Silverlight pointer pipeline since the Avalonia host (and its
    /// <c>PhoneApplicationFrameView</c>) was deleted on 2026-10-13.</para>
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

        // The button drawn in its pressed state while the finger is down on it; cleared on release
        // or as soon as the interaction becomes a drag, which is when WP7 un-presses it too.
        private static ButtonBase? _pressed;

        /// <summary>The Panorama or Pivot under the live interaction, if any (see Move/Release).</summary>
        private static UIElement? _panorama;

        /// <summary>
        /// The innermost Panorama or Pivot in the hit chain. Matched by type name because both
        /// come from the game's own Microsoft.Phone.Controls.dll, not from a WPR assembly.
        /// </summary>
        private static UIElement? FindPanorama(IReadOnlyList<UIElement> chain)
        {
            foreach (UIElement element in chain)
            {
                string? name = element.GetType().FullName;
                if (name == "Microsoft.Phone.Controls.Panorama" || name == "Microsoft.Phone.Controls.Pivot")
                    return element;
            }
            return null;
        }

        /// <summary>Forgets any in-flight interaction. Called at teardown.</summary>
        public static void ResetForNewLaunch()
        {
            _interaction = null;
            _panorama = null;
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
            _panorama = FindPanorama(chain);
            _scroller = FindScroller(chain);
            _scroller?.StopFlick();

            // Routed: the innermost hit element first, then outward, stopping at the first handler
            // that marks it handled.
            foreach (UIElement element in chain)
            {
                if (element.RaiseMouseLeftButtonDown(position)) break;
            }

            SetPressed(null);
            foreach (UIElement element in chain)
            {
                if (element is ButtonBase button) { SetPressed(button); break; }
            }
        }

        private static void SetPressed(ButtonBase? button)
        {
            if (ReferenceEquals(_pressed, button)) return;
            try { _pressed?.SetPressedByHost(false); } catch (Exception) { }
            _pressed = button;
            try { _pressed?.SetPressedByHost(true); } catch (Exception) { }
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
                SetPressed(null);
            }

            // A horizontal drag over a Panorama or Pivot pans it, live. Decided on the gesture's TOTAL travel, so a
            // drag that starts sideways stays a pan and the list inside does not scroll as well.
            if (interaction.IsDragging && _panorama != null && Math.Abs(totalX) > Math.Abs(totalY))
            {
                PanoramaState state = PanoramaStateTable.GetOrCreate(_panorama);
                state.IsDragging = true;
                state.DragOffset = totalX;
            }
            // The content follows the finger, so the viewport moves the OTHER way — drag down and
            // the offset decreases. Getting this sign wrong produces a list that scrolls away from
            // the finger, which reads as broken rather than inverted.
            else if (interaction.IsDragging && _scroller != null)
            {
                _scroller.ScrollBy(
                    interaction.LastPos.X - position.X,
                    interaction.LastPos.Y - position.Y);
            }

            // MouseMove to the elements the press went to, innermost first: a finger behaves as
            // if it captured what it touched, and a game dragging something (a slider, a piece)
            // listens on that element, not on whatever happens to be under the finger now.
            if (interaction.HitChain != null)
            {
                foreach (UIElement element in interaction.HitChain)
                {
                    if (element.RaiseMouseMove(position)) break;
                }
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
            SetPressed(null);
            if (interaction == null) return;

            var position = new Point(x, y);
            IReadOnlyList<UIElement> chain = interaction.HitChain ?? Array.Empty<UIElement>();

            foreach (UIElement element in chain)
            {
                if (element.RaiseMouseLeftButtonUp(position)) break;
            }

            UIElement? panorama = _panorama;
            _panorama = null;

            // A drag is not a tap. A horizontal one over a Panorama/Pivot commits a page turn when
            // it was a flick or crossed a third of the control, and springs back otherwise — the
            // Avalonia host's rule, ported when pure Silverlight titles moved to this router
            // (Minesweeper's whole main menu is a panorama; its game modes are on pages 3 and 4).
            if (interaction.IsDragging && panorama != null)
            {
                double dx = position.X - interaction.StartPos.X;
                double dy = position.Y - interaction.StartPos.Y;
                PanoramaState? state = PanoramaStateTable.TryGet(panorama);
                if (state != null && Math.Abs(dx) > Math.Abs(dy) && panorama is Panel panel)
                {
                    double seconds = Math.Max(0.001, (DateTime.UtcNow - interaction.StartTime).TotalSeconds);
                    bool flick = Math.Abs(dx / seconds) > PointerInteraction.FlickVelocity;
                    double width = panorama.ArrangedRect.Width > 0 ? panorama.ArrangedRect.Width : 480;
                    bool farEnough = Math.Abs(dx) > width / 3;
                    if ((flick || farEnough) && panel.Children.Count > 0)
                    {
                        state.Advance(dx < 0 ? +1 : -1, panel.Children.Count);
                    }
                    else
                    {
                        state.DragOffset = 0;
                        state.IsDragging = false;
                    }
                    _scroller = null;
                    return;
                }
            }

            if (interaction.IsDragging)
            {
                HandOffFlick(interaction, position);
                return;
            }

            _scroller = null;
            SelectTappedRow(chain);
            DispatchTap(chain, position);
        }

        /// <summary>
        /// A tap on a list row selects it, before anything hears the tap — on the phone the
        /// ListBoxItem takes the selection on the press, and a Tap handler on the list reads
        /// <c>SelectedItem</c> expecting the row that was touched (Minesweeper's mode lists do
        /// exactly that, and do nothing when it is null).
        /// </summary>
        private static void SelectTappedRow(IReadOnlyList<UIElement> chain)
        {
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                if (chain[i + 1] is Selector selector && ReferenceEquals(chain[i].Parent, selector))
                {
                    try { selector.SelectContainer(chain[i]); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[wpr-tap] selecting a row threw {ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }
            }
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
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-tap] GestureListener.Tap on {element.GetType().Name}");
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
