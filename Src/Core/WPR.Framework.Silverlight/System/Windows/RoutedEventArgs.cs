using System;

namespace WPR.SilverlightCompability
{
    public class RoutedEventArgs : EventArgs
    {
        public object? OriginalSource { get; set; }
        public bool Handled { get; set; }
    }
}
