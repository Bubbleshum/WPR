using System;

namespace WPR.SilverlightCompability
{
    public struct Point : IEquatable<Point>
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point(double x, double y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(Point other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Point p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y);

        public static bool operator ==(Point a, Point b) => a.Equals(b);
        public static bool operator !=(Point a, Point b) => !a.Equals(b);

        public override string ToString() => $"{X},{Y}";
    }
}
