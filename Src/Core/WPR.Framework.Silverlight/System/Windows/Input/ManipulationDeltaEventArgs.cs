using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationDeltaEventArgs</c>.</summary>
    public class ManipulationDeltaEventArgs : RoutedEventArgs
    {
        public Point ManipulationOrigin { get; set; }
        public UIElement? ManipulationContainer { get; set; }
        public ManipulationDelta DeltaManipulation { get; set; }
        public ManipulationDelta CumulativeManipulation { get; set; }
        public ManipulationVelocities Velocities { get; set; }
        public void Complete() { }
    }
}
