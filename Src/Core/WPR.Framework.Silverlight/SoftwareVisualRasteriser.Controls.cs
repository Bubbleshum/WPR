using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// WP7 control chrome for the CPU rasteriser: Panorama, Pivot and their items, the progress
    /// bars, ToggleSwitch, Button, and rounded Border corners.
    /// </summary>
    /// <remarks>
    /// <para>WPR applies no <c>ControlTemplate</c>s, so a control's look is synthesised by the
    /// renderer. The geometry, state and theme colours come from <see cref="WpChrome"/>, which the
    /// Avalonia <see cref="SilverlightRenderer"/> reads too, so a page looks the same through
    /// either. Only the drawing is here.</para>
    ///
    /// <para><b>Animation and state are in the change signature</b> (<see cref="SignChrome"/>):
    /// a panorama mid-swipe, a toggled switch, a pressed button or an indeterminate progress bar
    /// changes nothing else the signature looks at, so without it the page would be painted once
    /// and never again.</para>
    /// </remarks>
    internal static partial class SoftwareVisualRasteriser
    {
        /// <summary>
        /// The colour text inherits inside a pressed button's content (WP7 draws it in the
        /// background colour on a foreground fill). Null outside one.
        /// </summary>
        [ThreadStatic]
        private static Color? _pressedContentForeground;

        [ThreadStatic]
        private static ImageSource? _bingSource;

        [ThreadStatic]
        private static string? _bingSourcePath;

        /// <summary>Line height the rasteriser's own font gives, for <see cref="WpChrome"/> layout.</summary>
        private static readonly Func<string, double, double> RasterTextHeight =
            (_, size) => TextRasteriser.GetLineHeight(size);

        /// <summary>
        /// Paints <paramref name="element"/> as a WP7 control if it is one this file knows.
        /// Returns false to let the ordinary per-type painting run.
        /// </summary>
        private static bool TryPaintChrome(
            UIElement element, Rect bounds, double opacity, Surface surface, int depth,
            double scaleX, double scaleY, Rect clip, Rect drawClip)
        {
            switch (WpChrome.Classify(element))
            {
                case WpChrome.Kind.Panorama:
                    PaintPanorama(element, bounds, opacity, surface, depth, scaleX, scaleY, clip, drawClip, isPivot: false);
                    return true;
                case WpChrome.Kind.Pivot:
                    PaintPanorama(element, bounds, opacity, surface, depth, scaleX, scaleY, clip, drawClip, isPivot: true);
                    return true;
                case WpChrome.Kind.PanoramaItem:
                    PaintPanoramaItem(element, bounds, opacity, surface, depth, scaleX, scaleY, clip, drawClip, isPivotItem: false);
                    return true;
                case WpChrome.Kind.PivotItem:
                    PaintPanoramaItem(element, bounds, opacity, surface, depth, scaleX, scaleY, clip, drawClip, isPivotItem: true);
                    return true;
                case WpChrome.Kind.ProgressBar:
                    PaintProgressBar(element, bounds, opacity, surface, scaleX, scaleY, drawClip);
                    return true;
                case WpChrome.Kind.ToggleSwitch:
                    PaintToggleSwitch(element, bounds, opacity, surface, scaleX, scaleY, drawClip);
                    return true;
            }

            // Button only, as in SilverlightRenderer: CheckBox and RadioButton are ToggleButtons and
            // have chrome of their own that neither renderer draws yet.
            if (element is Button button)
            {
                PaintButton(button, bounds, opacity, surface, depth, scaleX, scaleY, clip, drawClip);
                return true;
            }

            if (element is Border border && HasRoundedCorners(border.CornerRadius))
            {
                PaintRoundedBorder(border, bounds, opacity, surface, depth, scaleX, scaleY, clip, drawClip);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The brush a <see cref="TextBlock"/> is drawn with: its own, else a pressed button's
        /// content colour, else the nearest ancestor's <c>Foreground</c> — Silverlight's
        /// inheritance, which <see cref="SilverlightRenderer"/> follows too. Null means the theme
        /// default.
        /// </summary>
        private static Brush? EffectiveForeground(TextBlock text)
        {
            if (text.Foreground != null) return text.Foreground;
            if (_pressedContentForeground is Color pressed) return new SolidColorBrush(pressed);

            for (var fe = text.Parent as FrameworkElement; fe != null; fe = fe.Parent as FrameworkElement)
            {
                if (fe.Foreground != null) return fe.Foreground;
            }

            return null;
        }

        // -----------------------------------------------------------------------------------
        // Panorama / Pivot
        // -----------------------------------------------------------------------------------

        private static void PaintPanorama(
            UIElement element, Rect bounds, double opacity, Surface surface, int depth,
            double scaleX, double scaleY, Rect clip, Rect drawClip, bool isPivot)
        {
            double sx = scaleX == 0 ? 1 : scaleX;
            double sy = scaleY == 0 ? 1 : scaleY;

            PaintPanoramaBackground(element, bounds, opacity, surface, drawClip, isPivot);

            WpChrome.PanoramaLayout? layout = WpChrome.ArrangePanorama(
                element, bounds.Width / sx, bounds.Height / sy, isPivot, RasterTextHeight);
            if (layout == null) return;

            Color fg = WpChrome.Foreground;
            if (layout.Title != null)
            {
                DrawChromeText(surface, layout.Title, layout.TitleFontSize * sy,
                    bounds.X + (layout.TitleX * sx), bounds.Y + (layout.TitleY * sy),
                    fg, opacity, drawClip);
            }

            foreach ((string text, double x, bool current) in layout.Headers)
            {
                DrawChromeText(surface, text, layout.HeaderFontSize * sy,
                    bounds.X + (x * sx), bounds.Y + (layout.HeaderY * sy),
                    current ? fg : WpChrome.Subtle, opacity, drawClip);
            }

            // Items to either side of the current one are arranged off the control; the clip keeps
            // them from painting over whatever is next to it.
            foreach (WpChrome.Slot slot in layout.Slots)
                PaintChild(slot.Child, bounds, opacity, surface, depth, scaleX, scaleY, clip, narrowTo: bounds);
        }

        /// <summary>
        /// The control's own Background if it would paint anything; otherwise, for a panorama, the
        /// Bing wallpaper real WP7 panorama apps showed through (the toolkit style sets a
        /// transparent background), and failing that a dark grey that separates it from the page.
        /// A pivot has no such backdrop.
        /// </summary>
        private static void PaintPanoramaBackground(
            UIElement element, Rect bounds, double opacity, Surface surface, Rect drawClip, bool isPivot)
        {
            Brush? background = (element as FrameworkElement)?.Background;
            if (!WpChrome.IsEffectivelyTransparent(background))
            {
                FillBrush(surface, background, bounds, drawClip, opacity);
                return;
            }

            if (isPivot) return;

            if (TryGetBingWallpaper(out int[]? pixels, out int w, out int h))
            {
                Rect dest = FitTo(bounds, w, h, Stretch.UniformToFill, AlignmentX.Center, AlignmentY.Center);
                surface.Blit(pixels!, w, h, dest, drawClip, opacity);
                return;
            }

            surface.FillRect(bounds, drawClip, Premultiply(WpChrome.PanoramaFallback, opacity));
        }

        private static bool TryGetBingWallpaper(out int[]? pixels, out int width, out int height)
        {
            pixels = null;
            width = 0;
            height = 0;

            string? path;
            try { path = BingWallpaper.CachedFilePath; }
            catch (Exception) { return false; }
            if (path == null) return false;

            if (_bingSource == null || !string.Equals(_bingSourcePath, path, StringComparison.Ordinal))
            {
                _bingSource = new WPR.WindowsCompability.BitmapImage(new Uri(path, UriKind.Absolute));
                _bingSourcePath = path;
            }

            return SilverlightImageDecoder.TryGetPixels(_bingSource, out pixels, out width, out height);
        }

        private static void PaintPanoramaItem(
            UIElement element, Rect bounds, double opacity, Surface surface, int depth,
            double scaleX, double scaleY, Rect clip, Rect drawClip, bool isPivotItem)
        {
            double sx = scaleX == 0 ? 1 : scaleX;
            double sy = scaleY == 0 ? 1 : scaleY;

            WpChrome.PanoramaItemLayout layout = WpChrome.ArrangePanoramaItem(
                element, bounds.Width / sx, bounds.Height / sy, isPivotItem, RasterTextHeight);

            if (layout.Header != null)
            {
                DrawChromeText(surface, layout.Header, layout.HeaderFontSize * sy,
                    bounds.X + (layout.HeaderX * sx), bounds.Y + (layout.HeaderY * sy),
                    WpChrome.Foreground, opacity, drawClip);
            }

            if (layout.Presenter == null) return;

            var viewport = new Rect(
                bounds.X + (layout.Viewport.X * sx), bounds.Y + (layout.Viewport.Y * sy),
                layout.Viewport.Width * sx, layout.Viewport.Height * sy);

            // The presenter is arranged at its full natural height; shifting the parent origin up
            // by the scroll offset and clipping to the viewport shows the window onto it.
            var scrolled = new Rect(bounds.X, bounds.Y - (layout.ScrollY * sy), bounds.Width, bounds.Height);
            PaintChild(layout.Presenter, scrolled, opacity, surface, depth, scaleX, scaleY, clip, narrowTo: viewport);
        }

        // -----------------------------------------------------------------------------------
        // Progress bar
        // -----------------------------------------------------------------------------------

        private static void PaintProgressBar(
            UIElement element, Rect bounds, double opacity, Surface surface,
            double scaleX, double scaleY, Rect drawClip)
        {
            double sx = scaleX == 0 ? 1 : scaleX;
            double sy = scaleY == 0 ? 1 : scaleY;
            uint colour = Premultiply(WpChrome.ProgressColor(element), opacity);
            double centreY = bounds.Y + (bounds.Height / 2);

            if (!WpChrome.IsIndeterminate(element))
            {
                double fraction = WpChrome.ProgressFraction(element);
                if (fraction <= 0) return;
                double thickness = WpChrome.ProgressBarThickness * sy;
                surface.FillRect(
                    new Rect(bounds.X, centreY - (thickness / 2), bounds.Width * fraction, thickness),
                    drawClip, colour);
                return;
            }

            double dot = WpChrome.ProgressDotDiameter;
            foreach (double x in WpChrome.ProgressDots(bounds.Width / sx, WpChrome.ProgressClock()))
            {
                surface.FillRect(
                    new Rect(bounds.X + (x * sx), centreY - (dot * sy / 2), dot * sx, dot * sy),
                    drawClip, colour);
            }
        }

        // -----------------------------------------------------------------------------------
        // ToggleSwitch
        // -----------------------------------------------------------------------------------

        private static void PaintToggleSwitch(
            UIElement element, Rect bounds, double opacity, Surface surface,
            double scaleX, double scaleY, Rect drawClip)
        {
            double sx = scaleX == 0 ? 1 : scaleX;
            double sy = scaleY == 0 ? 1 : scaleY;
            WpChrome.ToggleLayout layout = WpChrome.LayoutToggleSwitch(element, RasterTextHeight);
            Color fg = WpChrome.Foreground;

            if (layout.Header != null)
            {
                DrawChromeText(surface, layout.Header, WpChrome.ToggleHeaderFontSize * sy,
                    bounds.X, bounds.Y + (layout.HeaderY * sy), fg, opacity, drawClip);
            }

            Rect track = Scale(layout.Track, bounds, sx, sy);
            if (layout.IsOn)
                surface.FillRect(track, drawClip, Premultiply(WpChrome.Accent, opacity));
            else
                FillEdges(surface, track, WpChrome.ToggleTrackStrokeOff * sx, WpChrome.ToggleTrackStrokeOff * sy,
                    Premultiply(fg, opacity), drawClip);

            surface.FillRect(Scale(layout.Thumb, bounds, sx, sy), drawClip, Premultiply(fg, opacity));

            DrawChromeText(surface, layout.StateText, WpChrome.ToggleStateFontSize * sy,
                bounds.X + (layout.StateX * sx), bounds.Y + (layout.StateY * sy), fg, opacity, drawClip);
        }

        // -----------------------------------------------------------------------------------
        // Button
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// WP7's default button: optional Background, a foreground border (3px unless the button
        /// sets its own), the content centred in a padded interior — and when pressed, a
        /// foreground fill with the content drawn in the background colour.
        /// </summary>
        private static void PaintButton(
            Button button, Rect bounds, double opacity, Surface surface, int depth,
            double scaleX, double scaleY, Rect clip, Rect drawClip)
        {
            double sx = scaleX == 0 ? 1 : scaleX;
            double sy = scaleY == 0 ? 1 : scaleY;
            bool pressed = WpChrome.IsPressed(button);

            if (pressed)
                surface.FillRect(bounds, drawClip, Premultiply(WpChrome.Foreground, opacity));
            else if (!WpChrome.IsEffectivelyTransparent(button.Background))
                FillBrush(surface, button.Background, bounds, drawClip, opacity);

            double thickness = WpChrome.ButtonBorderWidth(button);
            Brush? borderBrush = button.BorderBrush;
            if (borderBrush != null && !(borderBrush is SolidColorBrush))
            {
                StrokeEdges(surface, borderBrush, bounds, thickness * sx, opacity, drawClip);
            }
            else
            {
                Color borderColour = borderBrush is SolidColorBrush solid ? solid.Color : WpChrome.Foreground;
                double brushOpacity = borderBrush?.Opacity ?? 1.0;
                FillEdges(surface, bounds, thickness * sx, thickness * sy,
                    Premultiply(borderColour, opacity * Math.Min(1, Math.Max(0, brushOpacity))), drawClip);
            }

            if (WpChrome.ArrangeButtonPresenter(button, bounds.Width / sx, bounds.Height / sy) == null) return;

            Color? saved = _pressedContentForeground;
            _pressedContentForeground = pressed ? WpChrome.Background : saved;
            try
            {
                PaintChild(button.Presenter!, bounds, opacity, surface, depth, scaleX, scaleY, clip);
            }
            finally
            {
                _pressedContentForeground = saved;
            }
        }

        // -----------------------------------------------------------------------------------
        // Rounded Border
        // -----------------------------------------------------------------------------------

        private static bool HasRoundedCorners(CornerRadius c)
            => c.TopLeft > 0 || c.TopRight > 0 || c.BottomRight > 0 || c.BottomLeft > 0;

        /// <summary>
        /// A <see cref="Border"/> with a <c>CornerRadius</c>: background and outline drawn through
        /// an anti-aliased rounded-rectangle mask. The child is not clipped to the corners, which
        /// matches Silverlight.
        /// </summary>
        private static void PaintRoundedBorder(
            Border border, Rect bounds, double opacity, Surface surface, int depth,
            double scaleX, double scaleY, Rect clip, Rect drawClip)
        {
            double s = Math.Min(Math.Abs(scaleX), Math.Abs(scaleY));
            if (s == 0) s = 1;
            CornerRadius cr = border.CornerRadius;
            var outer = new RoundedBox(bounds, cr.TopLeft * s, cr.TopRight * s, cr.BottomRight * s, cr.BottomLeft * s);

            if (border.Background != null)
                FillMasked(surface, border.Background, bounds, drawClip, opacity, outer, null);

            Thickness t = border.BorderThickness;
            if (border.BorderBrush != null && (t.Left > 0 || t.Top > 0 || t.Right > 0 || t.Bottom > 0))
            {
                var innerRect = new Rect(
                    bounds.X + (t.Left * scaleX), bounds.Y + (t.Top * scaleY),
                    Math.Max(0, bounds.Width - ((t.Left + t.Right) * scaleX)),
                    Math.Max(0, bounds.Height - ((t.Top + t.Bottom) * scaleY)));
                double tl = Math.Max(t.Left, t.Top) * s, tr = Math.Max(t.Right, t.Top) * s;
                double br = Math.Max(t.Right, t.Bottom) * s, bl = Math.Max(t.Left, t.Bottom) * s;
                var inner = new RoundedBox(innerRect,
                    Math.Max(0, outer.TopLeft - tl), Math.Max(0, outer.TopRight - tr),
                    Math.Max(0, outer.BottomRight - br), Math.Max(0, outer.BottomLeft - bl));
                FillMasked(surface, border.BorderBrush, bounds, drawClip, opacity, outer, inner);
            }

            if (border.Child != null)
                PaintChild(border.Child, bounds, opacity, surface, depth, scaleX, scaleY, clip);
        }

        /// <summary>A rectangle with independently rounded corners, in device space.</summary>
        internal readonly struct RoundedBox
        {
            public readonly Rect Box;
            public readonly double TopLeft, TopRight, BottomRight, BottomLeft;

            public RoundedBox(Rect box, double tl, double tr, double br, double bl)
            {
                Box = box;
                // Silverlight scales radii down when two on one side would overlap.
                double f = 1.0;
                f = Fit(f, tl + tr, box.Width);
                f = Fit(f, bl + br, box.Width);
                f = Fit(f, tl + bl, box.Height);
                f = Fit(f, tr + br, box.Height);
                TopLeft = tl * f;
                TopRight = tr * f;
                BottomRight = br * f;
                BottomLeft = bl * f;
            }

            private static double Fit(double f, double sum, double side)
                => sum > side && sum > 0 ? Math.Min(f, side / sum) : f;

            /// <summary>Whether a point is inside.</summary>
            public bool Contains(double x, double y)
            {
                Rect b = Box;
                if (x < b.X || y < b.Y || x > b.Right || y > b.Bottom) return false;
                if (InCorner(x, y, b.X + TopLeft, b.Y + TopLeft, TopLeft, x < b.X + TopLeft && y < b.Y + TopLeft)) return false;
                if (InCorner(x, y, b.Right - TopRight, b.Y + TopRight, TopRight, x > b.Right - TopRight && y < b.Y + TopRight)) return false;
                if (InCorner(x, y, b.Right - BottomRight, b.Bottom - BottomRight, BottomRight, x > b.Right - BottomRight && y > b.Bottom - BottomRight)) return false;
                if (InCorner(x, y, b.X + BottomLeft, b.Bottom - BottomLeft, BottomLeft, x < b.X + BottomLeft && y > b.Bottom - BottomLeft)) return false;
                return true;
            }

            /// <summary>True when the point is in the corner box but OUTSIDE its arc.</summary>
            private static bool InCorner(double x, double y, double cx, double cy, double r, bool inBox)
            {
                if (!inBox || r <= 0) return false;
                double dx = x - cx, dy = y - cy;
                return (dx * dx) + (dy * dy) > r * r;
            }

            /// <summary>
            /// Fraction of the pixel at (<paramref name="px"/>, <paramref name="py"/>) covered,
            /// 4x4 supersampled, with a fast path for pixels well inside or outside.
            /// </summary>
            public double Coverage(int px, int py)
            {
                Rect b = Box;
                if (px + 1 <= b.X || py + 1 <= b.Y || px >= b.Right || py >= b.Bottom) return 0;

                double r = Math.Max(Math.Max(TopLeft, TopRight), Math.Max(BottomRight, BottomLeft));
                bool interior = px >= b.X && py >= b.Y && px + 1 <= b.Right && py + 1 <= b.Bottom;
                if (interior &&
                    (px >= b.X + r && px + 1 <= b.Right - r || py >= b.Y + r && py + 1 <= b.Bottom - r))
                    return 1;

                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                for (int sx = 0; sx < 4; sx++)
                {
                    if (Contains(px + ((sx + 0.5) / 4), py + ((sy + 0.5) / 4))) hits++;
                }
                return hits / 16.0;
            }
        }

        /// <summary>
        /// Paints <paramref name="brush"/> into the pixels covered by <paramref name="outer"/>
        /// minus <paramref name="inner"/>. The brush is painted into a scratch buffer the size of
        /// the area first, so every brush kind the rasteriser knows is maskable, not just a solid.
        /// </summary>
        private static void FillMasked(
            Surface surface, Brush brush, Rect area, Rect clip, double opacity, RoundedBox outer, RoundedBox? inner)
        {
            Rect target = Intersect(area, clip);
            int x0 = Math.Max(0, (int)Math.Floor(target.X));
            int y0 = Math.Max(0, (int)Math.Floor(target.Y));
            int x1 = Math.Min(surface.Width, (int)Math.Ceiling(target.Right));
            int y1 = Math.Min(surface.Height, (int)Math.Ceiling(target.Bottom));
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return;

            var scratch = new Surface(new uint[w * h], w, h);
            FillBrush(scratch, brush,
                new Rect(area.X - x0, area.Y - y0, area.Width, area.Height),
                new Rect(0, 0, w, h), opacity);
            if (!scratch.Painted) return;

            uint[] src = scratch.Pixels;
            uint[] dst = surface.Pixels;
            for (int y = 0; y < h; y++)
            {
                int py = y0 + y;
                for (int x = 0; x < w; x++)
                {
                    uint s = src[(y * w) + x];
                    if (s == 0) continue;
                    int px = x0 + x;
                    double cov = outer.Coverage(px, py);
                    if (inner.HasValue) cov -= inner.Value.Coverage(px, py);
                    if (cov <= 0) continue;
                    if (cov < 1) s = ScalePremultiplied(s, (uint)Math.Round(cov * 255));
                    int i = (py * surface.Width) + px;
                    dst[i] = Over(s, dst[i]);
                    surface.Painted = true;
                }
            }
        }

        private static uint ScalePremultiplied(uint c, uint k)
            => Pack(Mul(c & 0xFF, k), Mul((c >> 8) & 0xFF, k), Mul((c >> 16) & 0xFF, k), Mul(c >> 24, k));

        // -----------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------

        private static Rect Scale(Rect local, Rect origin, double sx, double sy)
            => new Rect(origin.X + (local.X * sx), origin.Y + (local.Y * sy), local.Width * sx, local.Height * sy);

        private static void FillEdges(Surface surface, Rect r, double tx, double ty, uint colour, Rect clip)
        {
            if (tx <= 0 && ty <= 0) return;
            surface.FillRect(new Rect(r.X, r.Y, r.Width, ty), clip, colour);
            surface.FillRect(new Rect(r.X, r.Bottom - ty, r.Width, ty), clip, colour);
            surface.FillRect(new Rect(r.X, r.Y + ty, tx, Math.Max(0, r.Height - (2 * ty))), clip, colour);
            surface.FillRect(new Rect(r.Right - tx, r.Y + ty, tx, Math.Max(0, r.Height - (2 * ty))), clip, colour);
        }

        private static void DrawChromeText(
            Surface surface, string text, double fontSize, double x, double y, Color colour, double opacity, Rect clip)
        {
            if (fontSize <= 0 || string.IsNullOrEmpty(text)) return;
            if (!TextRasteriser.IsAvailable)
            {
                ReportOnce("TextBlock", "no system font found, so text is not drawn");
                return;
            }

            uint c = Premultiply(colour, opacity);
            if ((c >> 24) == 0) return;
            TextRasteriser.Draw(surface.Pixels, surface.Width, surface.Height, text, fontSize, x, y, c, clip);
            surface.Painted = true;
        }

        // -----------------------------------------------------------------------------------
        // Change detection
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Folds control state the rest of <see cref="Sign"/> cannot see into the signature —
        /// see the class remarks. Children are walked by the ordinary cases.
        /// </summary>
        private static void SignChrome(UIElement element, ref HashCode hash)
        {
            switch (WpChrome.Classify(element))
            {
                case WpChrome.Kind.Panorama:
                case WpChrome.Kind.Pivot:
                {
                    PanoramaState? state = PanoramaStateTable.TryGet(element);
                    if (state != null)
                    {
                        hash.Add(state.CurrentIndex);
                        hash.Add(state.DragOffset);
                    }
                    hash.Add(WpChrome.ReadText(element, "Title"));
                    // The wallpaper lands asynchronously; the frame it arrives on must repaint.
                    if (WpChrome.IsEffectivelyTransparent((element as FrameworkElement)?.Background))
                    {
                        string? bing = null;
                        try { bing = BingWallpaper.CachedFilePath; } catch (Exception) { }
                        hash.Add(bing);
                    }
                    break;
                }

                case WpChrome.Kind.PanoramaItem:
                case WpChrome.Kind.PivotItem:
                    hash.Add(WpChrome.ReadText(element, "Header"));
                    hash.Add(PanoramaItemScrollTable.TryGet(element)?.ScrollY ?? 0);
                    break;

                case WpChrome.Kind.ProgressBar:
                    if (WpChrome.IsIndeterminate(element))
                        hash.Add(WpChrome.ProgressClock());
                    else
                        hash.Add(WpChrome.ProgressFraction(element));
                    if (element is FrameworkElement pfe) SignBrush(pfe.Foreground, ref hash);
                    break;

                case WpChrome.Kind.ToggleSwitch:
                    hash.Add(WpChrome.IsToggleOn(element));
                    hash.Add(WpChrome.ReadText(element, "Header"));
                    break;
            }

            switch (element)
            {
                case Button button:
                    hash.Add(button.IsPressed);
                    SignBrush(button.BorderBrush, ref hash);
                    hash.Add(button.BorderThickness.Left);
                    hash.Add(button.BorderThickness.Top);
                    break;

                case Border border:
                    hash.Add(border.CornerRadius.TopLeft);
                    hash.Add(border.CornerRadius.TopRight);
                    hash.Add(border.CornerRadius.BottomRight);
                    hash.Add(border.CornerRadius.BottomLeft);
                    break;

                case FrameworkElement fe when fe.Foreground != null:
                    // Text inherits it now, so a page that recolours a container must repaint.
                    SignBrush(fe.Foreground, ref hash);
                    break;
            }
        }
    }
}
