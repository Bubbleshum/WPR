using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationVelocities</c>.</summary>
    public struct ManipulationVelocities
    {
        public Point LinearVelocity;
        public double AngularVelocity;
        public Point ExpansionVelocity;
    }
}
