namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Net.HttpUtility</c> (Silverlight/WP put HttpUtility in the
    /// <c>System.Windows</c> assembly's <c>System.Net</c> namespace). Delegates to
    /// <see cref="System.Net.WebUtility"/>, the modern .NET equivalent. AC Pirates uses
    /// <see cref="UrlDecode"/> in its URI mapper and in <c>MainPage.OnNavigatedTo</c>.
    /// </summary>
    public static class HttpUtility
    {
        public static string? UrlDecode(string? str)
            => str == null ? null : System.Net.WebUtility.UrlDecode(str);

        public static string? UrlEncode(string? str)
            => str == null ? null : System.Net.WebUtility.UrlEncode(str);

        public static string? HtmlDecode(string? str)
            => str == null ? null : System.Net.WebUtility.HtmlDecode(str);

        public static string? HtmlEncode(string? str)
            => str == null ? null : System.Net.WebUtility.HtmlEncode(str);
    }
}
