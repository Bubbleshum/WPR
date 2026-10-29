using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace System.Windows.Input
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationDeltaEventArgs</c>.</summary>
    public class ManipulationDeltaEventArgs : RoutedEventArgs
    {
        public Point ManipulationOrigin { get; set; }
        public UIElement? ManipulationContainer { get; set; }
        public ManipulationDelta DeltaManipulation { get; set; } = new();
        public ManipulationDelta CumulativeManipulation { get; set; } = new();
        public ManipulationVelocities Velocities { get; set; } = new();
        public void Complete() { }
    }
}
