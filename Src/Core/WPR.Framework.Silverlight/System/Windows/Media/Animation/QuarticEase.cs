using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace System.Windows.Media.Animation
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.QuarticEase</c>.</summary>
    public class QuarticEase : IEasingFunction
    {
        public EasingMode EasingMode { get; set; } = EasingMode.EaseOut;
    }
}
