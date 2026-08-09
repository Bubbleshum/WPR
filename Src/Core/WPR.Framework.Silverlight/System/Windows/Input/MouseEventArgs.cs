using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.MouseEventArgs</c>.</summary>
    public class MouseEventArgs : RoutedEventArgs
    {
        public Point GetPosition(UIElement? relativeTo) => default;
    }
}
