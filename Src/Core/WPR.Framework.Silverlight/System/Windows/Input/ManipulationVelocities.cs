using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace System.Windows.Input
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationVelocities</c>.</summary>
    public sealed class ManipulationVelocities
    {
        public Point LinearVelocity { get; set; }
        public double AngularVelocity { get; set; }
        public Point ExpansionVelocity { get; set; }
    }
}
