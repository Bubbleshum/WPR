using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.VisualTransition</c>. Storyboard between two named states.</summary>
    public class VisualTransition : DependencyObject
    {
        public string? From { get; set; }
        public string? To { get; set; }
        public Duration GeneratedDuration { get; set; } = new Duration(TimeSpan.Zero);
        public Storyboard? Storyboard { get; set; }
    }
}
