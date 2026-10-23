using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.TransformGroup</c>. Children are its XAML content, as in
    /// Silverlight (Microsoft Minesweeper writes its transforms directly inside the group).
    /// </summary>
    [ContentProperty(nameof(Children))]
    public class TransformGroup : Transform
    {
        public TransformCollection Children { get; } = new TransformCollection();
    }
}
