using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.KeyEventArgs</c>.</summary>
    public class KeyEventArgs : RoutedEventArgs
    {
        public Key Key { get; set; }
        public int PlatformKeyCode { get; set; }
    }
}
