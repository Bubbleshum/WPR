using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.ExponentialEase</c>.</summary>
    public class ExponentialEase : IEasingFunction
    {
        public double Exponent { get; set; } = 2.0;
        public EasingMode EasingMode { get; set; } = EasingMode.EaseOut;
    }
}
