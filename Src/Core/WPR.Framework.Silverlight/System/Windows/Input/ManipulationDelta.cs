using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationDelta</c>.</summary>
    public struct ManipulationDelta
    {
        public Point Translation;
        public double Rotation;
        public Point Scale;
        public Point Expansion;
    }
}
