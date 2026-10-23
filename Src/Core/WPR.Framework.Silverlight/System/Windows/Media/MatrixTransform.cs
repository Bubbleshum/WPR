using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.MatrixTransform</c>.</summary>
    /// <remarks>
    /// What <see cref="UIElement.TransformToVisual"/> returns, as Silverlight's did; the WP Toolkit's
    /// GestureListener depends on that (it type-tests the inverse for MatrixTransform).
    /// </remarks>
    public class MatrixTransform : Transform
    {
        public MatrixTransform() { }

        public MatrixTransform(Matrix matrix) { Matrix = matrix; }

        public Matrix Matrix { get; set; }

        public override Point Transform(Point point) => Matrix.Transform(point);

        public override GeneralTransform? Inverse =>
            Matrix.TryInvert() is Matrix inverse ? new MatrixTransform(inverse) : null;
    }
}
