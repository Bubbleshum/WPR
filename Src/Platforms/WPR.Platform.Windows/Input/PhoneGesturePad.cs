using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WPR.Platform.Windows.Input
{
    /// <summary>
    /// A to-scale outline of the phone screen you draw a gesture on: press to set the start point,
    /// drag to set the end point, release to finish. Click without dragging for a tap.
    ///
    /// <para><b>An outline, not a screenshot.</b> Authoring a gesture needs a spatial reference,
    /// not the game's actual pixels — you are placing a finger relative to the screen, and the
    /// screen is a fixed 800x480 (or 480x800). Drawing the frame ourselves means no frame grab
    /// from a running game, no stored per-game screenshot, and it works before the game has ever
    /// been launched.</para>
    ///
    /// <para>Coordinates are reported in WP7 display space, which is what
    /// <c>KeyboardTouchBinding</c> stores and what <c>TouchPanel</c> speaks — the control scales
    /// the pointer position into that space, so what is drawn here is literally what the game
    /// receives.</para>
    /// </summary>
    public sealed class PhoneGesturePad : Control
    {
        /// <summary>WP7's fixed surface, long edge first.</summary>
        private const double LongEdge = 800;
        private const double ShortEdge = 480;

        private bool _dragging;
        private Point? _startDisplay;
        private Point? _endDisplay;

        public static readonly StyledProperty<bool> IsLandscapeProperty =
            AvaloniaProperty.Register<PhoneGesturePad, bool>(nameof(IsLandscape), true);

        /// <summary>
        /// Which way the phone is held. WP7 titles fix their own orientation, so this is a fact
        /// about the game being authored for rather than a user preference — get it wrong and the
        /// axes are transposed relative to what the game sees.
        /// </summary>
        public bool IsLandscape
        {
            get => GetValue(IsLandscapeProperty);
            set => SetValue(IsLandscapeProperty, value);
        }

        public double DisplayWidth => IsLandscape ? LongEdge : ShortEdge;
        public double DisplayHeight => IsLandscape ? ShortEdge : LongEdge;

        /// <summary>Raised whenever a gesture is drawn, with points already in display space.</summary>
        public event EventHandler<PhoneGestureDrawnEventArgs>? GestureDrawn;

        static PhoneGesturePad()
        {
            AffectsRender<PhoneGesturePad>(IsLandscapeProperty);
        }

        public PhoneGesturePad()
        {
            Focusable = true;
            MinWidth = 240;
            MinHeight = 200;
        }

        /// <summary>Shows an existing binding on the pad, so opening one for edit draws it.</summary>
        public void SetGesture(double startX, double startY, double? endX, double? endY)
        {
            _startDisplay = new Point(startX, startY);
            _endDisplay = (endX.HasValue && endY.HasValue) ? new Point(endX.Value, endY.Value) : null;
            InvalidateVisual();
        }

        public void Clear()
        {
            _startDisplay = null;
            _endDisplay = null;
            InvalidateVisual();
        }

        /// <summary>
        /// The rectangle the phone screen occupies inside the control, letterboxed to preserve
        /// aspect. Everything else in this class converts through it, so the drawing and the
        /// hit-testing cannot disagree about where the screen is.
        /// </summary>
        private Rect ScreenRect()
        {
            double aspect = DisplayWidth / DisplayHeight;
            double w = Bounds.Width - 16;
            double h = Bounds.Height - 16;
            if (w <= 0 || h <= 0) return default;

            if (w / h > aspect) w = h * aspect;
            else h = w / aspect;

            return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        }

        private Point? ToDisplay(Point p)
        {
            Rect r = ScreenRect();
            if (r.Width <= 0 || r.Height <= 0) return null;

            double x = (p.X - r.X) / r.Width * DisplayWidth;
            double y = (p.Y - r.Y) / r.Height * DisplayHeight;

            // Clamp rather than reject: a drag that leaves the screen should end at the edge,
            // which is a legal gesture, instead of silently producing nothing.
            x = Math.Max(0, Math.Min(DisplayWidth, x));
            y = Math.Max(0, Math.Min(DisplayHeight, y));
            return new Point(Math.Round(x), Math.Round(y));
        }

        private Point ToControl(Point display)
        {
            Rect r = ScreenRect();
            return new Point(
                r.X + display.X / DisplayWidth * r.Width,
                r.Y + display.Y / DisplayHeight * r.Height);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Point? d = ToDisplay(e.GetPosition(this));
            if (d == null) return;

            _dragging = true;
            _startDisplay = d;
            _endDisplay = null;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;

            _endDisplay = ToDisplay(e.GetPosition(this));
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;

            _dragging = false;
            e.Pointer.Capture(null);
            _endDisplay = ToDisplay(e.GetPosition(this));
            InvalidateVisual();

            if (_startDisplay == null) return;

            Point s = _startDisplay.Value;
            Point en = _endDisplay ?? s;

            // A short drag is a slip of the hand, not a swipe. The threshold is in display space so
            // it means the same thing regardless of how big the pad is drawn on screen.
            bool isSwipe = Math.Abs(en.X - s.X) > 12 || Math.Abs(en.Y - s.Y) > 12;
            if (!isSwipe) _endDisplay = null;

            GestureDrawn?.Invoke(this, new PhoneGestureDrawnEventArgs(s, isSwipe ? en : (Point?)null));
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);
            Rect r = ScreenRect();
            if (r.Width <= 0) return;

            var body = new Pen(new SolidColorBrush(Color.Parse("#5FFFFFFF")), 1.5);
            var screenFill = new SolidColorBrush(Color.Parse("#14FFFFFF"));

            // Body: a slightly larger rounded rect around the screen, so the pad reads as a phone
            // rather than an abstract box — it is the whole reason the reference is legible.
            var bodyRect = new Rect(r.X - 7, r.Y - 7, r.Width + 14, r.Height + 14);
            ctx.DrawRectangle(null, body, new RoundedRect(bodyRect, 10));
            ctx.DrawRectangle(screenFill, new Pen(new SolidColorBrush(Color.Parse("#3FFFFFFF")), 1), r);

            // WP7's three hardware keys, on the short edge, purely as an orientation cue.
            var glyph = new SolidColorBrush(Color.Parse("#66FFFFFF"));
            if (IsLandscape)
            {
                double cx = bodyRect.Right - 3.5;
                for (int i = 0; i < 3; i += 1)
                {
                    double cy = bodyRect.Y + bodyRect.Height * (0.3 + i * 0.2);
                    ctx.DrawEllipse(glyph, null, new Point(cx, cy), 1.6, 1.6);
                }
            }
            else
            {
                double cy = bodyRect.Bottom - 3.5;
                for (int i = 0; i < 3; i += 1)
                {
                    double cx = bodyRect.X + bodyRect.Width * (0.3 + i * 0.2);
                    ctx.DrawEllipse(glyph, null, new Point(cx, cy), 1.6, 1.6);
                }
            }

            if (_startDisplay == null) return;

            Point s = ToControl(_startDisplay.Value);
            var accent = new SolidColorBrush(WP7AccentColors.Resolve(WPR.Common.Configuration.Current?.AccentColor).Hex is string hex
                ? Color.Parse(hex)
                : Colors.DeepSkyBlue);

            if (_endDisplay != null)
            {
                Point en = ToControl(_endDisplay.Value);
                ctx.DrawLine(new Pen(accent, 2.5), s, en);
                ctx.DrawEllipse(null, new Pen(accent, 2), en, 7, 7);   // hollow = lift
            }

            ctx.DrawEllipse(accent, null, s, 5, 5);                     // filled = touch down
        }
    }

    public sealed class PhoneGestureDrawnEventArgs : EventArgs
    {
        public PhoneGestureDrawnEventArgs(Point start, Point? end)
        {
            Start = start;
            End = end;
        }

        /// <summary>Touch-down point, display space.</summary>
        public Point Start { get; }

        /// <summary>Lift point, display space, or null for a tap.</summary>
        public Point? End { get; }

        public bool IsSwipe => End != null;
    }
}
