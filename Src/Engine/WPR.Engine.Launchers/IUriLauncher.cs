using System;

namespace WPR.Engine.Launchers
{
    /// <summary>
    /// Hands a URI to whatever the host OS uses to open it — the default browser, or an app that
    /// has claimed the link (on Android, a youtube.com link goes to the YouTube app).
    /// </summary>
    /// <remarks>
    /// <para>This is how WP7's launchers leave the game. <c>WebBrowserTask</c> is the first caller;
    /// <c>EmailComposeTask</c> (<c>mailto:</c>) and <c>BingMapsTask</c> are the obvious next ones,
    /// which is why the contract is "open a URI" rather than "open a web page".</para>
    ///
    /// <para><b>Policy lives in the caller, not here.</b> Which schemes a game may open, and how a
    /// WP7-only scheme such as <c>vnd.youtube:</c> is translated, are decided by the shim, which
    /// knows the WP7 API. An implementation opens what it is given.</para>
    /// </remarks>
    public interface IUriLauncher
    {
        /// <summary>
        /// Opens <paramref name="uri"/> outside the game. Must not throw: a launcher that fails is
        /// reported as <c>false</c>, and the game carries on, which is what WP7 did when a task
        /// could not be shown.
        /// </summary>
        /// <returns>True if the OS accepted the request.</returns>
        bool TryOpen(Uri uri);
    }
}
