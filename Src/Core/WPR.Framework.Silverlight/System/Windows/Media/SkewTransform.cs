namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.SkewTransform</c>, drawn by the rasteriser's skew path.</summary>
    /// <remarks>
    /// Blend's default transform group is Scale, Skew, Rotate, Translate, and storyboards address
    /// the translate as <c>(TransformGroup.Children)[3]</c>. With SkewTransform missing, the group
    /// had three children and that index named nothing: Microsoft Solitaire's loading animation
    /// never moved its sprite sheet.
    /// </remarks>
    public class SkewTransform : Transform
    {
        public double AngleX { get; set; }
        public double AngleY { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }
}
