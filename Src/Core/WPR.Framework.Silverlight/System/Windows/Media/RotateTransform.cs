using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.RotateTransform</c>.</summary>
    /// <remarks>
    /// Holds the rotation; neither renderer applies it yet (the CPU rasteriser is axis-aligned).
    /// It exists because a MISSING transform type is far more expensive than an unapplied one: the
    /// typeref stays scoped to WP7's <c>System.Windows</c>, which does not exist at runtime, so the
    /// whole method naming it fails to compile. Galactic Reign is the measured case —
    /// <c>TypeLoadException: Could not load type 'System.Windows.Media.RotateTransform'</c> took
    /// down <c>ArmadaClient.MenuPage</c>'s construction and the game never reached a menu.
    /// </remarks>
    public class RotateTransform : Transform
    {
        public double Angle { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }
}
