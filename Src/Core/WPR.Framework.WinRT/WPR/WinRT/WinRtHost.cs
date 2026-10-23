namespace WPR.WinRT
{
    /// <summary>
    /// What the WinRT classes in this assembly answer about the running title and the phone it
    /// thinks it is on. Filled per launch by <c>ApplicationLaunch</c> (the install folder) from the
    /// same numbers the WP8 native runtime uses (<c>Wp8Display</c>), so a managed and a native WP8
    /// title see the same device.
    /// </summary>
    public static class WinRtHost
    {
        /// <summary>The title's install folder: <c>Package.Current.InstalledLocation.Path</c>.</summary>
        public static string InstalledPath { get; set; } = System.AppContext.BaseDirectory;

        /// <summary><c>DisplayProperties.ResolutionScale</c> as a percentage (100, 150, 160).</summary>
        public static int ResolutionScalePercent { get; set; } = 160;

        /// <summary>True when the title runs landscape (what <c>CurrentOrientation</c> reports).</summary>
        public static bool Landscape { get; set; }

        /// <summary>
        /// <c>ApplicationData.Current.LocalFolder</c>: the title's own isolated store, as on WP8,
        /// where the two were one folder. Filled per launch; the default is only for tests.
        /// </summary>
        public static string LocalFolderPath { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-winrt-local");

        /// <summary>The title's product id (<c>CurrentApp.AppId</c>, <c>Package.Current.Id.Name</c>).</summary>
        public static string ProductId { get; set; } = string.Empty;

        /// <summary><c>Package.Current.Id.Version</c>.</summary>
        public static Windows.ApplicationModel.PackageVersion Version { get; set; }

        /// <summary>
        /// <c>Launcher.LaunchUriAsync</c>: the platform's link opener, or null when it has none
        /// (filled per launch from LauncherBackend; must not throw).
        /// </summary>
        public static System.Func<System.Uri, bool>? OpenUri { get; set; }
    }
}
