using global::System.IO;
using WPR.WinRT;

namespace Windows.ApplicationModel
{
    /// <summary>
    /// Shim for <c>Windows.ApplicationModel.Package</c>: the running title's package. Only what WP8
    /// titles were measured to read is here (SharpDX.Toolkit.Game takes its content root from
    /// <c>Current.InstalledLocation.Path</c>).
    /// </summary>
    public sealed class Package
    {
        private static readonly Package _current = new();

        private Package()
        {
        }

        public static Package Current => _current;

        public Windows.Storage.StorageFolder InstalledLocation => new(WinRtHost.InstalledPath);

        public PackageId Id => new();
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.PackageId</c>: the title's identity as WPR installed it.</summary>
    public sealed class PackageId
    {
        internal PackageId()
        {
        }

        public string Name => WinRtHost.ProductId;

        public string FullName => WinRtHost.ProductId;

        public PackageVersion Version => WinRtHost.Version;

        public string Publisher => string.Empty;
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.PackageVersion</c> (a WinRT struct: public fields).</summary>
    public struct PackageVersion
    {
        public ushort Major;
        public ushort Minor;
        public ushort Build;
        public ushort Revision;
    }
}

namespace Windows.Graphics.Display
{
    /// <summary>Shim for <c>Windows.Graphics.Display.ResolutionScale</c>; the values are the percentages.</summary>
    public enum ResolutionScale
    {
        Invalid = 0,
        Scale100Percent = 100,
        Scale140Percent = 140,
        Scale150Percent = 150,
        Scale160Percent = 160,
        Scale180Percent = 180,
        Scale225Percent = 225,
    }

    /// <summary>Shim for <c>Windows.Graphics.Display.DisplayOrientations</c>.</summary>
    [global::System.Flags]
    public enum DisplayOrientations : uint
    {
        None = 0,
        Landscape = 1,
        Portrait = 2,
        LandscapeFlipped = 4,
        PortraitFlipped = 8,
    }

    /// <summary>
    /// Shim for <c>Windows.Graphics.Display.DisplayProperties</c> (WP8's static display API),
    /// answering for the phone WPR presents (<c>WinRtHost</c>).
    /// </summary>
    public static class DisplayProperties
    {
        public static ResolutionScale ResolutionScale => (ResolutionScale)WinRtHost.ResolutionScalePercent;

        public static float LogicalDpi => 96f * WinRtHost.ResolutionScalePercent / 100f;

        public static DisplayOrientations CurrentOrientation => WinRtHost.Landscape ? DisplayOrientations.Landscape : DisplayOrientations.Portrait;

        public static DisplayOrientations NativeOrientation => DisplayOrientations.Portrait;

        private static DisplayOrientations _autoRotation;

        public static DisplayOrientations AutoRotationPreferences => _autoRotation;

        /// <summary>WinRT names the setter put_AutoRotationPreferences, which is what game IL calls.</summary>
        public static void put_AutoRotationPreferences(DisplayOrientations value) => _autoRotation = value;
    }
}
