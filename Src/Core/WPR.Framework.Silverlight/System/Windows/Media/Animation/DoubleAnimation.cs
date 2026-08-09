using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.DoubleAnimation</c>.</summary>
    public class DoubleAnimation : Timeline
    {
        public double? From { get; set; }
        public double? To { get; set; }
        public double? By { get; set; }
        public IEasingFunction? EasingFunction { get; set; }
    }
}
