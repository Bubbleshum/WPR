using System;

namespace WPR.SilverlightCompability
{
    public struct Rect : IEquatable<Rect>
    {
        public static Rect Empty => new(0, 0, 0, 0);

        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public Rect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public Rect(Point location, Size size) : this(location.X, location.Y, size.Width, size.Height) { }

        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public bool Equals(Rect other) =>
            X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

        public override bool Equals(object? obj) => obj is Rect r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

        public static bool operator ==(Rect a, Rect b) => a.Equals(b);
        public static bool operator !=(Rect a, Rect b) => !a.Equals(b);

        public override string ToString() => $"{X},{Y},{Width},{Height}";
    }
}
