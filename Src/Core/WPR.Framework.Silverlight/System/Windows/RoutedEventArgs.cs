using System;

namespace System.Windows
{
    public class RoutedEventArgs : EventArgs
    {
        public object? OriginalSource { get; set; }
        public bool Handled { get; set; }
    }
}
