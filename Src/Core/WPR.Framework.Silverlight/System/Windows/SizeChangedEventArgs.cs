using System;
using System.Collections.Generic;

namespace System.Windows
{
    /// <summary>Shim for <c>System.Windows.SizeChangedEventArgs</c>.</summary>
    public class SizeChangedEventArgs : RoutedEventArgs
    {
        public Size PreviousSize { get; set; }
        public Size NewSize { get; set; }
    }
}
