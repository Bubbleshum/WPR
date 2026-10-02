namespace WPR.Common
{
    /// <summary>
    /// Ambient information about the currently-hosted game, published by the launch
    /// path and read by low-level shims that can't (and shouldn't) reference the
    /// launcher or framework projects.
    ///
    /// <para><see cref="CurrentInstallFolder"/> is the on-disk root of the running
    /// game (<c>%LocalAppData%\WPR\AppData\&lt;ProductId&gt;</c>). On real WP7 the app's
    /// working directory WAS its install root, so titles read data files with bare
    /// relative paths (e.g. <c>XboxLIVESettings.xml</c>). Under WPR a Silverlight app
    /// runs in-process, so the process CWD is the host's exe directory and those
    /// relative reads miss. Shims like <c>XElement2.Load</c> fall back to this folder
    /// so the reads resolve where the game expects.</para>
    ///
    /// Set by <c>SilverlightAppHost.Boot</c> (Silverlight path) and
    /// <c>ApplicationLaunch</c> (XNA path). Lives here in WPR.Common so the shim
    /// assemblies can read it without an upward project dependency.
    /// </summary>
    public static class WprHostEnvironment
    {
        /// <summary>
        /// Install-root of the game currently being launched/hosted, or <c>null</c>
        /// when no game is active.
        /// </summary>
        public static string? CurrentInstallFolder { get; set; }

        /// <summary>
        /// ProductId of the game currently being launched/hosted, or <c>null</c> when no game is
        /// active. Set by the same launch paths that set <see cref="CurrentInstallFolder"/>.
        ///
        /// This mirrors <c>Application.Current.ProductId</c> deliberately. GamerServices needs the
        /// product id to scope achievement rows, and reading it off the Silverlight
        /// <c>Application</c> shim forced the XNA layer to depend on the whole Silverlight/Avalonia
        /// stack for one string. Both are kept in sync so the Silverlight-hosted path is unchanged.
        /// </summary>
        public static string? CurrentProductId { get; set; }

        /// <summary>
        /// Display name of the game currently being hosted, or <c>null</c>. Set beside
        /// <see cref="CurrentProductId"/>; WPR Hub uses it to name a leaderboard's game the first
        /// time a score arrives for a title nobody has defined yet.
        /// </summary>
        public static string? CurrentTitleName { get; set; }

        /// <summary>
        /// True while the game was started with "play with logging": <c>ApplicationLaunch</c> then
        /// attaches the per-game trace log and the first-chance exception logger that a Release build
        /// otherwise leaves out, and the head shows a "stop and send report" button over the game.
        /// Set by the head for one launch (on Android, in the <c>:game</c> process from the intent).
        /// </summary>
        public static bool DiagnosticRun { get; set; }
    }
}
