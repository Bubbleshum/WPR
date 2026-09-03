using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// The dark bezel strip below the phone surface that hosts the three WP7
    /// capacitive buttons (Back, Start, Search). Drawn by hand to avoid
    /// taking a dependency on an icon-font package and to stay crisp at any
    /// scale. The buttons map to host-level actions supplied by the caller —
    /// typically Back → <c>PhoneApplicationFrame.HandleBackKey</c> with a
    /// window-close fallback, Start → close the window (returning to the WPR
    /// launcher), Search → no-op (WP7 search is the OS-level Bing search,
    /// which doesn't apply here).
    ///
    /// <para><b>This is deliberately not unified with the XNA Back path</b>
    /// (<c>IGameHost.PressBackButton</c> → <c>WprPhoneBackButton</c> →
    /// <c>GamePad.Buttons.Back</c>). The two differ in semantics, not just
    /// plumbing: that one is a level-sampled gamepad button held for exactly
    /// one frame, this one is a routed event a page's <c>BackKeyPress</c>
    /// handler can cancel before <c>GoBack()</c> runs. They also have disjoint
    /// hosts — Silverlight apps are not driven by <c>IGameHost</c> at all, and
    /// there is no Silverlight path on Android — so a common interface would
    /// have two implementations and no caller that ever held it
    /// polymorphically.</para>
    /// </summary>
    internal sealed class PhoneHardwareButtons : Control
    {
        private readonly Action _onBack;
        private readonly Action _onStart;
        private readonly Action _onSearch;
        private int _pressedIndex = -1;

        /// <summary>Logical height of the strip, in DIPs. Matches the visual
        /// proportion of the real device bezel relative to a 480-wide screen.</summary>
        public const double StripHeight = 56;

        public PhoneHardwareButtons(Action onBack, Action onStart, Action onSearch)
        {
            _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
            _onStart = onStart ?? throw new ArgumentNullException(nameof(onStart));
            _onSearch = onSearch ?? throw new ArgumentNullException(nameof(onSearch));
            Height = StripHeight;
            Focusable = false;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        protected override Size MeasureOverride(Size availableSize)
            => new Size(double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width, StripHeight);

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var size = Bounds.Size;
            context.FillRectangle(Brushes.Black, new Rect(size));

            double slotWidth = size.Width / 3;
            var hoverBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            for (int i = 0; i < 3; i++)
            {
                var slot = new Rect(i * slotWidth, 0, slotWidth, size.Height);
                if (i == _pressedIndex)
                    context.FillRectangle(hoverBrush, slot);
                DrawGlyph(context, i, slot);
            }
        }

        private static void DrawGlyph(DrawingContext c, int idx, Rect slot)
        {
            var brush = Brushes.White;
            // 22 DIPs is the canonical WP7 glyph size in this bezel proportion.
            double scale = 22;
            double cx = slot.X + slot.Width / 2;
            double cy = slot.Y + slot.Height / 2;
            switch (idx)
            {
                case 0: DrawBackArrow(c, brush, cx, cy, scale); break;
                case 1: DrawStartLogo(c, brush, cx, cy, scale); break;
                case 2: DrawSearchGlass(c, brush, cx, cy, scale); break;
            }
        }

        /// <summary>WP7 back glyph: a left-pointing chevron with a flat tail.</summary>
        private static void DrawBackArrow(DrawingContext c, IBrush brush, double cx, double cy, double s)
        {
            // Geometry sized to roughly fit a s × s box, centered on (cx, cy).
            // Coordinates picked to mirror the WP7 system glyph proportions:
            // a wedge head plus a stubby tail extending to the right.
            double u = s / 24.0;
            var fig = new PathFigure
            {
                StartPoint = new Point(cx - 11 * u, cy),
                IsClosed = true,
                IsFilled = true,
                Segments = new PathSegments
                {
                    new LineSegment { Point = new Point(cx - 1 * u, cy - 9 * u) },
                    new LineSegment { Point = new Point(cx - 1 * u, cy - 3 * u) },
                    new LineSegment { Point = new Point(cx + 11 * u, cy - 3 * u) },
                    new LineSegment { Point = new Point(cx + 11 * u, cy + 3 * u) },
                    new LineSegment { Point = new Point(cx - 1 * u, cy + 3 * u) },
                    new LineSegment { Point = new Point(cx - 1 * u, cy + 9 * u) },
                },
            };
            var geo = new PathGeometry { Figures = new PathFigures { fig } };
            c.DrawGeometry(brush, null, geo);
        }

        /// <summary>WP7 start glyph: four equal squares with a small gap, the
        /// "tilted Windows logo" of that era. Drawn flat (not skewed) — matches
        /// the bezel print on the original HTC and Samsung WP7 devices.</summary>
        private static void DrawStartLogo(DrawingContext c, IBrush brush, double cx, double cy, double s)
        {
            double tile = s / 2.6;
            double gap = s / 12.0;
            double total = tile * 2 + gap;
            double x0 = cx - total / 2;
            double y0 = cy - total / 2;
            for (int row = 0; row < 2; row++)
            {
                for (int col = 0; col < 2; col++)
                {
                    var r = new Rect(x0 + col * (tile + gap), y0 + row * (tile + gap), tile, tile);
                    c.FillRectangle(brush, r);
                }
            }
        }

        /// <summary>WP7 search glyph: a circle with a short diagonal handle.</summary>
        private static void DrawSearchGlass(DrawingContext c, IBrush brush, double cx, double cy, double s)
        {
            double r = s * 0.30;
            double ox = -s * 0.10;
            double oy = -s * 0.10;
            var pen = new Pen(brush, s * 0.13);
            // Glass.
            c.DrawEllipse(null, pen, new Point(cx + ox, cy + oy), r, r);
            // Handle: from the glass's lower-right tangent point, extending
            // down-right at 45° for ~r in length.
            double diag = r * Math.Sqrt(0.5);
            var p1 = new Point(cx + ox + diag, cy + oy + diag);
            var p2 = new Point(p1.X + r * 0.95, p1.Y + r * 0.95);
            c.DrawLine(pen, p1, p2);
        }

        private int HitIndex(Point pos)
        {
            if (pos.Y < 0 || pos.Y > Bounds.Height) return -1;
            double slotWidth = Bounds.Width / 3;
            int idx = (int)(pos.X / slotWidth);
            return idx >= 0 && idx < 3 ? idx : -1;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            _pressedIndex = HitIndex(e.GetPosition(this));
            if (_pressedIndex >= 0)
            {
                e.Pointer.Capture(this);
                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            int idx = _pressedIndex;
            _pressedIndex = -1;
            e.Pointer.Capture(null);
            InvalidateVisual();
            if (idx < 0) return;

            // Only fire if release happened over the same button.
            int releaseIdx = HitIndex(e.GetPosition(this));
            if (releaseIdx != idx) return;

            try
            {
                switch (idx)
                {
                    case 0: _onBack(); break;
                    case 1: _onStart(); break;
                    case 2: _onSearch(); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bezel] button {idx} action threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
