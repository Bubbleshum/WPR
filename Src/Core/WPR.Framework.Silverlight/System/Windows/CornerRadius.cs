using System;

namespace WPR.SilverlightCompability
{
    public struct CornerRadius : IEquatable<CornerRadius>
    {
        public double TopLeft { get; set; }
        public double TopRight { get; set; }
        public double BottomRight { get; set; }
        public double BottomLeft { get; set; }

        public CornerRadius(double uniformRadius)
        {
            TopLeft = TopRight = BottomRight = BottomLeft = uniformRadius;
        }

        public CornerRadius(double topLeft, double topRight, double bottomRight, double bottomLeft)
        {
            TopLeft = topLeft;
            TopRight = topRight;
            BottomRight = bottomRight;
            BottomLeft = bottomLeft;
        }

        public bool Equals(CornerRadius other) =>
            TopLeft == other.TopLeft && TopRight == other.TopRight &&
            BottomRight == other.BottomRight && BottomLeft == other.BottomLeft;

        public override bool Equals(object? obj) => obj is CornerRadius c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(TopLeft, TopRight, BottomRight, BottomLeft);

        public static bool operator ==(CornerRadius a, CornerRadius b) => a.Equals(b);
        public static bool operator !=(CornerRadius a, CornerRadius b) => !a.Equals(b);

        public override string ToString() => $"{TopLeft},{TopRight},{BottomRight},{BottomLeft}";
    }
}
