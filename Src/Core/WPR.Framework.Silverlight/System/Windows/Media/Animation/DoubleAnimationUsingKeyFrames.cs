using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames</c>.</summary>
    [ContentProperty(nameof(KeyFrames))]
    public class DoubleAnimationUsingKeyFrames : Timeline
    {
        public DoubleKeyFrameCollection KeyFrames { get; } = new DoubleKeyFrameCollection();
    }
}
