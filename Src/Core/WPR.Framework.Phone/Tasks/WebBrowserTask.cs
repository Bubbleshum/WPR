using System;
using System.Diagnostics;
using WPR.Engine.Launchers;

namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Tasks.WebBrowserTask</c> — opens a web page outside the game.
    /// </summary>
    /// <remarks>
    /// <para>Goes through <see cref="LauncherBackend.Uri"/>, which each head fills: the default
    /// browser on Windows, an <c>ACTION_VIEW</c> intent on Android (so a YouTube link lands in the
    /// YouTube app, as it did on the phone). A platform that declares no launcher leaves this the
    /// no-op it used to be. <see cref="Show"/> never throws: WP7 games call it from button handlers
    /// and a throw there takes the whole handler down.</para>
    ///
    /// <para><b>Cut the Rope is the reference case.</b> Its Cartoons menu does not play video at
    /// all — every episode is a <c>WebBrowserTask</c> to <c>vnd.youtube:&lt;id&gt;</c> (Youku on
    /// Chinese locales). With the empty <c>Show</c> the tap did nothing, while the game still
    /// marked the episode watched.</para>
    ///
    /// <para><b>Two pieces of policy live here, because only the WP7 side knows them:</b></para>
    /// <list type="bullet">
    /// <item><c>vnd.youtube:</c> is the scheme the WP7 YouTube app registered. Android's YouTube
    /// app happens to claim it too, but nothing on a desktop does and a phone without the app has
    /// no handler, so it is rewritten to the equivalent <c>https://www.youtube.com/watch?v=</c>
    /// link — which Android still routes to the YouTube app when it is installed.</item>
    /// <item><b>Only http and https leave the game.</b> The scheme is game-supplied data; on Android
    /// an arbitrary scheme becomes an intent any installed app may claim, and <c>file:</c> or
    /// <c>intent:</c> have no business coming out of a 2011 phone game. Anything else is logged as
    /// <c>[wpr-launch]</c> and dropped.</item>
    /// </list>
    /// </remarks>
    public class WebBrowserTask
    {
        public WebBrowserTask()
        {
        }

        /// <summary>
        /// The WP7.0 property; superseded by <see cref="Uri"/> in 7.1 but still used by older
        /// titles, so it is honoured when <see cref="Uri"/> is not set.
        /// </summary>
        public string? URL { get; set; }

        public Uri? Uri { get; set; }

        public void Show()
        {
            string? raw = Uri?.OriginalString ?? URL;
            if (string.IsNullOrWhiteSpace(raw))
            {
                Trace.WriteLine("[wpr-launch] WebBrowserTask.Show with no URI - ignored");
                return;
            }

            Uri? target = Normalise(raw.Trim());
            if (target == null)
            {
                Trace.WriteLine("[wpr-launch] WebBrowserTask.Show refused '" + raw + "' (only http/https are opened)");
                return;
            }

            IUriLauncher? launcher = LauncherBackend.Uri;
            if (launcher == null)
            {
                Trace.WriteLine("[wpr-launch] WebBrowserTask.Show '" + target.AbsoluteUri + "' - no launcher on this platform");
                return;
            }

            try
            {
                launcher.TryOpen(target);
            }
            catch (Exception ex)
            {
                // The contract says TryOpen does not throw; this is the belt to that brace, because
                // the caller is a game's button handler.
                Trace.WriteLine("[wpr-launch] launcher threw: " + ex);
            }
        }

        /// <summary>
        /// Turns what a WP7 title passed into an http(s) URI, or null if it is not one we open.
        /// </summary>
        internal static Uri? Normalise(string raw)
        {
            const string youTube = "vnd.youtube:";
            if (raw.StartsWith(youTube, StringComparison.OrdinalIgnoreCase))
            {
                string id = raw.Substring(youTube.Length).TrimStart('/');
                int cut = id.IndexOfAny(new[] { '?', '&', '#' });
                if (cut >= 0) id = id.Substring(0, cut);
                if (id.Length == 0 || id.IndexOf('/') >= 0) return null;
                raw = "https://www.youtube.com/watch?v=" + global::System.Uri.EscapeDataString(id);
            }
            else if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                // A bare host with no scheme, which the WP7 browser accepted.
                raw = "http://" + raw;
            }

            if (!global::System.Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)) return null;
            if (uri.Scheme != global::System.Uri.UriSchemeHttp && uri.Scheme != global::System.Uri.UriSchemeHttps) return null;
            return uri;
        }
    }
}
