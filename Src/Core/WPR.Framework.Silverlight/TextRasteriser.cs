using System;
using System.Collections.Generic;
using System.IO;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Draws and measures text for <see cref="SoftwareVisualRasteriser"/>, with no Avalonia and no
    /// GraphicsDevice.
    /// </summary>
    /// <remarks>
    /// <para>Rasterises glyph outlines from a font already installed on the device (see
    /// <see cref="TrueTypeFont"/> for why WPR parses them itself). Output is anti-aliased 8-bit
    /// coverage, blended into the same premultiplied buffer the rest of the rasteriser paints to.
    /// </para>
    ///
    /// <para><b>Glyphs are cached per (glyph, size).</b> A WP7 page redraws only when its
    /// signature changes, but a page that animates re-rasterises its whole tree each time, and
    /// scan-converting an outline is far more expensive than blitting the result. The cache is
    /// bounded and cleared with the rest of the per-launch state.</para>
    ///
    /// <para><b>Not implemented, deliberately:</b> kerning, ligatures, shaping, bidirectional text
    /// and vertical writing. WP7 UI text is short left-to-right runs; each of those features costs
    /// more than it returns here. Text wraps on explicit newlines only.</para>
    /// </remarks>
    internal static class TextRasteriser
    {
        /// <summary>Vertical sub-samples per pixel row when scan-converting. 4 is the usual
        /// quality/cost knee: 2 visibly stairsteps diagonals, 8 is not distinguishable from 4 at
        /// UI sizes.</summary>
        private const int SubSamples = 4;

        /// <summary>Beyond this the cache is cleared wholesale rather than evicted cleverly — a
        /// page cycling through thousands of distinct glyph/size pairs is not a real WP7 page.</summary>
        private const int MaxCachedGlyphs = 4096;

        private static readonly object _gate = new object();
        private static TrueTypeFont? _font;
        private static bool _fontResolved;
        private static readonly Dictionary<long, Glyph> _glyphs = new Dictionary<long, Glyph>();

        /// <summary>A rasterised glyph: 8-bit coverage plus where to put it relative to the pen.</summary>
        private sealed class Glyph
        {
            public byte[] Coverage = Array.Empty<byte>();
            public int Width;
            public int Height;

            /// <summary>Pixels from the pen position to the left edge of the bitmap.</summary>
            public int Left;

            /// <summary>Pixels from the baseline UP to the top edge of the bitmap.</summary>
            public int Top;

            /// <summary>Pen movement for this glyph, in pixels.</summary>
            public float Advance;
        }

        /// <summary>Drops every cached glyph and the resolved font. Called at teardown.</summary>
        public static void ResetForNewLaunch()
        {
            lock (_gate)
            {
                _glyphs.Clear();
                _font = null;
                _fontResolved = false;
            }
        }

        /// <summary>True when a usable system font was found; false means text cannot be drawn.</summary>
        public static bool IsAvailable => ResolveFont() != null;

        // ---- Font resolution ----

        /// <summary>
        /// Candidate font files, best first. The list is ordered by how close each is to WP7's own
        /// Segoe WP, which is not redistributable and is therefore never shipped here.
        /// </summary>
        private static IEnumerable<string> CandidateFontPaths()
        {
            // Windows. Segoe UI is the closest relative of Segoe WP and is present on every
            // supported version.
            string? windir = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(windir))
            {
                string fonts = Path.Combine(windir!, "Fonts");
                yield return Path.Combine(fonts, "segoeui.ttf");
                yield return Path.Combine(fonts, "tahoma.ttf");
                yield return Path.Combine(fonts, "arial.ttf");
                yield return Path.Combine(fonts, "verdana.ttf");
            }

            // Android. Roboto is the system UI face; the Droid names are the older equivalents and
            // are still present on some images.
            yield return "/system/fonts/Roboto-Regular.ttf";
            yield return "/system/fonts/RobotoStatic-Regular.ttf";
            yield return "/system/fonts/DroidSans.ttf";
            yield return "/system/fonts/NotoSans-Regular.ttf";

            // Desktop Linux, for the bare game-host harness.
            yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        }

        private static TrueTypeFont? ResolveFont()
        {
            lock (_gate)
            {
                if (_fontResolved) return _font;
                _fontResolved = true;

                foreach (string path in CandidateFontPaths())
                {
                    try
                    {
                        if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                        TrueTypeFont? font = TrueTypeFont.Load(File.ReadAllBytes(path));
                        if (font == null) continue;   // e.g. a CFF/OpenType face

                        _font = font;
                        System.Diagnostics.Trace.WriteLine($"[wpr-text] using font '{path}'.");
                        return _font;
                    }
                    catch
                    {
                        // Unreadable candidate — try the next.
                    }
                }

                System.Diagnostics.Trace.WriteLine(
                    "[wpr-text] no usable system TrueType font found; text will not be drawn.");
                return null;
            }
        }

        // ---- Measurement ----

        /// <summary>Width of <paramref name="text"/> at <paramref name="fontSize"/>, in pixels.</summary>
        private static double MeasureRun(TrueTypeFont font, string text, double fontSize)
        {
            double scale = fontSize / font.UnitsPerEm;
            double w = 0;

            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n') continue;
                w += font.GetAdvanceWidth(font.GetGlyphIndex(ch)) * scale;
            }

            return w;
        }

        /// <summary>
        /// Breaks <paramref name="text"/> into display lines: always at explicit newlines, and
        /// additionally at word boundaries when <paramref name="maxWidth"/> is finite and positive.
        /// </summary>
        /// <remarks>
        /// <b>Measure and draw share this</b>, which is the point of it existing rather than each
        /// doing its own splitting. Layout that disagrees with rendering by even one line is how a
        /// caption ends up clipped or floating away from its box, and the two drift apart the
        /// moment they are written twice.
        /// <para>A single word wider than the line is left overlong rather than broken mid-word:
        /// WP7 UI strings are short, and a hard break inside a word looks like corruption.</para>
        /// </remarks>
        private static List<string> LayoutLines(TrueTypeFont font, string text, double fontSize, double maxWidth)
        {
            var lines = new List<string>();
            bool wrap = maxWidth > 0 && !double.IsInfinity(maxWidth) && !double.IsNaN(maxWidth);

            foreach (string paragraph in text.Replace("\r", string.Empty).Split('\n'))
            {
                if (!wrap)
                {
                    lines.Add(paragraph);
                    continue;
                }

                if (MeasureRun(font, paragraph, fontSize) <= maxWidth)
                {
                    lines.Add(paragraph);
                    continue;
                }

                string current = string.Empty;
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;

                    if (current.Length > 0 && MeasureRun(font, candidate, fontSize) > maxWidth)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else
                    {
                        current = candidate;
                    }
                }

                lines.Add(current);
            }

            return lines;
        }

        /// <summary>
        /// The pixel size of <paramref name="text"/> at <paramref name="fontSize"/>, honouring
        /// explicit newlines and — when <paramref name="maxWidth"/> is finite — word wrapping.
        /// Returns (0,0) when there is nothing to draw.
        /// </summary>
        public static void Measure(
            string? text,
            double fontSize,
            out double width,
            out double height,
            double maxWidth = double.PositiveInfinity)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(text) || fontSize <= 0) return;

            TrueTypeFont? font = ResolveFont();
            if (font == null) return;

            List<string> lines = LayoutLines(font, text!, fontSize, maxWidth);

            double widest = 0;
            foreach (string line in lines)
            {
                double w = MeasureRun(font, line, fontSize);
                if (w > widest) widest = w;
            }

            width = widest;
            height = GetLineHeight(fontSize) * Math.Max(1, lines.Count);
        }

        /// <summary>The baseline offset from the top of a line box, in pixels.</summary>
        public static double GetBaseline(double fontSize)
        {
            TrueTypeFont? font = ResolveFont();
            if (font == null) return fontSize;
            return font.Ascender * (fontSize / font.UnitsPerEm);
        }

        /// <summary>The height of one line at <paramref name="fontSize"/>, in pixels.</summary>
        public static double GetLineHeight(double fontSize)
        {
            TrueTypeFont? font = ResolveFont();
            if (font == null) return fontSize * 1.2;

            double h = (font.Ascender - font.Descender + font.LineGap) * (fontSize / font.UnitsPerEm);
            return h > 0 ? h : fontSize * 1.2;
        }

        // ---- Drawing ----

        /// <summary>
        /// Blends <paramref name="text"/> into <paramref name="dest"/> with its top-left at
        /// (<paramref name="originX"/>, <paramref name="originY"/>), clipped to
        /// <paramref name="clip"/>.
        /// </summary>
        /// <param name="premultipliedColor">
        /// The text colour, already premultiplied and packed the way XNA's <c>Color</c> packs it
        /// (R in the low byte) — the same convention the rest of the rasteriser's buffer uses.
        /// </param>
        /// <param name="layoutWidth">
        /// The width text is laid out in: the wrap width, and what <paramref name="alignment"/> is
        /// measured against. Infinity means no wrapping and left alignment.
        /// </param>
        public static void Draw(
            uint[] dest,
            int destWidth,
            int destHeight,
            string? text,
            double fontSize,
            double originX,
            double originY,
            uint premultipliedColor,
            Rect clip,
            double layoutWidth = double.PositiveInfinity,
            TextAlignment alignment = TextAlignment.Left)
        {
            if (string.IsNullOrEmpty(text) || fontSize <= 0) return;

            TrueTypeFont? font = ResolveFont();
            if (font == null) return;

            double lineHeight = GetLineHeight(fontSize);
            double baseline = GetBaseline(fontSize);
            double scale = fontSize / font.UnitsPerEm;

            List<string> lines = LayoutLines(font, text!, fontSize, layoutWidth);
            bool canAlign = layoutWidth > 0 && !double.IsInfinity(layoutWidth) && !double.IsNaN(layoutWidth);

            double penY = originY + baseline;

            foreach (string line in lines)
            {
                double penX = originX;

                if (canAlign && alignment != TextAlignment.Left)
                {
                    double slack = layoutWidth - MeasureRun(font, line, fontSize);
                    if (slack > 0)
                        penX += alignment == TextAlignment.Center ? slack / 2 : slack;
                }

                foreach (char ch in line)
                {
                    if (ch == '\r' || ch == '\n') continue;

                    int glyphIndex = font.GetGlyphIndex(ch);
                    Glyph? glyph = GetGlyph(font, glyphIndex, fontSize);

                    if (glyph != null && glyph.Width > 0 && glyph.Height > 0)
                    {
                        BlendGlyph(
                            dest, destWidth, destHeight, glyph,
                            (int)Math.Round(penX) + glyph.Left,
                            (int)Math.Round(penY) - glyph.Top,
                            premultipliedColor, clip);
                    }

                    penX += font.GetAdvanceWidth(glyphIndex) * scale;
                }

                penY += lineHeight;
            }
        }

        private static void BlendGlyph(
            uint[] dest, int destWidth, int destHeight,
            Glyph glyph, int atX, int atY,
            uint premultipliedColor, Rect clip)
        {
            int clipLeft = Math.Max(0, (int)Math.Floor(clip.X));
            int clipTop = Math.Max(0, (int)Math.Floor(clip.Y));
            int clipRight = Math.Min(destWidth, (int)Math.Ceiling(clip.X + clip.Width));
            int clipBottom = Math.Min(destHeight, (int)Math.Ceiling(clip.Y + clip.Height));

            uint srcR = premultipliedColor & 0xFF;
            uint srcG = (premultipliedColor >> 8) & 0xFF;
            uint srcB = (premultipliedColor >> 16) & 0xFF;
            uint srcA = (premultipliedColor >> 24) & 0xFF;

            for (int gy = 0; gy < glyph.Height; gy++)
            {
                int py = atY + gy;
                if (py < clipTop || py >= clipBottom) continue;

                int rowBase = py * destWidth;
                int covBase = gy * glyph.Width;

                for (int gx = 0; gx < glyph.Width; gx++)
                {
                    int px = atX + gx;
                    if (px < clipLeft || px >= clipRight) continue;

                    byte coverage = glyph.Coverage[covBase + gx];
                    if (coverage == 0) continue;

                    // Scale the already-premultiplied source by coverage, then source-over.
                    uint a = (srcA * coverage) / 255;
                    if (a == 0) continue;

                    uint r = (srcR * coverage) / 255;
                    uint g = (srcG * coverage) / 255;
                    uint b = (srcB * coverage) / 255;

                    uint under = dest[rowBase + px];
                    uint inv = 255 - a;

                    uint outR = r + (((under & 0xFF) * inv) / 255);
                    uint outG = g + ((((under >> 8) & 0xFF) * inv) / 255);
                    uint outB = b + ((((under >> 16) & 0xFF) * inv) / 255);
                    uint outA = a + ((((under >> 24) & 0xFF) * inv) / 255);

                    dest[rowBase + px] =
                        (outR & 0xFF) | ((outG & 0xFF) << 8) | ((outB & 0xFF) << 16) | ((outA & 0xFF) << 24);
                }
            }
        }

        // ---- Glyph rasterisation ----

        private static Glyph? GetGlyph(TrueTypeFont font, int glyphIndex, double fontSize)
        {
            // Quantise the size into the key: a storyboard animating a font size by fractions of a
            // pixel would otherwise mint a new cache entry every frame.
            int sizeKey = (int)Math.Round(fontSize * 4);
            long key = ((long)sizeKey << 32) | (uint)glyphIndex;

            lock (_gate)
            {
                if (_glyphs.TryGetValue(key, out Glyph? cached)) return cached;
                if (_glyphs.Count >= MaxCachedGlyphs) _glyphs.Clear();
            }

            Glyph glyph = RasteriseGlyph(font, glyphIndex, sizeKey / 4.0);

            lock (_gate)
            {
                _glyphs[key] = glyph;
            }

            return glyph;
        }

        private static Glyph RasteriseGlyph(TrueTypeFont font, int glyphIndex, double fontSize)
        {
            var glyph = new Glyph
            {
                Advance = (float)(font.GetAdvanceWidth(glyphIndex) * (fontSize / font.UnitsPerEm)),
            };

            var contours = new List<TrueTypeFont.Contour>();
            if (!font.TryGetOutline(glyphIndex, contours) || contours.Count == 0)
                return glyph;   // no ink (a space) — advance only

            float scale = (float)(fontSize / font.UnitsPerEm);

            // Flatten to polylines in pixel space, Y DOWN (design units are Y up).
            var edges = new List<(float X0, float Y0, float X1, float Y1)>();
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (TrueTypeFont.Contour contour in contours)
            {
                var pts = Flatten(contour, scale);
                if (pts.Count < 2) continue;

                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Count];   // closed
                    if (a.Y != b.Y) edges.Add((a.X, a.Y, b.X, b.Y));

                    if (a.X < minX) minX = a.X;
                    if (a.X > maxX) maxX = a.X;
                    if (a.Y < minY) minY = a.Y;
                    if (a.Y > maxY) maxY = a.Y;
                }
            }

            if (edges.Count == 0 || minX > maxX) return glyph;

            int left = (int)Math.Floor(minX);
            int top = (int)Math.Floor(minY);
            int width = (int)Math.Ceiling(maxX) - left + 1;
            int height = (int)Math.Ceiling(maxY) - top + 1;

            if (width <= 0 || height <= 0 || width > 2048 || height > 2048) return glyph;

            var coverage = new byte[width * height];
            var accum = new float[width];
            var crossings = new List<(float X, int Dir)>();

            for (int row = 0; row < height; row++)
            {
                Array.Clear(accum, 0, width);

                for (int s = 0; s < SubSamples; s++)
                {
                    float sampleY = top + row + ((s + 0.5f) / SubSamples);

                    crossings.Clear();
                    foreach (var e in edges)
                    {
                        float y0 = e.Y0, y1 = e.Y1;
                        int dir = 1;
                        float x0 = e.X0, x1 = e.X1;

                        if (y0 > y1)
                        {
                            (y0, y1) = (y1, y0);
                            (x0, x1) = (x1, x0);
                            dir = -1;
                        }

                        // Half-open in Y so a vertex shared by two edges is counted once.
                        if (sampleY < y0 || sampleY >= y1) continue;

                        float t = (sampleY - y0) / (y1 - y0);
                        crossings.Add((x0 + (t * (x1 - x0)), dir));
                    }

                    if (crossings.Count < 2) continue;
                    crossings.Sort((p, q) => p.X.CompareTo(q.X));

                    // Non-zero winding: a span is inside while the accumulated direction is not 0.
                    int winding = 0;
                    for (int i = 0; i < crossings.Count - 1; i++)
                    {
                        winding += crossings[i].Dir;
                        if (winding == 0) continue;

                        AddSpan(accum, width, crossings[i].X - left, crossings[i + 1].X - left,
                                1.0f / SubSamples);
                    }
                }

                int rowBase = row * width;
                for (int x = 0; x < width; x++)
                {
                    float c = accum[x];
                    if (c <= 0) continue;
                    coverage[rowBase + x] = (byte)(c >= 1f ? 255 : (int)((c * 255) + 0.5f));
                }
            }

            glyph.Coverage = coverage;
            glyph.Width = width;
            glyph.Height = height;
            glyph.Left = left;
            glyph.Top = -top;   // `top` is above the baseline, so negative in Y-down space
            return glyph;
        }

        /// <summary>
        /// Adds <paramref name="weight"/> coverage across [<paramref name="x0"/>,
        /// <paramref name="x1"/>), giving the partially covered end pixels their exact fraction.
        /// </summary>
        private static void AddSpan(float[] accum, int width, float x0, float x1, float weight)
        {
            if (x1 <= x0) return;
            if (x1 <= 0 || x0 >= width) return;

            if (x0 < 0) x0 = 0;
            if (x1 > width) x1 = width;

            int first = (int)Math.Floor(x0);
            int last = (int)Math.Ceiling(x1) - 1;

            if (first == last)
            {
                accum[first] += (x1 - x0) * weight;
                return;
            }

            accum[first] += (first + 1 - x0) * weight;
            for (int x = first + 1; x < last; x++) accum[x] += weight;
            if (last < width) accum[last] += (x1 - last) * weight;
        }

        /// <summary>
        /// Turns a TrueType contour into a closed polyline in pixel space, Y down.
        /// </summary>
        /// <remarks>
        /// TrueType contours are quadratic B-splines in which CONSECUTIVE OFF-CURVE POINTS IMPLY an
        /// on-curve point at their midpoint. Treating off-curve points as ordinary vertices — the
        /// obvious reading — makes every curve visibly polygonal and pulls it inside the true
        /// outline, which at UI sizes looks like a bad font rather than a bad renderer.
        /// </remarks>
        private static List<(float X, float Y)> Flatten(TrueTypeFont.Contour contour, float scale)
        {
            var src = contour.Points;
            var result = new List<(float X, float Y)>();
            if (src.Count == 0) return result;

            (float X, float Y) P(TrueTypeFont.GlyphPoint p) => (p.X * scale, -p.Y * scale);

            // The contour may begin on an off-curve point; start from a real on-curve point so the
            // walk below always has somewhere to draw from.
            int start = -1;
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i].OnCurve) { start = i; break; }
            }

            (float X, float Y) current;
            if (start < 0)
            {
                // Every point is off-curve — a shape made entirely of curves. The implied start is
                // the midpoint of the last and first control points.
                var a = P(src[src.Count - 1]);
                var b = P(src[0]);
                current = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                start = 0;
            }
            else
            {
                current = P(src[start]);
                start += 1;
            }

            result.Add(current);

            for (int n = 0; n < src.Count; n++)
            {
                var pt = src[(start + n) % src.Count];
                var p = P(pt);

                if (pt.OnCurve)
                {
                    result.Add(p);
                    current = p;
                    continue;
                }

                // Off-curve: this is a control point. Its end is the next on-curve point, or the
                // implied midpoint when the next one is also off-curve.
                var nextPt = src[(start + n + 1) % src.Count];
                var next = P(nextPt);
                var end = nextPt.OnCurve ? next : ((p.X + next.X) / 2, (p.Y + next.Y) / 2);

                EmitQuadratic(result, current, p, end);
                current = end;
                if (nextPt.OnCurve) n++;   // consumed
            }

            return result;
        }

        private static void EmitQuadratic(
            List<(float X, float Y)> into,
            (float X, float Y) from,
            (float X, float Y) control,
            (float X, float Y) to)
        {
            // Segment count from the control polygon's length, so a big curve gets more steps than
            // a small one and a glyph at 72px is as smooth as one at 18px.
            float d =
                Math.Abs(control.X - from.X) + Math.Abs(control.Y - from.Y) +
                Math.Abs(to.X - control.X) + Math.Abs(to.Y - control.Y);

            int steps = (int)(d / 3f);
            if (steps < 2) steps = 2;
            if (steps > 32) steps = 32;

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float u = 1 - t;

                into.Add((
                    (u * u * from.X) + (2 * u * t * control.X) + (t * t * to.X),
                    (u * u * from.Y) + (2 * u * t * control.Y) + (t * t * to.Y)));
            }
        }
    }
}
