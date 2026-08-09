using System;

namespace WPR.SilverlightCompability
{
    public struct Color : IEquatable<Color>
    {
        public byte A { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public static Color FromArgb(byte a, byte r, byte g, byte b)
            => new Color { A = a, R = r, G = g, B = b };

        public static Color FromRgb(byte r, byte g, byte b)
            => new Color { A = 0xFF, R = r, G = g, B = b };

        public bool Equals(Color other) => A == other.A && R == other.R && G == other.G && B == other.B;
        public override bool Equals(object? obj) => obj is Color c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(A, R, G, B);

        public static bool operator ==(Color a, Color b) => a.Equals(b);
        public static bool operator !=(Color a, Color b) => !a.Equals(b);

        public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }
}
