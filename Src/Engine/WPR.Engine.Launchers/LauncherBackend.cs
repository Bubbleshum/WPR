namespace WPR.Engine.Launchers
{
    /// <summary>
    /// The registry a platform head fills through <c>caps.UriLauncher(...)</c> and
    /// <c>caps.ShareSheet(...)</c>. One slot per OS facility.
    /// </summary>
    /// <remarks>
    /// <para>Process-lifetime, like <c>VibrationBackend</c>: it is set at composition and not
    /// cleared at game teardown. There is nothing to reset between launches either — the seam is
    /// push-only, with no events and no subscribers, so it cannot pin a game's load context.</para>
    ///
    /// <para><b>Null is the normal unset state, not an error.</b> A platform that declares no
    /// launcher leaves the WP7 tasks that would use it silent no-ops, which is what they all were
    /// before this existed.</para>
    /// </remarks>
    public static class LauncherBackend
    {
        public static IUriLauncher? Uri { get; private set; }

        public static IShareSheet? Share { get; private set; }

        public static void SetUriLauncher(IUriLauncher? launcher) => Uri = launcher;

        public static void SetShareSheet(IShareSheet? sheet) => Share = sheet;
    }
}
