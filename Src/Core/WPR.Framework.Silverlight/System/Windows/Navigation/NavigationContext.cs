using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Navigation.NavigationContext</c> — the query-string parameters
    /// a page was navigated to with.
    /// </summary>
    /// <remarks>
    /// WP7 apps pass state between pages by putting it in the URI
    /// (<c>/GamePage.xaml?level=3</c>) and reading it back out of here. The dictionary is
    /// populated from the query string of the URI the page was actually navigated to, so a page
    /// that reads a parameter it was given gets it.
    ///
    /// <para><b>Missing keys are the normal case and must not throw.</b> A WP7 page is entitled
    /// to be navigated to with no parameters at all — which is exactly what happens for the
    /// start page, the only navigation WPR's mixed-mode host performs — so <c>QueryString</c>
    /// is an empty dictionary rather than null, and callers use <c>TryGetValue</c> or
    /// <c>ContainsKey</c> as they did on the phone.</para>
    /// </remarks>
    public class NavigationContext
    {
        public IDictionary<string, string> QueryString { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Fills <see cref="QueryString"/> from a navigation URI's query part. Tolerates a null
        /// or query-less URI by leaving the dictionary empty.
        /// </summary>
        internal void Populate(Uri? uri)
        {
            QueryString.Clear();
            if (uri == null) return;

            // The URI is relative ("/GamePage.xaml?a=1"), so Uri.Query is unavailable — split the
            // raw string instead.
            string raw = uri.IsAbsoluteUri ? uri.Query : uri.OriginalString;
            int q = raw.IndexOf('?');
            if (q < 0 || q == raw.Length - 1) return;

            foreach (string pair in raw.Substring(q + 1).Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    QueryString[Uri.UnescapeDataString(pair)] = string.Empty;
                }
                else
                {
                    QueryString[Uri.UnescapeDataString(pair.Substring(0, eq))] =
                        Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
        }
    }
}
