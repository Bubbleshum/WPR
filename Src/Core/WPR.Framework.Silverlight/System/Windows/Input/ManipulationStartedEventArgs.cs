using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.ManipulationStartedEventArgs</c>.</summary>
    public class ManipulationStartedEventArgs : RoutedEventArgs
    {
        public Point ManipulationOrigin { get; set; }
        public UIElement? ManipulationContainer { get; set; }
        public void Complete() { }
    }
}
