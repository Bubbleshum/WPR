using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationCompletedEventArgs</c>.</summary>
    public class ManipulationCompletedEventArgs : RoutedEventArgs
    {
        public Point ManipulationOrigin { get; set; }
        public UIElement? ManipulationContainer { get; set; }
        public ManipulationDelta TotalManipulation { get; set; }
        public ManipulationVelocities FinalVelocities { get; set; }
    }
}
