using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Matrix</c>: a 2D affine matrix, row-vector convention.</summary>
    /// <remarks>
    /// A point maps as <c>x' = x*M11 + y*M21 + OffsetX</c>, <c>y' = x*M12 + y*M22 + OffsetY</c>, which is
    /// Silverlight's. The WP Toolkit's GestureListener reads one out of a <see cref="MatrixTransform"/>,
    /// zeroes the offset and writes it back, so a drag delta is rotated and scaled but not moved.
    /// </remarks>
    public struct Matrix
    {
        // Stored as differences from the identity, so default(Matrix) IS the identity, as in Silverlight.
        private double _m11Delta;
        private double _m22Delta;

        public Matrix(double m11, double m12, double m21, double m22, double offsetX, double offsetY)
        {
            _m11Delta = m11 - 1;
            M12 = m12;
            M21 = m21;
            _m22Delta = m22 - 1;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        public double M11 { get => _m11Delta + 1; set => _m11Delta = value - 1; }
        public double M12 { get; set; }
        public double M21 { get; set; }
        public double M22 { get => _m22Delta + 1; set => _m22Delta = value - 1; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }

        public static Matrix Identity => default;

        public bool IsIdentity =>
            _m11Delta == 0 && M12 == 0 && M21 == 0 && _m22Delta == 0 && OffsetX == 0 && OffsetY == 0;

        public Point Transform(Point point) =>
            new Point(point.X * M11 + point.Y * M21 + OffsetX, point.X * M12 + point.Y * M22 + OffsetY);

        internal double Determinant => M11 * M22 - M12 * M21;

        /// <summary>The inverse, or null when the matrix is singular.</summary>
        internal Matrix? TryInvert()
        {
            double det = Determinant;
            if (det == 0 || double.IsNaN(det)) return null;
            double m11 = M22 / det, m12 = -M12 / det, m21 = -M21 / det, m22 = M11 / det;
            return new Matrix(m11, m12, m21, m22,
                -(OffsetX * m11 + OffsetY * m21),
                -(OffsetX * m12 + OffsetY * m22));
        }

        public override string ToString() =>
            IsIdentity ? "Identity" : $"{M11},{M12},{M21},{M22},{OffsetX},{OffsetY}";
    }
}
