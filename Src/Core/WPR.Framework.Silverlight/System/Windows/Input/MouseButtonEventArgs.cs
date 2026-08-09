using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.MouseButtonEventArgs</c>.</summary>
    public class MouseButtonEventArgs : RoutedEventArgs
    {
        public Point GetPosition(UIElement? relativeTo) => default;
    }
}
