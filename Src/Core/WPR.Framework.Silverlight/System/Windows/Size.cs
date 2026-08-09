using System;

namespace WPR.SilverlightCompability
{
    public struct Size : IEquatable<Size>
    {
        public static Size Empty => new(0, 0);

        public double Width { get; set; }
        public double Height { get; set; }

        public Size(double width, double height)
        {
            Width = width;
            Height = height;
        }

        public bool IsEmpty => Width == 0 && Height == 0;

        public bool Equals(Size other) => Width == other.Width && Height == other.Height;
        public override bool Equals(object? obj) => obj is Size s && Equals(s);
        public override int GetHashCode() => HashCode.Combine(Width, Height);

        public static bool operator ==(Size a, Size b) => a.Equals(b);
        public static bool operator !=(Size a, Size b) => !a.Equals(b);

        public override string ToString() => $"{Width},{Height}";
    }
}
