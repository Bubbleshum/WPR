using System;
using System.Globalization;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Parses the XAML path mini-language — <c>Data="F1 M 0,0 L 10,0 C ... A ... Z"</c> — into a
    /// <see cref="PathGeometry"/>.
    /// </summary>
    /// <remarks>
    /// <para>Silverlight accepts the full SVG command set: <c>M L H V C S Q T A Z</c>, each in
    /// absolute (upper case) and relative (lower case) form, with implicit repetition (extra
    /// coordinate pairs after an <c>M</c> are line-tos) and the optional leading fill rule
    /// <c>F0</c> (EvenOdd, the default) or <c>F1</c> (Nonzero).</para>
    ///
    /// <para><b>Numbers need no separators</b>, and Blend's output relies on it: Flowerz's petals
    /// are <c>F1M24.6729,5.7021C24.6729,3.5911,...</c>, and <c>1-2</c> or <c>.5.5</c> are two
    /// numbers each. Arc flags may also be packed (<c>A10,10 0 0110,10</c>), so a flag is read as
    /// one character.</para>
    ///
    /// <para>A malformed string yields whatever parsed before the fault rather than throwing: the
    /// XAML reader would otherwise drop the whole element, and a partial outline is closer to what
    /// the designer drew than nothing.</para>
    /// </remarks>
    internal static class PathMarkupParser
    {
        public static PathGeometry Parse(string data)
        {
            var geometry = new PathGeometry();
            var p = new Reader(data ?? string.Empty);

            p.SkipSeparators();
            if (p.Peek() == 'F' || p.Peek() == 'f')
            {
                p.Next();
                p.SkipSeparators();
                char rule = p.Next();
                geometry.FillRule = rule == '1' ? FillRule.Nonzero : FillRule.EvenOdd;
            }

            PathFigure? figure = null;
            var current = new Point(0, 0);
            var figureStart = new Point(0, 0);
            Point? lastCubicControl = null;
            Point? lastQuadControl = null;
            char command = '\0';

            try
            {
                while (true)
                {
                    p.SkipSeparators();
                    if (p.AtEnd) break;

                    char c = p.Peek();
                    if (char.IsLetter(c))
                    {
                        command = p.Next();
                    }
                    else if (command == '\0')
                    {
                        break; // numbers before any command
                    }
                    else if (command == 'M') command = 'L';   // implicit line-to after a move
                    else if (command == 'm') command = 'l';
                    else if (command == 'Z' || command == 'z') break;

                    bool rel = char.IsLower(command);
                    char cmd = char.ToUpperInvariant(command);

                    // Any drawing command after a Z (and before an M) starts a new figure at the
                    // point the closed one returned to.
                    if (cmd != 'M' && cmd != 'Z' && figure == null)
                    {
                        figure = NewFigure(geometry, current);
                        figureStart = current;
                    }

                    switch (cmd)
                    {
                        case 'M':
                        {
                            Point pt = p.ReadPoint(rel, current);
                            figure = NewFigure(geometry, pt);
                            current = figureStart = pt;
                            lastCubicControl = lastQuadControl = null;
                            break;
                        }

                        case 'L':
                        {
                            Point pt = p.ReadPoint(rel, current);
                            figure!.Segments.Add(new LineSegment { Point = pt });
                            current = pt;
                            lastCubicControl = lastQuadControl = null;
                            break;
                        }

                        case 'H':
                        {
                            double x = p.ReadNumber();
                            var pt = new Point(rel ? current.X + x : x, current.Y);
                            figure!.Segments.Add(new LineSegment { Point = pt });
                            current = pt;
                            lastCubicControl = lastQuadControl = null;
                            break;
                        }

                        case 'V':
                        {
                            double y = p.ReadNumber();
                            var pt = new Point(current.X, rel ? current.Y + y : y);
                            figure!.Segments.Add(new LineSegment { Point = pt });
                            current = pt;
                            lastCubicControl = lastQuadControl = null;
                            break;
                        }

                        case 'C':
                        {
                            Point c1 = p.ReadPoint(rel, current);
                            Point c2 = p.ReadPoint(rel, current);
                            Point end = p.ReadPoint(rel, current);
                            figure!.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = end });
                            current = end;
                            lastCubicControl = c2;
                            lastQuadControl = null;
                            break;
                        }

                        case 'S':
                        {
                            Point c1 = lastCubicControl.HasValue
                                ? new Point((2 * current.X) - lastCubicControl.Value.X, (2 * current.Y) - lastCubicControl.Value.Y)
                                : current;
                            Point c2 = p.ReadPoint(rel, current);
                            Point end = p.ReadPoint(rel, current);
                            figure!.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = end });
                            current = end;
                            lastCubicControl = c2;
                            lastQuadControl = null;
                            break;
                        }

                        case 'Q':
                        {
                            Point c1 = p.ReadPoint(rel, current);
                            Point end = p.ReadPoint(rel, current);
                            figure!.Segments.Add(new QuadraticBezierSegment { Point1 = c1, Point2 = end });
                            current = end;
                            lastQuadControl = c1;
                            lastCubicControl = null;
                            break;
                        }

                        case 'T':
                        {
                            Point c1 = lastQuadControl.HasValue
                                ? new Point((2 * current.X) - lastQuadControl.Value.X, (2 * current.Y) - lastQuadControl.Value.Y)
                                : current;
                            Point end = p.ReadPoint(rel, current);
                            figure!.Segments.Add(new QuadraticBezierSegment { Point1 = c1, Point2 = end });
                            current = end;
                            lastQuadControl = c1;
                            lastCubicControl = null;
                            break;
                        }

                        case 'A':
                        {
                            double rx = p.ReadNumber();
                            double ry = p.ReadNumber();
                            double angle = p.ReadNumber();
                            bool large = p.ReadFlag();
                            bool sweep = p.ReadFlag();
                            Point end = p.ReadPoint(rel, current);
                            figure!.Segments.Add(new ArcSegment
                            {
                                Point = end,
                                Size = new Size(rx, ry),
                                RotationAngle = angle,
                                IsLargeArc = large,
                                SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                            });
                            current = end;
                            lastCubicControl = lastQuadControl = null;
                            break;
                        }

                        case 'Z':
                            if (figure != null) figure.IsClosed = true;
                            current = figureStart;
                            figure = null;
                            lastCubicControl = lastQuadControl = null;
                            command = '\0';
                            break;

                        default:
                            return geometry; // unknown command: keep what parsed
                    }
                }
            }
            catch (FormatException)
            {
                // Keep the figures that parsed; see the remarks.
            }

            return geometry;
        }

        private static PathFigure NewFigure(PathGeometry geometry, Point start)
        {
            var figure = new PathFigure { StartPoint = start };
            geometry.Figures.Add(figure);
            return figure;
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s) { _s = s; }

            public bool AtEnd => _i >= _s.Length;

            public char Peek() => _i < _s.Length ? _s[_i] : '\0';

            public char Next() => _i < _s.Length ? _s[_i++] : '\0';

            public void SkipSeparators()
            {
                while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
            }

            public Point ReadPoint(bool relative, Point current)
            {
                double x = ReadNumber();
                double y = ReadNumber();
                return relative ? new Point(current.X + x, current.Y + y) : new Point(x, y);
            }

            public bool ReadFlag()
            {
                SkipSeparators();
                char c = Next();
                if (c == '0') return false;
                if (c == '1') return true;
                throw new FormatException("arc flag");
            }

            public double ReadNumber()
            {
                SkipSeparators();
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                bool digits = false, dot = false;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c >= '0' && c <= '9') { digits = true; _i++; }
                    else if (c == '.' && !dot) { dot = true; _i++; }
                    else break;
                }
                if (digits && _i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    int save = _i;
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    if (_i < _s.Length && char.IsDigit(_s[_i]))
                    {
                        while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                    }
                    else
                    {
                        _i = save;
                    }
                }
                if (!digits) throw new FormatException("number expected");
                return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
