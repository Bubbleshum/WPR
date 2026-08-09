using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.VisualStateChangedEventArgs</c>.</summary>
    public class VisualStateChangedEventArgs : EventArgs
    {
        public VisualState? OldState { get; set; }
        public VisualState? NewState { get; set; }
        public Control? Control { get; set; }
    }
}
