using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// A minimal TrueType reader: character-to-glyph mapping, advance widths, and glyph outlines.
    /// </summary>
    /// <remarks>
    /// <para><b>Why WPR parses fonts itself.</b> The CPU rasteriser that draws a mixed-mode title's
    /// Silverlight page runs with no Avalonia and no Skia — that is what lets it work on the
    /// Android head — so there was no way to draw a glyph and every <c>TextBlock</c> came out
    /// blank. The alternatives were each worse: a baked glyph atlas would mean redistributing a
    /// font (WP7's own Segoe WP is not ours to ship) and would fix the sizes at build time, and a
    /// native rasteriser would add a fourth hand-maintained binary to keep in step with vendored C.
    /// This reads a font already on the device and needs nothing new.</para>
    ///
    /// <para><b>Scope is deliberate.</b> `glyf`-outline fonts only — which covers the system fonts
    /// both heads actually have (Segoe UI on Windows, Roboto/Droid on Android). A CFF/PostScript
    /// OpenType font has its outlines in a `CFF ` table this does not read, and
    /// <see cref="Load"/> answers null for one rather than half-working. No hinting, no kerning,
    /// no ligatures, no bidi, no shaping: WP7 UI text is overwhelmingly short left-to-right
    /// strings, and every one of those features costs far more than it buys here.</para>
    ///
    /// <para>All multi-byte values in a TrueType file are BIG-endian, which is the opposite of
    /// every platform WPR runs on — hence the explicit readers below rather than
    /// <c>BitConverter</c>.</para>
    /// </remarks>
    internal sealed class TrueTypeFont
    {
        private readonly byte[] _data;
        private readonly Dictionary<string, (int Offset, int Length)> _tables;

        private readonly int _glyfOffset;
        private readonly int _locaOffset;
        private readonly int _hmtxOffset;
        private readonly int _cmapOffset;
        private readonly bool _longLoca;
        private readonly int _numGlyphs;
        private readonly int _numHMetrics;

        /// <summary>Design units per em — the scale every other metric is expressed in.</summary>
        public int UnitsPerEm { get; }

        /// <summary>Distance from the baseline to the top of the em box, in design units.</summary>
        public int Ascender { get; }

        /// <summary>Distance from the baseline to the bottom, in design units (negative).</summary>
        public int Descender { get; }

        /// <summary>Extra leading between lines, in design units.</summary>
        public int LineGap { get; }

        private TrueTypeFont(
            byte[] data,
            Dictionary<string, (int, int)> tables,
            int unitsPerEm,
            int ascender,
            int descender,
            int lineGap,
            int glyfOffset,
            int locaOffset,
            int hmtxOffset,
            int cmapOffset,
            bool longLoca,
            int numGlyphs,
            int numHMetrics)
        {
            _data = data;
            _tables = tables;
            UnitsPerEm = unitsPerEm;
            Ascender = ascender;
            Descender = descender;
            LineGap = lineGap;
            _glyfOffset = glyfOffset;
            _locaOffset = locaOffset;
            _hmtxOffset = hmtxOffset;
            _cmapOffset = cmapOffset;
            _longLoca = longLoca;
            _numGlyphs = numGlyphs;
            _numHMetrics = numHMetrics;
        }

        // ---- Big-endian primitives ----

        private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);

        private static short S16(byte[] d, int o) => (short)((d[o] << 8) | d[o + 1]);

        private static uint U32(byte[] d, int o) =>
            (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        /// <summary>
        /// Parses <paramref name="data"/>, or returns null if it is not a `glyf`-outline TrueType
        /// font this can read.
        /// </summary>
        public static TrueTypeFont? Load(byte[]? data)
        {
            try
            {
                if (data == null || data.Length < 12) return null;

                int offset = 0;
                uint tag = U32(data, 0);

                // 'ttcf' — a TrueType Collection. Take the first font in it; on Android several
                // system families ship this way and the first face is the regular one.
                if (tag == 0x74746366u)
                {
                    if (data.Length < 16) return null;
                    offset = (int)U32(data, 12);
                    if (offset < 0 || offset + 12 > data.Length) return null;
                    tag = U32(data, offset);
                }

                // 0x00010000 = TrueType outlines, 'true' = the old Apple variant.
                // 'OTTO' is CFF/PostScript, whose outlines are not in `glyf` — refused.
                if (tag != 0x00010000u && tag != 0x74727565u) return null;

                int numTables = U16(data, offset + 4);
                var tables = new Dictionary<string, (int, int)>(numTables, StringComparer.Ordinal);

                for (int i = 0; i < numTables; i++)
                {
                    int rec = offset + 12 + (i * 16);
                    if (rec + 16 > data.Length) return null;

                    string name = new string(new[]
                    {
                        (char)data[rec], (char)data[rec + 1], (char)data[rec + 2], (char)data[rec + 3]
                    });

                    int tableOffset = (int)U32(data, rec + 8);
                    int tableLength = (int)U32(data, rec + 12);
                    if (tableOffset < 0 || tableOffset > data.Length) continue;

                    tables[name] = (tableOffset, tableLength);
                }

                if (!tables.TryGetValue("head", out var head) ||
                    !tables.TryGetValue("maxp", out var maxp) ||
                    !tables.TryGetValue("hhea", out var hhea) ||
                    !tables.TryGetValue("hmtx", out var hmtx) ||
                    !tables.TryGetValue("cmap", out var cmap) ||
                    !tables.TryGetValue("loca", out var loca) ||
                    !tables.TryGetValue("glyf", out var glyf))
                {
                    return null;
                }

                int unitsPerEm = U16(data, head.Item1 + 18);
                if (unitsPerEm <= 0) return null;

                // 0 = 16-bit loca offsets (stored halved), 1 = 32-bit.
                bool longLoca = S16(data, head.Item1 + 50) != 0;

                int numGlyphs = U16(data, maxp.Item1 + 4);
                int ascender = S16(data, hhea.Item1 + 4);
                int descender = S16(data, hhea.Item1 + 6);
                int lineGap = S16(data, hhea.Item1 + 8);
                int numHMetrics = U16(data, hhea.Item1 + 34);

                return new TrueTypeFont(
                    data, tables, unitsPerEm, ascender, descender, lineGap,
                    glyf.Item1, loca.Item1, hmtx.Item1, cmap.Item1,
                    longLoca, numGlyphs, numHMetrics);
            }
            catch
            {
                // A malformed font is "no font", never a crash: this runs during a game's draw.
                return null;
            }
        }

        // ---- Character mapping ----

        /// <summary>Glyph index for <paramref name="codepoint"/>, or 0 (.notdef) if unmapped.</summary>
        public int GetGlyphIndex(int codepoint)
        {
            try
            {
                int numSubtables = U16(_data, _cmapOffset + 2);
                int best = -1;
                int bestScore = -1;

                for (int i = 0; i < numSubtables; i++)
                {
                    int rec = _cmapOffset + 4 + (i * 8);
                    int platformId = U16(_data, rec);
                    int encodingId = U16(_data, rec + 2);
                    int subOffset = _cmapOffset + (int)U32(_data, rec + 4);

                    // Prefer a full Unicode table over a BMP-only one, and either over anything
                    // else. Windows/Unicode-full (3,10) > Windows/BMP (3,1) > Unicode (0,*).
                    int score =
                        (platformId == 3 && encodingId == 10) ? 3 :
                        (platformId == 3 && encodingId == 1) ? 2 :
                        (platformId == 0) ? 1 : 0;

                    if (score > bestScore) { bestScore = score; best = subOffset; }
                }

                if (best < 0) return 0;

                int format = U16(_data, best);
                return format switch
                {
                    4 => LookupFormat4(best, codepoint),
                    12 => LookupFormat12(best, codepoint),
                    6 => LookupFormat6(best, codepoint),
                    _ => 0,
                };
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Segment mapping to delta values — the standard BMP table.</summary>
        private int LookupFormat4(int table, int codepoint)
        {
            if (codepoint > 0xFFFF) return 0;

            int segCountX2 = U16(_data, table + 6);
            int segCount = segCountX2 / 2;

            int endCodes = table + 14;
            int startCodes = endCodes + segCountX2 + 2;   // +2 skips reservedPad
            int idDeltas = startCodes + segCountX2;
            int idRangeOffsets = idDeltas + segCountX2;

            for (int seg = 0; seg < segCount; seg++)
            {
                int end = U16(_data, endCodes + (seg * 2));
                if (codepoint > end) continue;

                int start = U16(_data, startCodes + (seg * 2));
                if (codepoint < start) return 0;

                int idDelta = S16(_data, idDeltas + (seg * 2));
                int rangeOffsetAt = idRangeOffsets + (seg * 2);
                int idRangeOffset = U16(_data, rangeOffsetAt);

                if (idRangeOffset == 0)
                    return (codepoint + idDelta) & 0xFFFF;

                // The offset is a BYTE count from the idRangeOffset slot itself — the one piece of
                // the format that cannot be read as an ordinary array index.
                int glyphAt = rangeOffsetAt + idRangeOffset + ((codepoint - start) * 2);
                if (glyphAt + 1 >= _data.Length) return 0;

                int glyph = U16(_data, glyphAt);
                return glyph == 0 ? 0 : (glyph + idDelta) & 0xFFFF;
            }

            return 0;
        }

        /// <summary>Segmented coverage — the full-Unicode table.</summary>
        private int LookupFormat12(int table, int codepoint)
        {
            int numGroups = (int)U32(_data, table + 12);
            int lo = 0, hi = numGroups - 1;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int rec = table + 16 + (mid * 12);
                uint startChar = U32(_data, rec);
                uint endChar = U32(_data, rec + 4);

                if (codepoint < startChar) hi = mid - 1;
                else if (codepoint > endChar) lo = mid + 1;
                else return (int)(U32(_data, rec + 8) + (codepoint - startChar));
            }

            return 0;
        }

        /// <summary>Trimmed table mapping — a small contiguous range.</summary>
        private int LookupFormat6(int table, int codepoint)
        {
            int first = U16(_data, table + 6);
            int count = U16(_data, table + 8);
            if (codepoint < first || codepoint >= first + count) return 0;
            return U16(_data, table + 10 + ((codepoint - first) * 2));
        }

        // ---- Metrics ----

        /// <summary>Advance width of <paramref name="glyphIndex"/> in design units.</summary>
        /// <remarks>
        /// The `hmtx` table stores full metrics for the first <c>numberOfHMetrics</c> glyphs only;
        /// every glyph past that shares the LAST recorded advance, which is how monospaced tails
        /// (and CJK blocks) are compressed. Reading past the array instead of clamping is the
        /// classic bug here.
        /// </remarks>
        public int GetAdvanceWidth(int glyphIndex)
        {
            try
            {
                if (_numHMetrics <= 0) return 0;
                int i = glyphIndex < _numHMetrics ? glyphIndex : _numHMetrics - 1;
                return U16(_data, _hmtxOffset + (i * 4));
            }
            catch
            {
                return 0;
            }
        }

        // ---- Outlines ----

        /// <summary>One closed contour of a glyph, in design units, Y up.</summary>
        internal sealed class Contour
        {
            public readonly List<GlyphPoint> Points = new List<GlyphPoint>();
        }

        internal struct GlyphPoint
        {
            public float X;
            public float Y;
            public bool OnCurve;

            public GlyphPoint(float x, float y, bool onCurve)
            {
                X = x;
                Y = y;
                OnCurve = onCurve;
            }
        }

        private (int Start, int End) GlyphRange(int glyphIndex)
        {
            if (glyphIndex < 0 || glyphIndex >= _numGlyphs) return (0, 0);

            if (_longLoca)
            {
                int a = (int)U32(_data, _locaOffset + (glyphIndex * 4));
                int b = (int)U32(_data, _locaOffset + ((glyphIndex + 1) * 4));
                return (a, b);
            }

            // Short form stores offsets halved.
            int s = U16(_data, _locaOffset + (glyphIndex * 2)) * 2;
            int e = U16(_data, _locaOffset + ((glyphIndex + 1) * 2)) * 2;
            return (s, e);
        }

        /// <summary>
        /// Appends the contours of <paramref name="glyphIndex"/> to <paramref name="into"/>,
        /// offset by (<paramref name="dx"/>, <paramref name="dy"/>) design units.
        /// </summary>
        /// <returns>False when the glyph has no outline (a space, or out of range).</returns>
        public bool TryGetOutline(int glyphIndex, List<Contour> into, float dx = 0, float dy = 0, int depth = 0)
        {
            // A composite glyph referencing itself would recurse for ever; real fonts nest one or
            // two deep (an accented letter built from a base plus a mark).
            if (depth > 5) return false;

            try
            {
                (int start, int end) = GlyphRange(glyphIndex);
                if (end <= start) return false;   // empty outline: a space, legitimately

                int g = _glyfOffset + start;
                int numContours = S16(_data, g);

                if (numContours >= 0) return ReadSimpleGlyph(g, numContours, into, dx, dy);
                return ReadCompositeGlyph(g, into, dx, dy, depth);
            }
            catch
            {
                return false;
            }
        }

        private bool ReadSimpleGlyph(int g, int numContours, List<Contour> into, float dx, float dy)
        {
            if (numContours == 0) return false;

            int p = g + 10;   // past numberOfContours + the bounding box

            var endPts = new int[numContours];
            for (int i = 0; i < numContours; i++)
            {
                endPts[i] = U16(_data, p);
                p += 2;
            }

            int pointCount = endPts[numContours - 1] + 1;
            if (pointCount <= 0 || pointCount > 10000) return false;

            int instructionLength = U16(_data, p);
            p += 2 + instructionLength;   // hinting bytecode: skipped, never executed

            // Flags are run-length encoded: bit 3 says the next byte is a repeat count.
            var flags = new byte[pointCount];
            for (int i = 0; i < pointCount;)
            {
                byte f = _data[p++];
                flags[i++] = f;

                if ((f & 0x08) != 0)
                {
                    int repeat = _data[p++];
                    while (repeat-- > 0 && i < pointCount) flags[i++] = f;
                }
            }

            // X then Y, each delta-encoded, each with its own short/long and sign/same encoding.
            var xs = new float[pointCount];
            int x = 0;
            for (int i = 0; i < pointCount; i++)
            {
                byte f = flags[i];
                if ((f & 0x02) != 0)
                {
                    int d = _data[p++];
                    x += ((f & 0x10) != 0) ? d : -d;
                }
                else if ((f & 0x10) == 0)
                {
                    x += S16(_data, p);
                    p += 2;
                }
                // else: X_SAME — the delta is zero, nothing to read.
                xs[i] = x;
            }

            var ys = new float[pointCount];
            int y = 0;
            for (int i = 0; i < pointCount; i++)
            {
                byte f = flags[i];
                if ((f & 0x04) != 0)
                {
                    int d = _data[p++];
                    y += ((f & 0x20) != 0) ? d : -d;
                }
                else if ((f & 0x20) == 0)
                {
                    y += S16(_data, p);
                    p += 2;
                }
                ys[i] = y;
            }

            int first = 0;
            for (int c = 0; c < numContours; c++)
            {
                int last = endPts[c];
                if (last < first) { first = last + 1; continue; }

                var contour = new Contour();
                for (int i = first; i <= last; i++)
                    contour.Points.Add(new GlyphPoint(xs[i] + dx, ys[i] + dy, (flags[i] & 0x01) != 0));

                if (contour.Points.Count > 0) into.Add(contour);
                first = last + 1;
            }

            return into.Count > 0;
        }

        private bool ReadCompositeGlyph(int g, List<Contour> into, float dx, float dy, int depth)
        {
            int p = g + 10;
            bool any = false;

            while (true)
            {
                int flags = U16(_data, p);
                int glyphIndex = U16(_data, p + 2);
                p += 4;

                float a1, a2;
                if ((flags & 0x0001) != 0)      // ARG_1_AND_2_ARE_WORDS
                {
                    a1 = S16(_data, p);
                    a2 = S16(_data, p + 2);
                    p += 4;
                }
                else
                {
                    a1 = (sbyte)_data[p];
                    a2 = (sbyte)_data[p + 1];
                    p += 2;
                }

                // Only the offset form is honoured. ARGS_ARE_XY_VALUES is what real fonts use;
                // the point-matching alternative is vanishingly rare and would need the parent's
                // points, which are not built yet at this stage.
                float offsetX = 0, offsetY = 0;
                if ((flags & 0x0002) != 0)      // ARGS_ARE_XY_VALUES
                {
                    offsetX = a1;
                    offsetY = a2;
                }

                // Component scaling is skipped deliberately: it affects a small number of glyphs
                // (mostly small-caps and some accents) and getting it wrong is worse than a
                // component at its natural size.
                if ((flags & 0x0008) != 0) p += 2;       // WE_HAVE_A_SCALE
                else if ((flags & 0x0040) != 0) p += 4;  // X_AND_Y_SCALE
                else if ((flags & 0x0080) != 0) p += 8;  // TWO_BY_TWO

                if (TryGetOutline(glyphIndex, into, dx + offsetX, dy + offsetY, depth + 1)) any = true;

                if ((flags & 0x0020) == 0) break;        // MORE_COMPONENTS
            }

            return any;
        }
    }
}
