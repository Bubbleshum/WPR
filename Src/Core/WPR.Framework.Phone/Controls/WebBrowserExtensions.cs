using System.Threading.Tasks;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// WP8's <c>Microsoft.Phone.Controls.WebBrowserExtensions</c>: clearing the browser control's
    /// cookies and cache. WPR's WebBrowser keeps neither, so both finish at once. The Facebook
    /// client SDK clears cookies on every logout; Microsoft Bingo logs out from its Facebook
    /// service's constructor.
    /// </summary>
    public static class WebBrowserExtensions
    {
        public static Task ClearCookiesAsync(this WebBrowser browser) => Task.CompletedTask;

        public static Task ClearInternetCacheAsync(this WebBrowser browser) => Task.CompletedTask;
    }
}
