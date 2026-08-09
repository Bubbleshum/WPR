using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.FontWeight</c>.</summary>
    public struct FontWeight : IEquatable<FontWeight>
    {
        public int Weight { get; }
        public FontWeight(int weight) { Weight = weight; }
        public bool Equals(FontWeight other) => Weight == other.Weight;
        public override bool Equals(object? obj) => obj is FontWeight fw && Equals(fw);
        public override int GetHashCode() => Weight;
        public static bool operator ==(FontWeight a, FontWeight b) => a.Equals(b);
        public static bool operator !=(FontWeight a, FontWeight b) => !a.Equals(b);
        public override string ToString() => Weight.ToString();
    }
}
