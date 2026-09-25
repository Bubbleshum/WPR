using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.ScaleTransform</c>.</summary>
    /// <remarks>
    /// The sibling of <see cref="RotateTransform"/>, added for the same reason and with the same
    /// limitation: the values are stored, nothing applies them yet. Scale is the transform a WP7
    /// design reaches for most often after translate, so a game is likely to name both.
    /// </remarks>
    public class ScaleTransform : Transform
    {
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }
}
