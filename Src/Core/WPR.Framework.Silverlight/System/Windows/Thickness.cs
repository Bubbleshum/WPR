using System;

namespace WPR.SilverlightCompability
{
    public struct Thickness : IEquatable<Thickness>
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }

        public Thickness(double uniformLength)
        {
            Left = Top = Right = Bottom = uniformLength;
        }

        public Thickness(double left, double top, double right, double bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public bool Equals(Thickness other) =>
            Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override bool Equals(object? obj) => obj is Thickness t && Equals(t);
        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

        public static bool operator ==(Thickness a, Thickness b) => a.Equals(b);
        public static bool operator !=(Thickness a, Thickness b) => !a.Equals(b);

        public override string ToString() => $"{Left},{Top},{Right},{Bottom}";
    }
}
