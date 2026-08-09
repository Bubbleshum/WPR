using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.EasingDoubleKeyFrame</c>.</summary>
    public class EasingDoubleKeyFrame : DoubleKeyFrame
    {
        public IEasingFunction? EasingFunction { get; set; }
    }
}
