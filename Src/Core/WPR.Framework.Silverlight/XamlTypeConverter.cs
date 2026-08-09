using System;
using System.Collections.Generic;
using System.Globalization;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Converts XAML attribute string values to target CLR types. Handles primitives,
    /// enums, and the structured Silverlight value types (Color, Brush, Thickness,
    /// GridLength, CornerRadius). Throws XamlParseException on failure.
    /// </summary>
    internal static class XamlTypeConverter
    {
        private static readonly Dictionary<string, Color> NamedColors =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Transparent"] = Color.FromArgb(0, 0, 0, 0),
                ["Black"] = Color.FromRgb(0, 0, 0),
                ["White"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
                ["Red"] = Color.FromRgb(0xFF, 0, 0),
                ["Green"] = Color.FromRgb(0, 0x80, 0),
                ["Lime"] = Color.FromRgb(0, 0xFF, 0),
                ["Blue"] = Color.FromRgb(0, 0, 0xFF),
                ["Yellow"] = Color.FromRgb(0xFF, 0xFF, 0),
                ["Cyan"] = Color.FromRgb(0, 0xFF, 0xFF),
                ["Aqua"] = Color.FromRgb(0, 0xFF, 0xFF),
                ["Magenta"] = Color.FromRgb(0xFF, 0, 0xFF),
                ["Fuchsia"] = Color.FromRgb(0xFF, 0, 0xFF),
                ["Gray"] = Color.FromRgb(0x80, 0x80, 0x80),
                ["DarkGray"] = Color.FromRgb(0xA9, 0xA9, 0xA9),
                ["LightGray"] = Color.FromRgb(0xD3, 0xD3, 0xD3),
                ["Silver"] = Color.FromRgb(0xC0, 0xC0, 0xC0),
                ["Orange"] = Color.FromRgb(0xFF, 0xA5, 0),
                ["Purple"] = Color.FromRgb(0x80, 0, 0x80),
                ["Brown"] = Color.FromRgb(0xA5, 0x2A, 0x2A),
                ["Pink"] = Color.FromRgb(0xFF, 0xC0, 0xCB),
                ["Gold"] = Color.FromRgb(0xFF, 0xD7, 0),
                ["Navy"] = Color.FromRgb(0, 0, 0x80),
                ["Teal"] = Color.FromRgb(0, 0x80, 0x80),
                ["Olive"] = Color.FromRgb(0x80, 0x80, 0),
                ["Maroon"] = Color.FromRgb(0x80, 0, 0),
            };

        public static object? Convert(string? raw, Type targetType)
        {
            if (raw == null) return null;
            string s = raw.Trim();

            if (targetType == typeof(string)) return s;

            // Nullable<T>: peel and recurse
            Type? underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null) return Convert(s, underlying);

            if (targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, s, true, out var e)) return e;
                throw new XamlParseException($"'{s}' is not a valid {targetType.Name}");
            }

            if (targetType == typeof(bool)) return bool.Parse(s);
            if (targetType == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
            {
                if (s.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return double.NaN;
                if (s.Equals("NaN", StringComparison.OrdinalIgnoreCase)) return double.NaN;
                if (s.Equals("Infinity", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
                return double.Parse(s, CultureInfo.InvariantCulture);
            }
            if (targetType == typeof(float)) return float.Parse(s, CultureInfo.InvariantCulture);

            if (targetType == typeof(Color)) return ParseColor(s);
            if (targetType == typeof(Thickness)) return ParseThickness(s);
            if (targetType == typeof(CornerRadius)) return ParseCornerRadius(s);
            if (targetType == typeof(GridLength)) return ParseGridLength(s);
            if (targetType == typeof(Point)) return ParsePoint(s);
            if (targetType == typeof(Size)) return ParseSize(s);
            if (targetType == typeof(Rect)) return ParseRect(s);
            if (targetType == typeof(Duration)) return ParseDuration(s);
            if (targetType == typeof(TimeSpan)) return TimeSpan.Parse(s, CultureInfo.InvariantCulture);
            if (targetType == typeof(FontWeight)) return ParseFontWeight(s);
            if (targetType == typeof(PropertyPath)) return new PropertyPath(s);
            if (targetType == typeof(KeyTime)) return ParseKeyTime(s);

            // CacheMode — XAML strings "BitmapCache" / "None" produce concrete instances.
            if (targetType == typeof(CacheMode) || typeof(CacheMode).IsAssignableFrom(targetType))
            {
                if (s.Equals("BitmapCache", StringComparison.OrdinalIgnoreCase))
                    return new BitmapCache();
                return null; // unknown / "None"
            }

            // System.Type — Style TargetType="Foo" etc. Best-effort lookup across
            // loaded assemblies; falls back to null (permissive) if not found.
            if (targetType == typeof(Type))
                return ResolveTypeByName(s);

            if (targetType == typeof(Brush) || typeof(Brush).IsAssignableFrom(targetType))
                return new SolidColorBrush(ParseColor(s));

            // Image.Source="foo.png" — wrap the raw path; Image.GetAvaloniaBitmap
            // resolves it lazily via File.Exists when first rendered. Anything
            // deriving from ImageSource (BitmapImage, BitmapSource, WriteableBitmap)
            // hits this branch too — for those, code paths typically construct the
            // instance directly rather than going through string-attribute XAML, but
            // a string fallback is the least-surprising option.
            if (targetType == typeof(ImageSource) || typeof(ImageSource).IsAssignableFrom(targetType))
                return new ImageSource(s);

            if (targetType == typeof(object)) return s;

            // Permissive: an unknown target type for a XAML attribute is almost always
            // a fancy property type we don't model (Cursor, FontFamily-as-object, custom
            // markup-typed property, etc.). Logging + returning null beats killing the
            // entire element load — the property just stays at its default.
            Console.WriteLine(
                $"[XamlTypeConverter] No converter for '{targetType.FullName}' (value: '{s}'); using null/default");
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        }

        public static Point ParsePoint(string s)
        {
            string[] parts = SplitNumeric(s);
            if (parts.Length != 2)
                throw new XamlParseException($"Invalid Point '{s}' (expected 'X,Y')");
            return new Point(
                double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        public static Size ParseSize(string s)
        {
            string[] parts = SplitNumeric(s);
            if (parts.Length != 2)
                throw new XamlParseException($"Invalid Size '{s}' (expected 'W,H')");
            return new Size(
                double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        public static Rect ParseRect(string s)
        {
            string[] parts = SplitNumeric(s);
            if (parts.Length != 4)
                throw new XamlParseException($"Invalid Rect '{s}' (expected 'X,Y,W,H')");
            return new Rect(
                double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture));
        }

        public static Duration ParseDuration(string s)
        {
            if (s.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
                return Duration.Automatic;
            if (s.Equals("Forever", StringComparison.OrdinalIgnoreCase))
                return Duration.Forever;
            // "0:0:1.5" — hh:mm:ss[.ff]
            return new Duration(TimeSpan.Parse(s, CultureInfo.InvariantCulture));
        }

        public static KeyTime ParseKeyTime(string s)
        {
            if (s.Equals("Uniform", StringComparison.OrdinalIgnoreCase)) return KeyTime.Uniform;
            if (s.Equals("Paced", StringComparison.OrdinalIgnoreCase))   return KeyTime.Paced;
            if (s.EndsWith("%", StringComparison.Ordinal)
                && double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            {
                return KeyTime.FromPercent(pct / 100.0);
            }
            return KeyTime.FromTimeSpan(TimeSpan.Parse(s, CultureInfo.InvariantCulture));
        }

        private static Type? ResolveTypeByName(string s)
        {
            // Strip namespace prefix ("controls:Panorama" → "Panorama") because the
            // type's actual namespace is opaque to us here; we just scan loaded asms.
            int colon = s.IndexOf(':');
            string simpleName = colon >= 0 ? s.Substring(colon + 1) : s;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (t.Name == simpleName) return t;
                    }
                }
                catch { /* unloadable assembly — skip */ }
            }
            return null;
        }

        public static FontWeight ParseFontWeight(string s)
        {
            // Named (Bold, Normal, Thin, ...) or numeric (700)
            switch (s)
            {
                case "Thin":       return FontWeights.Thin;
                case "ExtraLight": return FontWeights.ExtraLight;
                case "Light":      return FontWeights.Light;
                case "Normal":     return FontWeights.Normal;
                case "Medium":     return FontWeights.Medium;
                case "SemiBold":   return FontWeights.SemiBold;
                case "Bold":       return FontWeights.Bold;
                case "ExtraBold":  return FontWeights.ExtraBold;
                case "Black":      return FontWeights.Black;
            }
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return new FontWeight(n);
            throw new XamlParseException($"Invalid FontWeight '{s}'");
        }

        public static Color ParseColor(string s)
        {
            if (NamedColors.TryGetValue(s, out var named)) return named;

            if (s.StartsWith("#", StringComparison.Ordinal))
            {
                string hex = s.Substring(1);
                switch (hex.Length)
                {
                    case 6: // RRGGBB
                        return Color.FromRgb(
                            ParseHex(hex, 0, 2),
                            ParseHex(hex, 2, 2),
                            ParseHex(hex, 4, 2));
                    case 8: // AARRGGBB
                        return Color.FromArgb(
                            ParseHex(hex, 0, 2),
                            ParseHex(hex, 2, 2),
                            ParseHex(hex, 4, 2),
                            ParseHex(hex, 6, 2));
                    case 3: // RGB
                        return Color.FromRgb(
                            (byte)(ParseHex(hex, 0, 1) * 0x11),
                            (byte)(ParseHex(hex, 1, 1) * 0x11),
                            (byte)(ParseHex(hex, 2, 1) * 0x11));
                    default:
                        throw new XamlParseException($"Invalid hex color '{s}'");
                }
            }

            throw new XamlParseException($"Cannot parse color '{s}'");
        }

        private static byte ParseHex(string s, int offset, int len) =>
            (byte)int.Parse(s.AsSpan(offset, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public static Thickness ParseThickness(string s)
        {
            string[] parts = SplitNumeric(s);
            switch (parts.Length)
            {
                case 1:
                    double u = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    return new Thickness(u);
                case 2:
                    double h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    double v = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    return new Thickness(h, v, h, v);
                case 4:
                    return new Thickness(
                        double.Parse(parts[0], CultureInfo.InvariantCulture),
                        double.Parse(parts[1], CultureInfo.InvariantCulture),
                        double.Parse(parts[2], CultureInfo.InvariantCulture),
                        double.Parse(parts[3], CultureInfo.InvariantCulture));
                default:
                    throw new XamlParseException($"Invalid Thickness '{s}'");
            }
        }

        public static CornerRadius ParseCornerRadius(string s)
        {
            string[] parts = SplitNumeric(s);
            switch (parts.Length)
            {
                case 1:
                    double u = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    return new CornerRadius(u);
                case 4:
                    return new CornerRadius(
                        double.Parse(parts[0], CultureInfo.InvariantCulture),
                        double.Parse(parts[1], CultureInfo.InvariantCulture),
                        double.Parse(parts[2], CultureInfo.InvariantCulture),
                        double.Parse(parts[3], CultureInfo.InvariantCulture));
                default:
                    throw new XamlParseException($"Invalid CornerRadius '{s}'");
            }
        }

        public static GridLength ParseGridLength(string s)
        {
            if (s.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                return GridLength.Auto;

            if (s == "*") return new GridLength(1.0, GridUnitType.Star);

            if (s.EndsWith("*", StringComparison.Ordinal))
            {
                string num = s.Substring(0, s.Length - 1);
                double v = double.Parse(num, CultureInfo.InvariantCulture);
                return new GridLength(v, GridUnitType.Star);
            }

            return new GridLength(double.Parse(s, CultureInfo.InvariantCulture), GridUnitType.Pixel);
        }

        private static string[] SplitNumeric(string s) =>
            s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
