using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.TileBrush</c>. Base for ImageBrush.</summary>
    public abstract class TileBrush : Brush
    {
        public AlignmentX AlignmentX { get; set; } = AlignmentX.Center;
        public AlignmentY AlignmentY { get; set; } = AlignmentY.Center;
        public Stretch Stretch { get; set; } = Stretch.Fill;
    }
}
