namespace WPR.Common
{
    /// <summary>
    /// Ambient information about the currently-hosted game, published by the launch
    /// path and read by low-level shims that can't (and shouldn't) reference the
    /// launcher or framework projects.
    ///
    /// <para><see cref="CurrentInstallFolder"/> is the on-disk root of the running
    /// game (<c>%LocalAppData%\WPR\Apps\&lt;ProductId&gt;</c>). On real WP7 the app's
    /// working directory WAS its install root, so titles read data files with bare
    /// relative paths (e.g. <c>XboxLIVESettings.xml</c>). Under WPR a Silverlight app
    /// runs in-process, so the process CWD is the host's exe directory and those
    /// relative reads miss. Shims like <c>XElement2.Load</c> fall back to this folder
    /// so the reads resolve where the game expects.</para>
    ///
    /// Set by <c>SilverlightAppHost.Boot</c> (Silverlight path) and
    /// <c>ApplicationLaunch</c> (XNA path). Lives here in WPR.Common so
    /// WPR.StandardCompability can read it without an upward project dependency.
    /// </summary>
    public static class WprHostEnvironment
    {
        /// <summary>
        /// Install-root of the game currently being launched/hosted, or <c>null</c>
        /// when no game is active.
        /// </summary>
        public static string? CurrentInstallFolder { get; set; }
    }
}
