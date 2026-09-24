using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Paints a laid-out Silverlight visual tree into a CPU pixel buffer, with no Avalonia and no
    /// GraphicsDevice. It exists for <c>UIElementRenderer</c>: a mixed-mode game composites its
    /// page into its own XNA scene, and that runs on Android, where the Avalonia renderer beside
    /// this file cannot.
    /// </summary>
    /// <remarks>
    /// <para><b>Output is premultiplied RGBA packed the way XNA's <c>Color</c> packs it</b> — R in
    /// the low byte, A in the high one. Games blit the resulting texture with a default
    /// <c>SpriteBatch.Begin()</c>, i.e. <c>BlendState.AlphaBlend</c>, which is premultiplied; a
    /// straight-alpha buffer would fringe every edge.</para>
    ///
    /// <para><b>What it draws, and what it does not.</b> Backgrounds (any
    /// <see cref="FrameworkElement"/>), <see cref="Border"/> chrome, <see cref="Shape"/> fills and
    /// strokes, <see cref="Image"/> and <see cref="TextBlock"/>, with <see cref="SolidColorBrush"/>
    /// and <see cref="ImageBrush"/>. Text goes through <see cref="TextRasteriser"/>, which
    /// rasterises glyph outlines from a system font. <c>RenderTransform</c> and
    /// <see cref="Viewbox"/> scaling are applied for the axis-aligned part — translate and
    /// scale — with rotation and skew reported and skipped; see <see cref="TryGetTransform"/>.
    /// Linear and radial gradients are painted, including their spread methods. A
    /// <see cref="ScrollViewer"/> is shown as a window onto content arranged at full extent.
    /// Each unsupported thing it meets is reported once, by name, so a blank region has a reason
    /// in the log rather than being indistinguishable from a bug.</para>
    ///
    /// <para><b>Nothing is painted that the tree does not ask for.</b> In particular there is no
    /// opaque page backdrop, unlike <see cref="SilverlightRenderer.RenderPage"/>: this output is
    /// composited over a game's own scene, so an unrequested fill would hide the game rather than
    /// the missing overlay.</para>
    ///
    /// <para><b>Coordinates come from <see cref="UIElement.ArrangedRect"/>, which is
    /// parent-relative</b>, so each level adds its parent's origin — the same model
    /// <c>SilverlightRenderer.OffsetTo</c> uses. The tree must have been laid out first;
    /// <c>MixedModeGame</c> runs one pass after navigation.</para>
    /// </remarks>
    internal static class SoftwareVisualRasteriser
    {
        /// <summary>Guards against a tree that contains a cycle; nothing legitimate is this deep.</summary>
        private const int MaxDepth = 64;

        private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

        /// <summary>
        /// Paints <paramref name="root"/> into <paramref name="dest"/>, which is cleared first.
        /// Returns true when at least one pixel was written.
        /// </summary>
        public static bool Paint(UIElement? root, int width, int height, uint[] dest)
        {
            Array.Clear(dest, 0, Math.Min(dest.Length, width * height));
            if (root == null || width <= 0 || height <= 0) return false;

            var surface = new Surface(dest, width, height);
            PaintElement(root, new Rect(0, 0, width, height), 1.0, surface, 0, 1.0, 1.0, new Rect(0, 0, width, height));
            return surface.Painted;
        }

        /// <param name="bounds">The element's slot in DEVICE space — layout already transformed.</param>
        /// <param name="scaleX">
        /// Accumulated horizontal scale from every ancestor's <c>RenderTransform</c> and
        /// <see cref="Viewbox"/>. Children's layout offsets and sizes are multiplied by it; see
        /// <see cref="PaintChild"/>.
        /// </param>
        private static void PaintElement(
            UIElement element, Rect bounds, double opacity, Surface surface, int depth,
            double scaleX, double scaleY, Rect clip)
        {
            if (depth > MaxDepth) return;
            if (element is FrameworkElement fe && fe.Visibility == Visibility.Collapsed) return;

            opacity *= element.Opacity;
            if (opacity <= 0.0) return;
            if (opacity > 1.0) opacity = 1.0;

            // The element's own transform, folded into its slot and into the scale its children
            // inherit. Translation lands in `bounds` (device space), so only the scale has to be
            // carried onward.
            if (TryGetTransform(element, out double tsx, out double tsy, out double tdx, out double tdy))
            {
                // Scaling happens about RenderTransformOrigin, expressed as a fraction of the
                // element's own size — the default (0,0) is its top-left corner.
                Point origin = element.RenderTransformOrigin;
                double ox = bounds.X + (origin.X * bounds.Width);
                double oy = bounds.Y + (origin.Y * bounds.Height);

                bounds = new Rect(
                    ox + ((bounds.X - ox) * tsx) + (tdx * scaleX),
                    oy + ((bounds.Y - oy) * tsy) + (tdy * scaleY),
                    bounds.Width * tsx,
                    bounds.Height * tsy);

                scaleX *= tsx;
                scaleY *= tsy;
            }

            // A Viewbox scales its content to its slot. It is the same operation as a
            // ScaleTransform and is applied the same way — without it a Viewbox lays its child out
            // at natural size, which is how Carcassonne's menu buttons grew tall enough to overlap
            // the toolbar beneath them.
            if (element is Viewbox viewbox)
            {
                ApplyViewboxScale(viewbox, ref bounds, ref scaleX, ref scaleY);
            }

            // Everything this element draws is clipped to its own slot AND to whatever an
            // ancestor imposed — a ScrollViewer viewport, in practice.
            Rect drawClip = Intersect(bounds, clip);

            switch (element)
            {
                case Rectangle rect:
                    FillBrush(surface, rect.Fill, bounds, drawClip, opacity);
                    StrokeEdges(surface, rect.Stroke, bounds, rect.StrokeThickness, opacity, drawClip);
                    break;

                case Shape shape:
                    // Every other shape is approximated by its bounding box. Wrong for an ellipse
                    // or a path, but a filled box in roughly the right place beats nothing at all,
                    // and WP7 chrome is overwhelmingly rectangles.
                    ReportOnce("shape:" + shape.GetType().Name, "drawn as its bounding rectangle");
                    FillBrush(surface, shape.Fill, bounds, drawClip, opacity);
                    break;

                case Border border:
                    FillBrush(surface, border.Background, bounds, drawClip, opacity);
                    StrokeBorder(surface, border, bounds, opacity, drawClip);
                    if (border.Child != null)
                        PaintChild(border.Child, bounds, opacity, surface, depth, scaleX, scaleY, clip);
                    break;

                case Popup:
                    // Popups float above the tree instead of taking part in their parent's
                    // layout, so painting one at the slot it was arranged into puts it in the
                    // wrong place (usually a zero-height one). SilverlightRenderer gives them a
                    // deferred page-level pass; nothing needs that here yet.
                    ReportOnce("Popup", "not composited into the overlay");
                    break;

                case Image image:
                    PaintImage(surface, image, bounds, opacity, drawClip);
                    break;

                case TextBlock text:
                    PaintText(surface, text, bounds, opacity, scaleY, drawClip);
                    break;

                case Frame frame:
                    if (frame.Content is UIElement framed)
                        PaintChild(framed, bounds, opacity, surface, depth, scaleX, scaleY, clip);
                    break;

                case Panel panel:
                    FillBrush(surface, panel.Background, bounds, drawClip, opacity);
                    foreach (UIElement child in panel.Children)
                        PaintChild(child, bounds, opacity, surface, depth, scaleX, scaleY, clip);
                    break;

                // Before the general ContentControl case, which it derives from: a scroll viewer
                // shows a WINDOW onto content arranged at full extent, so its child is shifted by
                // the offset and clipped to the viewport. Without the clip the content draws over
                // everything around it, which is worse than not scrolling.
                case ScrollViewer scroller:
                    FillBrush(surface, scroller.Background, bounds, drawClip, opacity);
                    if (scroller.Presenter != null)
                    {
                        var scrolled = new Rect(
                            bounds.X - (scroller.HorizontalOffset * scaleX),
                            bounds.Y - (scroller.VerticalOffset * scaleY),
                            bounds.Width,
                            bounds.Height);

                        PaintChild(
                            scroller.Presenter, scrolled, opacity, surface, depth, scaleX, scaleY,
                            clip, narrowTo: bounds);
                    }
                    break;

                case ContentControl content:
                    FillBrush(surface, content.Background, bounds, drawClip, opacity);
                    if (content.Presenter != null)
                        PaintChild(content.Presenter, bounds, opacity, surface, depth, scaleX, scaleY, clip);
                    break;

                default:
                    if (element is FrameworkElement plain)
                        FillBrush(surface, plain.Background, bounds, drawClip, opacity);
                    break;
            }
        }

        /// <param name="clip">
        /// Narrows what the child and its descendants may draw into. Passed by
        /// <see cref="ScrollViewer"/>, whose content is arranged at full extent and must not paint
        /// outside the viewport; null inherits the caller's.
        /// </param>
        private static void PaintChild(
            UIElement child, Rect parentBounds, double opacity, Surface surface, int depth,
            double scaleX, double scaleY, Rect inheritedClip, Rect? narrowTo = null)
        {
            // ArrangedRect is parent-relative and in LAYOUT units, so both the offset and the size
            // scale by whatever the ancestors' transforms accumulated. Adding the offset
            // unscaled — which is what this did before transforms existed — puts a scaled
            // element's children at the wrong place inside it.
            Rect r = child.ArrangedRect;
            PaintElement(
                child,
                new Rect(
                    parentBounds.X + (r.X * scaleX),
                    parentBounds.Y + (r.Y * scaleY),
                    r.Width * scaleX,
                    r.Height * scaleY),
                opacity,
                surface,
                depth + 1,
                scaleX,
                scaleY,
                narrowTo.HasValue ? Intersect(narrowTo.Value, inheritedClip) : inheritedClip);
        }

        /// <summary>
        /// The element's <c>RenderTransform</c> reduced to a scale and a translation, or false
        /// when it has none that moves anything.
        /// </summary>
        /// <remarks>
        /// <para><b>Axis-aligned only, on purpose.</b> Scale and translate keep every drawing
        /// operation a rectangle, so the whole existing fill/blit/text path applies unchanged and
        /// stays exact. Rotation and skew would need inverse-mapped sampling per destination pixel
        /// and a different code path for each primitive — a much larger piece of work for a much
        /// rarer case in WP7 UI, where transforms are overwhelmingly slide-ins and scale pulses.
        /// A rotation or skew is reported once and its scale/translate part still applied, so the
        /// element lands in the right place at the right size and simply is not turned.</para>
        ///
        /// <para>Translation is returned in the element's own LAYOUT units; the caller scales it by
        /// the transform already in effect.</para>
        /// </remarks>
        private static bool TryGetTransform(
            UIElement element, out double scaleX, out double scaleY, out double dx, out double dy)
        {
            scaleX = 1;
            scaleY = 1;
            dx = 0;
            dy = 0;

            Transform? transform = element.RenderTransform;
            if (transform == null) return false;

            Accumulate(transform, ref scaleX, ref scaleY, ref dx, ref dy, 0);

            return scaleX != 1 || scaleY != 1 || dx != 0 || dy != 0;
        }

        private static void Accumulate(
            Transform? transform, ref double scaleX, ref double scaleY, ref double dx, ref double dy, int depth)
        {
            if (transform == null || depth > 8) return;

            switch (transform)
            {
                case TranslateTransform t:
                    dx += t.X;
                    dy += t.Y;
                    break;

                case ScaleTransform s:
                    scaleX *= s.ScaleX;
                    scaleY *= s.ScaleY;
                    break;

                case CompositeTransform c:
                    scaleX *= c.ScaleX;
                    scaleY *= c.ScaleY;
                    dx += c.TranslateX;
                    dy += c.TranslateY;
                    if (c.Rotation != 0 || c.SkewX != 0 || c.SkewY != 0)
                        ReportOnce("CompositeTransform.Rotation", "rotation and skew are not applied");
                    break;

                case RotateTransform r:
                    if (r.Angle != 0) ReportOnce("RotateTransform", "rotation is not applied");
                    break;

                case TransformGroup group:
                    foreach (Transform child in group.Children)
                        Accumulate(child, ref scaleX, ref scaleY, ref dx, ref dy, depth + 1);
                    break;

                default:
                    ReportOnce("transform:" + transform.GetType().Name, "not applied");
                    break;
            }
        }

        /// <summary>
        /// Folds a <see cref="Viewbox"/>'s content scaling into <paramref name="bounds"/> and the
        /// scale its child inherits.
        /// </summary>
        /// <remarks>
        /// The child is laid out at its natural size and then scaled to fit, which is what a
        /// Viewbox is for. <see cref="Stretch.Uniform"/> — the default — preserves aspect ratio;
        /// <see cref="Stretch.Fill"/> does not; <see cref="Stretch.None"/> leaves it alone.
        /// <see cref="StretchDirection"/> is honoured so a Viewbox set to shrink-only does not
        /// enlarge small content.
        /// </remarks>
        private static void ApplyViewboxScale(
            Viewbox viewbox, ref Rect bounds, ref double scaleX, ref double scaleY)
        {
            if (viewbox.Stretch == Stretch.None) return;

            UIElement? child = viewbox.Presenter ?? viewbox.Content as UIElement;
            if (child == null) return;

            Rect natural = child.ArrangedRect;
            if (natural.Width <= 0 || natural.Height <= 0) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            // bounds is device space and `natural` is layout units, so divide out the scale
            // already in effect to compare like with like.
            double slotWidth = bounds.Width / (scaleX == 0 ? 1 : scaleX);
            double slotHeight = bounds.Height / (scaleY == 0 ? 1 : scaleY);

            double fitX = slotWidth / natural.Width;
            double fitY = slotHeight / natural.Height;

            if (viewbox.Stretch == Stretch.Uniform)
            {
                double uniform = Math.Min(fitX, fitY);
                fitX = uniform;
                fitY = uniform;
            }
            else if (viewbox.Stretch == Stretch.UniformToFill)
            {
                double uniform = Math.Max(fitX, fitY);
                fitX = uniform;
                fitY = uniform;
            }

            if (viewbox.StretchDirection == StretchDirection.UpOnly && (fitX < 1 || fitY < 1)) return;
            if (viewbox.StretchDirection == StretchDirection.DownOnly && (fitX > 1 || fitY > 1)) return;

            scaleX *= fitX;
            scaleY *= fitY;
        }

        /// <summary>
        /// Draws a <see cref="TextBlock"/>'s text with <see cref="TextRasteriser"/>.
        /// </summary>
        /// <remarks>
        /// <para>The foreground defaults to the WP7 dark theme's white rather than to nothing: a
        /// <c>TextBlock</c> that inherits its brush from a <c>Style</c> or an ancestor arrives here
        /// with a null <see cref="TextBlock.Foreground"/>, and WPR resolves neither inheritance nor
        /// templates — so defaulting to "invisible" would blank most of the text on a page for a
        /// reason no log would explain.</para>
        ///
        /// <para>Alignment is measured against the arranged slot, which is what Silverlight aligns
        /// within. Wrapping only when the element asked for it, so a single-line label cannot be
        /// broken by a slot that happens to be narrow.</para>
        /// </remarks>
        /// <param name="scale">
        /// The vertical scale in effect. Glyphs are rasterised at the scaled size rather than
        /// drawn small and stretched, so text inside a <see cref="Viewbox"/> stays sharp — which
        /// is the whole reason WP7 designs put it in one.
        /// </param>
        private static void PaintText(Surface surface, TextBlock text, Rect bounds, double opacity, double scale, Rect clip)
        {
            string? content = text.Text;

            if (string.IsNullOrEmpty(content)) return;

            double fontSize = text.FontSize * (scale > 0 ? scale : 1);
            if (fontSize <= 0) return;

            if (!TextRasteriser.IsAvailable)
            {
                ReportOnce("TextBlock", "no system font found, so text is not drawn");
                return;
            }

            uint colour = ResolveTextColour(text.Foreground, opacity);
            if (colour == 0) return;

            double layoutWidth = text.TextWrapping == TextWrapping.Wrap
                ? bounds.Width
                : double.PositiveInfinity;

            TextRasteriser.Draw(
                surface.Pixels, surface.Width, surface.Height,
                content, fontSize,
                bounds.X, bounds.Y,
                colour, Intersect(bounds, clip),
                layoutWidth, text.TextAlignment);

            surface.Painted = true;
        }

        /// <summary>
        /// The premultiplied colour to draw text in — the brush's own colour, or the WP7 dark
        /// theme's white when it has none (see <see cref="PaintText"/>).
        /// </summary>
        private static uint ResolveTextColour(Brush? foreground, double opacity)
        {
            if (foreground is SolidColorBrush solid)
            {
                double brushOpacity = solid.Opacity;
                if (brushOpacity <= 0) return 0;
                if (brushOpacity > 1) brushOpacity = 1;
                return Premultiply(solid.Color, opacity * brushOpacity);
            }

            if (foreground != null)
                ReportOnce("textbrush:" + foreground.GetType().Name, "text drawn in the default colour");

            return Premultiply(Color.FromArgb(255, 255, 255, 255), opacity);
        }

        private static void PaintImage(Surface surface, Image image, Rect bounds, double opacity, Rect clip)
        {
            if (!SilverlightImageDecoder.TryGetPixels(image.Source, out int[]? pixels, out int sw, out int sh))
                return;

            Rect dest = FitTo(bounds, sw, sh, image.Stretch, AlignmentX.Center, AlignmentY.Center);
            surface.Blit(pixels!, sw, sh, dest, Intersect(bounds, clip), opacity);
        }

        /// <summary>
        /// Paints <paramref name="brush"/> over <paramref name="area"/>, clipped to
        /// <paramref name="clip"/>.
        /// </summary>
        private static void FillBrush(Surface surface, Brush? brush, Rect area, Rect clip, double opacity)
        {
            if (brush == null) return;

            double brushOpacity = brush.Opacity;
            if (brushOpacity <= 0) return;
            double effective = opacity * (brushOpacity > 1 ? 1 : brushOpacity);

            switch (brush)
            {
                case SolidColorBrush solid:
                    surface.FillRect(area, clip, Premultiply(solid.Color, effective));
                    break;

                case ImageBrush img:
                {
                    if (!SilverlightImageDecoder.TryGetPixels(img.ImageSource, out int[]? pixels, out int sw, out int sh))
                        return;
                    Rect dest = FitTo(area, sw, sh, img.Stretch, img.AlignmentX, img.AlignmentY);
                    surface.Blit(pixels!, sw, sh, dest, Intersect(area, clip), effective);
                    break;
                }

                case LinearGradientBrush linear:
                    FillLinearGradient(surface, linear, area, clip, effective);
                    break;

                case RadialGradientBrush radial:
                    FillRadialGradient(surface, radial, area, clip, effective);
                    break;

                default:
                    ReportOnce("brush:" + brush.GetType().Name, "not painted");
                    break;
            }
        }

        /// <summary>Resolution of the precomputed colour ramp a gradient is sampled through.</summary>
        /// <remarks>
        /// 256 entries is one per 8-bit channel step, so the ramp is not the thing that bands.
        /// Precomputing it turns per-pixel work into an array index — worth it because these fills
        /// are per-pixel loops rather than the memset a solid colour gets.
        /// </remarks>
        private const int GradientRampSize = 256;

        /// <summary>
        /// Builds a premultiplied colour ramp from a gradient's stops.
        /// </summary>
        /// <remarks>
        /// <para>Stops are SORTED by offset and interpolated in straight alpha before
        /// premultiplying — interpolating premultiplied colours darkens the transition to or from
        /// a transparent stop, which is the classic "fade to black instead of to nothing" artefact
        /// and exactly what a gradient is usually used for.</para>
        ///
        /// <para>A brush with no stops paints nothing; with one stop it is a solid colour. Both are
        /// legal XAML and neither should be an error.</para>
        /// </remarks>
        private static uint[]? BuildGradientRamp(GradientBrush brush, double opacity)
        {
            var stops = new List<GradientStop>(brush.GradientStops);
            stops.RemoveAll(s => s == null);
            if (stops.Count == 0) return null;

            stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            var ramp = new uint[GradientRampSize];
            int next = 0;

            for (int i = 0; i < GradientRampSize; i++)
            {
                double t = (double)i / (GradientRampSize - 1);

                while (next < stops.Count - 1 && stops[next + 1].Offset < t) next++;

                GradientStop from = stops[next];
                GradientStop to = stops[Math.Min(next + 1, stops.Count - 1)];

                double span = to.Offset - from.Offset;
                double local = span <= 0 ? 0 : Clamp01((t - from.Offset) / span);

                // Before the first stop and after the last, hold the end colour — the ramp's own
                // ends; SpreadMethod decides what happens outside the gradient's extent, not here.
                if (t <= stops[0].Offset) { from = stops[0]; to = stops[0]; local = 0; }
                else if (t >= stops[stops.Count - 1].Offset)
                {
                    from = stops[stops.Count - 1];
                    to = from;
                    local = 0;
                }

                byte a = Lerp(from.Color.A, to.Color.A, local);
                byte r = Lerp(from.Color.R, to.Color.R, local);
                byte g = Lerp(from.Color.G, to.Color.G, local);
                byte b = Lerp(from.Color.B, to.Color.B, local);

                ramp[i] = Premultiply(Color.FromArgb(a, r, g, b), opacity);
            }

            return ramp;
        }

        private static byte Lerp(byte from, byte to, double t)
            => (byte)Math.Round(from + ((to - from) * t));

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>
        /// Paints a linear gradient: the parameter is the projection of each pixel onto the
        /// start→end axis.
        /// </summary>
        /// <remarks>
        /// <c>RelativeToBoundingBox</c> (the default) means the endpoints are fractions of the
        /// painted area, so the usual <c>StartPoint="0,0" EndPoint="0,1"</c> is a top-to-bottom
        /// fade whatever the element's size.
        /// </remarks>
        private static void FillLinearGradient(
            Surface surface, LinearGradientBrush brush, Rect area, Rect clip, double opacity)
        {
            uint[]? ramp = BuildGradientRamp(brush, opacity);
            if (ramp == null || area.Width <= 0 || area.Height <= 0) return;

            bool relative = brush.MappingMode == BrushMappingMode.RelativeToBoundingBox;

            double x0 = area.X + (relative ? brush.StartPoint.X * area.Width : brush.StartPoint.X);
            double y0 = area.Y + (relative ? brush.StartPoint.Y * area.Height : brush.StartPoint.Y);
            double x1 = area.X + (relative ? brush.EndPoint.X * area.Width : brush.EndPoint.X);
            double y1 = area.Y + (relative ? brush.EndPoint.Y * area.Height : brush.EndPoint.Y);

            double dx = x1 - x0;
            double dy = y1 - y0;
            double lengthSquared = (dx * dx) + (dy * dy);

            // Degenerate axis: Silverlight paints the last stop's colour flat.
            if (lengthSquared <= 0.0000001)
            {
                surface.FillRect(area, clip, ramp[GradientRampSize - 1]);
                return;
            }

            surface.FillShaded(area, clip, ramp, brush.SpreadMethod,
                (px, py) => (((px - x0) * dx) + ((py - y0) * dy)) / lengthSquared);
        }

        /// <summary>
        /// Paints a radial gradient: the parameter is the distance from the origin, normalised by
        /// the radii so an elliptical brush stays elliptical.
        /// </summary>
        /// <remarks>
        /// Measured from <c>GradientOrigin</c> rather than <c>Center</c> — that is what makes the
        /// highlight of a sphere sit off-centre, and defaulting it to the centre would quietly
        /// flatten every brush that sets it.
        /// </remarks>
        private static void FillRadialGradient(
            Surface surface, RadialGradientBrush brush, Rect area, Rect clip, double opacity)
        {
            uint[]? ramp = BuildGradientRamp(brush, opacity);
            if (ramp == null || area.Width <= 0 || area.Height <= 0) return;

            bool relative = brush.MappingMode == BrushMappingMode.RelativeToBoundingBox;

            double originX = area.X + (relative ? brush.GradientOrigin.X * area.Width : brush.GradientOrigin.X);
            double originY = area.Y + (relative ? brush.GradientOrigin.Y * area.Height : brush.GradientOrigin.Y);

            double radiusX = relative ? brush.RadiusX * area.Width : brush.RadiusX;
            double radiusY = relative ? brush.RadiusY * area.Height : brush.RadiusY;

            if (radiusX <= 0 || radiusY <= 0)
            {
                surface.FillRect(area, clip, ramp[GradientRampSize - 1]);
                return;
            }

            surface.FillShaded(area, clip, ramp, brush.SpreadMethod, (px, py) =>
            {
                double nx = (px - originX) / radiusX;
                double ny = (py - originY) / radiusY;
                return Math.Sqrt((nx * nx) + (ny * ny));
            });
        }

        private static void StrokeBorder(Surface surface, Border border, Rect bounds, double opacity, Rect clip)
        {
            Brush? stroke = border.BorderBrush;
            if (stroke == null) return;

            Thickness t = border.BorderThickness;
            if (t.Left <= 0 && t.Top <= 0 && t.Right <= 0 && t.Bottom <= 0) return;

            FillBrush(surface, stroke, new Rect(bounds.X, bounds.Y, bounds.Width, t.Top), clip, opacity);
            FillBrush(surface, stroke, new Rect(bounds.X, bounds.Bottom - t.Bottom, bounds.Width, t.Bottom), clip, opacity);
            FillBrush(surface, stroke, new Rect(bounds.X, bounds.Y, t.Left, bounds.Height), clip, opacity);
            FillBrush(surface, stroke, new Rect(bounds.Right - t.Right, bounds.Y, t.Right, bounds.Height), clip, opacity);
        }

        private static void StrokeEdges(Surface surface, Brush? stroke, Rect bounds, double thickness, double opacity, Rect clip)
        {
            if (stroke == null || thickness <= 0) return;
            FillBrush(surface, stroke, new Rect(bounds.X, bounds.Y, bounds.Width, thickness), clip, opacity);
            FillBrush(surface, stroke, new Rect(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), clip, opacity);
            FillBrush(surface, stroke, new Rect(bounds.X, bounds.Y, thickness, bounds.Height), clip, opacity);
            FillBrush(surface, stroke, new Rect(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), clip, opacity);
        }

        /// <summary>
        /// Where an image of <paramref name="sw"/>x<paramref name="sh"/> lands inside
        /// <paramref name="slot"/> for a given <see cref="Stretch"/> and alignment. Silverlight
        /// clips the result to the slot rather than growing it, which is the caller's job.
        /// </summary>
        private static Rect FitTo(Rect slot, int sw, int sh, Stretch stretch, AlignmentX ax, AlignmentY ay)
        {
            if (sw <= 0 || sh <= 0 || slot.Width <= 0 || slot.Height <= 0) return slot;
            if (stretch == Stretch.Fill) return slot;

            double scale;
            switch (stretch)
            {
                case Stretch.None:
                    scale = 1.0;
                    break;
                case Stretch.UniformToFill:
                    scale = Math.Max(slot.Width / sw, slot.Height / sh);
                    break;
                default: // Uniform
                    scale = Math.Min(slot.Width / sw, slot.Height / sh);
                    break;
            }

            double w = sw * scale;
            double h = sh * scale;

            double x = ax switch
            {
                AlignmentX.Left => slot.X,
                AlignmentX.Right => slot.Right - w,
                _ => slot.X + (slot.Width - w) / 2,
            };
            double y = ay switch
            {
                AlignmentY.Top => slot.Y,
                AlignmentY.Bottom => slot.Bottom - h,
                _ => slot.Y + (slot.Height - h) / 2,
            };

            return new Rect(x, y, w, h);
        }

        private static Rect Intersect(Rect a, Rect b)
        {
            double x = Math.Max(a.X, b.X);
            double y = Math.Max(a.Y, b.Y);
            double r = Math.Min(a.Right, b.Right);
            double t = Math.Min(a.Bottom, b.Bottom);
            return r <= x || t <= y ? new Rect(0, 0, 0, 0) : new Rect(x, y, r - x, t - y);
        }

        private static void ReportOnce(string key, string what)
        {
            lock (Reported)
            {
                if (!Reported.Add(key)) return;
            }
            System.Diagnostics.Trace.WriteLine("[wpr-uirender] " + key + ": " + what + ".");
        }

        // ---------------------------------------------------------------------------------
        // Change detection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// A hash of everything this rasteriser would look at. <c>UIElementRenderer.Render</c>
        /// runs from a game's draw, so re-walking a few nodes to decide whether to repaint is
        /// enormously cheaper than clearing and recompositing a full-screen buffer sixty times a
        /// second — which for a static page is what it would otherwise do for ever.
        /// </summary>
        public static int Signature(UIElement? root)
        {
            var hash = new HashCode();
            // Folded in so that the frame after an image finally decodes is repainted: the tree
            // itself does not change when that happens, only what we can draw from it.
            hash.Add(SilverlightImageDecoder.Generation);
            Sign(root, ref hash, 0);
            return hash.ToHashCode();
        }

        private static void Sign(UIElement? element, ref HashCode hash, int depth)
        {
            if (element == null || depth > MaxDepth) return;

            hash.Add(element.GetType());
            Rect r = element.ArrangedRect;
            hash.Add(r.X); hash.Add(r.Y); hash.Add(r.Width); hash.Add(r.Height);
            hash.Add(element.Opacity);

            // A storyboard that slides a panel animates its transform and nothing else — the
            // arranged rect never moves — so without this the page hashes identically every frame
            // and the slide is never drawn.
            if (TryGetTransform(element, out double tsx, out double tsy, out double tdx, out double tdy))
            {
                hash.Add(tsx); hash.Add(tsy); hash.Add(tdx); hash.Add(tdy);
            }
            if (element is FrameworkElement fe)
            {
                hash.Add((int)fe.Visibility);
                SignBrush(fe.Background, ref hash);
            }

            switch (element)
            {
                case Shape shape:
                    SignBrush(shape.Fill, ref hash);
                    SignBrush(shape.Stroke, ref hash);
                    hash.Add(shape.StrokeThickness);
                    break;

                case Border border:
                    SignBrush(border.BorderBrush, ref hash);
                    hash.Add(border.BorderThickness.Left);
                    hash.Add(border.BorderThickness.Top);
                    hash.Add(border.BorderThickness.Right);
                    hash.Add(border.BorderThickness.Bottom);
                    Sign(border.Child, ref hash, depth + 1);
                    break;

                case Image image:
                    hash.Add(RuntimeHelpers.GetHashCode(image.Source));
                    hash.Add((int)image.Stretch);
                    break;

                // Without this a page that changes only its text — a score, a countdown, a
                // selected menu label — hashes identically and is never repainted, so the old
                // string stays on screen for ever.
                case TextBlock text:
                    hash.Add(text.Text);
                    hash.Add(text.FontSize);
                    hash.Add((int)text.TextAlignment);
                    hash.Add((int)text.TextWrapping);
                    SignBrush(text.Foreground, ref hash);
                    break;

                case Frame frame:
                    Sign(frame.Content as UIElement, ref hash, depth + 1);
                    break;

                case Panel panel:
                    foreach (UIElement child in panel.Children)
                        Sign(child, ref hash, depth + 1);
                    break;

                // Before ContentControl: a list being dragged changes only its offset, so without
                // this the page hashes identically all the way down a scroll and never repaints.
                case ScrollViewer scroller:
                    hash.Add(scroller.HorizontalOffset);
                    hash.Add(scroller.VerticalOffset);
                    Sign(scroller.Presenter, ref hash, depth + 1);
                    break;

                case ContentControl content:
                    Sign(content.Presenter, ref hash, depth + 1);
                    break;
            }
        }

        private static void SignBrush(Brush? brush, ref HashCode hash)
        {
            if (brush == null) { hash.Add(0); return; }
            hash.Add(brush.GetType());
            hash.Add(brush.Opacity);
            switch (brush)
            {
                case SolidColorBrush solid:
                    Color c = solid.Color;
                    hash.Add((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
                    break;
                // A storyboard animating a stop's Colour or Offset changes nothing else about the
                // tree, so without the stops in the hash an animated gradient never repaints.
                case GradientBrush gradient:
                    hash.Add((int)gradient.SpreadMethod);
                    hash.Add((int)gradient.MappingMode);
                    foreach (GradientStop stop in gradient.GradientStops)
                    {
                        if (stop == null) continue;
                        Color sc = stop.Color;
                        hash.Add((sc.A << 24) | (sc.R << 16) | (sc.G << 8) | sc.B);
                        hash.Add(stop.Offset);
                    }
                    if (gradient is LinearGradientBrush lin)
                    {
                        hash.Add(lin.StartPoint.X); hash.Add(lin.StartPoint.Y);
                        hash.Add(lin.EndPoint.X); hash.Add(lin.EndPoint.Y);
                    }
                    else if (gradient is RadialGradientBrush rad)
                    {
                        hash.Add(rad.GradientOrigin.X); hash.Add(rad.GradientOrigin.Y);
                        hash.Add(rad.RadiusX); hash.Add(rad.RadiusY);
                    }
                    break;

                case ImageBrush img:
                    hash.Add(RuntimeHelpers.GetHashCode(img.ImageSource));
                    hash.Add((int)img.Stretch);
                    hash.Add((int)img.AlignmentX);
                    hash.Add((int)img.AlignmentY);
                    break;
            }
        }

        // ---------------------------------------------------------------------------------
        // Pixels
        // ---------------------------------------------------------------------------------

        /// <summary>Straight-alpha Silverlight colour to premultiplied packed RGBA.</summary>
        private static uint Premultiply(Color color, double opacity)
        {
            uint a = (uint)Math.Round(color.A * opacity);
            if (a > 255) a = 255;
            return Pack(Mul(color.R, a), Mul(color.G, a), Mul(color.B, a), a);
        }

        /// <summary>Straight-alpha 0xAARRGGBB to premultiplied packed RGBA.</summary>
        private static uint Premultiply(int argb, uint alphaScale)
        {
            uint a = Mul((uint)((argb >> 24) & 0xFF), alphaScale);
            if (a == 0) return 0;
            return Pack(
                Mul((uint)((argb >> 16) & 0xFF), a),
                Mul((uint)((argb >> 8) & 0xFF), a),
                Mul((uint)(argb & 0xFF), a),
                a);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Pack(uint r, uint g, uint b, uint a) => r | (g << 8) | (b << 16) | (a << 24);

        /// <summary>x * y / 255, rounded, without a divide.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Mul(uint x, uint y)
        {
            uint t = x * y + 128;
            return (t + (t >> 8)) >> 8;
        }

        /// <summary>Source-over, both operands premultiplied.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Over(uint src, uint dst)
        {
            uint sa = src >> 24;
            if (sa == 255) return src;
            if (sa == 0) return dst;
            uint inv = 255 - sa;
            return Pack(
                (src & 0xFF) + Mul(dst & 0xFF, inv),
                ((src >> 8) & 0xFF) + Mul((dst >> 8) & 0xFF, inv),
                ((src >> 16) & 0xFF) + Mul((dst >> 16) & 0xFF, inv),
                sa + Mul(dst >> 24, inv));
        }

        private sealed class Surface
        {
            private readonly uint[] _px;
            private readonly int _w;
            private readonly int _h;

            public Surface(uint[] pixels, int width, int height)
            {
                _px = pixels;
                _w = width;
                _h = height;
            }

            public bool Painted { get; set; }

            /// <summary>
            /// The raw buffer, for <see cref="TextRasteriser"/>, which composites glyph coverage
            /// itself rather than going through <see cref="FillRect"/> per pixel. Exposed rather
            /// than adding a glyph-shaped method here so the font code stays in one file.
            /// </summary>
            public uint[] Pixels => _px;

            public int Width => _w;

            public int Height => _h;

            public void FillRect(Rect area, Rect clip, uint premultiplied)
            {
                if ((premultiplied >> 24) == 0) return;

                ToPixels(area, clip, out int x0, out int y0, out int x1, out int y1);
                if (x1 <= x0 || y1 <= y0) return;

                bool opaque = (premultiplied >> 24) == 255;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * _w;
                    if (opaque)
                    {
                        for (int x = x0; x < x1; x++) _px[row + x] = premultiplied;
                    }
                    else
                    {
                        for (int x = x0; x < x1; x++) _px[row + x] = Over(premultiplied, _px[row + x]);
                    }
                }
                Painted = true;
            }

            /// <summary>
            /// Fills <paramref name="area"/> by evaluating <paramref name="parameter"/> at each
            /// pixel centre and sampling <paramref name="ramp"/>.
            /// </summary>
            /// <remarks>
            /// <para>One shaded fill serves both gradient kinds: the only thing that differs
            /// between a linear and a radial brush is how a pixel becomes a number between 0 and
            /// 1, and that is the delegate. The spread method — what happens OUTSIDE that
            /// range — is shared and lives here.</para>
            ///
            /// <para>A delegate per pixel is slower than inlining each gradient's maths, and that
            /// is an accepted trade: a WP7 page is static in the steady state and
            /// <c>UIElementRenderer</c> only repaints it when its signature changes, so these
            /// loops run on a change rather than per frame.</para>
            ///
            /// <para><b>Pixel CENTRES, not corners.</b> Sampling at the corner shifts a gradient
            /// half a pixel and, worse, makes the first and last rows of a short gradient wrong by
            /// a whole ramp step.</para>
            /// </remarks>
            public void FillShaded(
                Rect area, Rect clip, uint[] ramp, GradientSpreadMethod spread,
                Func<double, double, double> parameter)
            {
                if (ramp.Length == 0) return;

                ToPixels(area, clip, out int x0, out int y0, out int x1, out int y1);
                if (x1 <= x0 || y1 <= y0) return;

                int last = ramp.Length - 1;

                for (int y = y0; y < y1; y++)
                {
                    int row = y * _w;
                    double py = y + 0.5;

                    for (int x = x0; x < x1; x++)
                    {
                        double t = Spread(parameter(x + 0.5, py), spread);

                        int index = (int)(t * last);
                        if (index < 0) index = 0;
                        else if (index > last) index = last;

                        uint colour = ramp[index];
                        if ((colour >> 24) == 0) continue;

                        _px[row + x] = (colour >> 24) == 255
                            ? colour
                            : Over(colour, _px[row + x]);
                    }
                }

                Painted = true;
            }

            /// <summary>Maps a raw gradient parameter into 0..1 according to the spread method.</summary>
            private static double Spread(double t, GradientSpreadMethod spread)
            {
                switch (spread)
                {
                    case GradientSpreadMethod.Repeat:
                        t -= Math.Floor(t);
                        return t;

                    case GradientSpreadMethod.Reflect:
                    {
                        // Period 2, folded — so 1.2 reads as 0.8 and the ramp mirrors rather than
                        // jumping back to its start.
                        double period = Math.Abs(t) % 2.0;
                        return period > 1.0 ? 2.0 - period : period;
                    }

                    default:
                        return t < 0 ? 0 : (t > 1 ? 1 : t);
                }
            }

            /// <summary>
            /// Point-sampled blit of a straight-alpha ARGB image into <paramref name="dest"/>,
            /// clipped to <paramref name="clip"/>.
            /// </summary>
            /// <remarks>
            /// Point sampling rather than bilinear on purpose: the overwhelmingly common case here
            /// is a WVGA asset painted at WVGA, i.e. 1:1, where filtering costs three extra reads
            /// per pixel and changes nothing. A scaled brush is slightly harder-edged than
            /// Silverlight's; that is the trade.
            /// </remarks>
            public void Blit(int[] src, int srcWidth, int srcHeight, Rect dest, Rect clip, double opacity)
            {
                if (srcWidth <= 0 || srcHeight <= 0 || dest.Width <= 0 || dest.Height <= 0) return;

                uint alphaScale = (uint)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
                if (alphaScale == 0) return;

                ToPixels(dest, clip, out int x0, out int y0, out int x1, out int y1);
                if (x1 <= x0 || y1 <= y0) return;

                double uScale = srcWidth / dest.Width;
                double vScale = srcHeight / dest.Height;

                for (int y = y0; y < y1; y++)
                {
                    int sy = (int)((y + 0.5 - dest.Y) * vScale);
                    if (sy < 0) sy = 0; else if (sy >= srcHeight) sy = srcHeight - 1;
                    int srcRow = sy * srcWidth;
                    int dstRow = y * _w;

                    for (int x = x0; x < x1; x++)
                    {
                        int sx = (int)((x + 0.5 - dest.X) * uScale);
                        if (sx < 0) sx = 0; else if (sx >= srcWidth) sx = srcWidth - 1;

                        uint s = Premultiply(src[srcRow + sx], alphaScale);
                        if (s != 0) _px[dstRow + x] = Over(s, _px[dstRow + x]);
                    }
                }
                Painted = true;
            }

            /// <summary>
            /// The integer pixel span covered by <paramref name="area"/> once clipped to
            /// <paramref name="clip"/> and to the surface. Rounded rather than floor/ceil so a
            /// full-screen element lands on exactly the full screen and adjacent elements do not
            /// both claim the shared edge.
            /// </summary>
            private void ToPixels(Rect area, Rect clip, out int x0, out int y0, out int x1, out int y1)
            {
                double left = Math.Max(area.X, clip.X);
                double top = Math.Max(area.Y, clip.Y);
                double right = Math.Min(area.Right, clip.Right);
                double bottom = Math.Min(area.Bottom, clip.Bottom);

                x0 = Math.Max(0, (int)Math.Round(left));
                y0 = Math.Max(0, (int)Math.Round(top));
                x1 = Math.Min(_w, (int)Math.Round(right));
                y1 = Math.Min(_h, (int)Math.Round(bottom));
            }
        }
    }
}
