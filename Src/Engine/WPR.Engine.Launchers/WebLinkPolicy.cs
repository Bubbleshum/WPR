using System;

namespace WPR.Engine.Launchers
{
    /// <summary>
    /// Which links a phone game may send out of WPR, and how a phone-only scheme is translated:
    /// the one rule every launcher caller applies before <see cref="IUriLauncher.TryOpen"/>.
    /// </summary>
    /// <remarks>
    /// <para>It lives here rather than in a shim because there are two callers that must agree: WP7's
    /// <c>Microsoft.Phone.Tasks.WebBrowserTask</c> and WP8's <c>Windows.System.Launcher.LaunchUriAsync</c>
    /// (the native runtime, <c>WPR.Engine.Wp8Native</c>). Neither may be laxer than the other.</para>
    ///
    /// <list type="bullet">
    /// <item><c>vnd.youtube:</c> is the scheme the Windows Phone YouTube app registered. Nothing on a
    /// desktop claims it and a phone without the app has no handler, so it is rewritten to the
    /// equivalent <c>https://www.youtube.com/watch?v=</c> link, which Android still routes to the
    /// YouTube app when it is installed.</item>
    /// <item><b>Only http and https leave the game.</b> The URI is game-supplied data; on Android an
    /// arbitrary scheme becomes an intent any installed app may claim, and <c>file:</c> or
    /// <c>intent:</c> have no business coming out of a phone game.</item>
    /// </list>
    /// </remarks>
    public static class WebLinkPolicy
    {
        /// <summary>
        /// Turns what a game passed into an http(s) URI, or null if it is not one we open.
        /// </summary>
        public static Uri? Normalise(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();

            const string youTube = "vnd.youtube:";
            if (raw.StartsWith(youTube, StringComparison.OrdinalIgnoreCase))
            {
                string id = raw.Substring(youTube.Length).TrimStart('/');
                int cut = id.IndexOfAny(new[] { '?', '&', '#' });
                if (cut >= 0) id = id.Substring(0, cut);
                if (id.Length == 0 || id.IndexOf('/') >= 0) return null;
                raw = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id);
            }
            else if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                // A bare host with no scheme, which the phone's browser accepted.
                raw = "http://" + raw;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            return uri;
        }
    }
}
