using System.Reflection;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Holds runtime references the boot path establishes after loading the user's app —
    /// used by code in this assembly that has to look up resources/types in the user assembly
    /// without taking a project-level reference back to it.
    /// </summary>
    public static class HostContext
    {
        /// <summary>
        /// The user's main assembly (e.g. <c>WinPhoneRunnerAppInterop.dll</c>).
        /// Set by <c>WPR.SilverlightAppHost.Boot</c> after loading it; null on XNA path.
        /// </summary>
        public static Assembly? UserAssembly { get; set; }

        /// <summary>
        /// The running app's ProductId (GUID from WMAppManifest). Used by per-app extension
        /// points to look up app-specific behaviour — e.g. the
        /// <see cref="DrawingSurfaceBackgroundGrid"/> renderer registry.
        /// </summary>
        public static string? CurrentProductId { get; set; }

        /// <summary>
        /// Absolute path to the running app's install folder (where WMAppManifest.xml,
        /// SplashScreenImage.jpg, the .dll/.winmd files, etc. live). Used by renderers and
        /// extensions that need to read app assets at runtime.
        /// </summary>
        public static string? CurrentInstallFolder { get; set; }
    }
}
