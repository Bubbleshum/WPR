using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.TransformGroup</c>.</summary>
    public class TransformGroup : Transform
    {
        public TransformCollection Children { get; } = new TransformCollection();
    }
}
