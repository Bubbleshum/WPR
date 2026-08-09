using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.TranslateTransform</c>.</summary>
    public class TranslateTransform : Transform
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
