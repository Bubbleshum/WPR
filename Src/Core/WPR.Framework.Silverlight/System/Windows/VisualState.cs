using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.VisualState</c>.</summary>
    public class VisualState : DependencyObject
    {
        public string? Name { get; set; }
        public Storyboard? Storyboard { get; set; }
    }
}
