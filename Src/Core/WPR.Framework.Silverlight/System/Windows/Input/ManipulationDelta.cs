using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace System.Windows.Input
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationDelta</c>.</summary>
    /// <remarks>A sealed class with properties, as in Silverlight: it was a struct with fields, so
    /// game IL calling <c>get_Translation</c> (Coding4Fun, Microsoft Bingo) did not resolve.</remarks>
    public sealed class ManipulationDelta
    {
        public Point Translation { get; set; }
        public double Rotation { get; set; }
        public Point Scale { get; set; }
        public Point Expansion { get; set; }
    }
}
