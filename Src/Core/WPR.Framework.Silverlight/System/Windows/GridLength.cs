using System;

namespace WPR.SilverlightCompability
{
    public struct GridLength : IEquatable<GridLength>
    {
        public static GridLength Auto => new(1.0, GridUnitType.Auto);

        public double Value { get; }
        public GridUnitType GridUnitType { get; }

        public GridLength(double value) : this(value, GridUnitType.Pixel) { }

        public GridLength(double value, GridUnitType type)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                throw new ArgumentException("GridLength value must be a non-negative finite number.", nameof(value));
            Value = value;
            GridUnitType = type;
        }

        public bool IsAuto => GridUnitType == GridUnitType.Auto;
        public bool IsAbsolute => GridUnitType == GridUnitType.Pixel;
        public bool IsStar => GridUnitType == GridUnitType.Star;

        public bool Equals(GridLength other) => Value == other.Value && GridUnitType == other.GridUnitType;
        public override bool Equals(object? obj) => obj is GridLength g && Equals(g);
        public override int GetHashCode() => HashCode.Combine(Value, GridUnitType);

        public static bool operator ==(GridLength a, GridLength b) => a.Equals(b);
        public static bool operator !=(GridLength a, GridLength b) => !a.Equals(b);

        public override string ToString() => GridUnitType switch
        {
            GridUnitType.Auto => "Auto",
            GridUnitType.Star => Value == 1.0 ? "*" : $"{Value}*",
            _ => Value.ToString(),
        };
    }
}
