using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Shell.ShellToast</c>, the toast a background agent shows. WPR
    /// runs no background agents, and a toast raised while the game is in front was never shown on
    /// the phone either, so <see cref="Show"/> only records it in the log.
    /// </summary>
    public class ShellToast
    {
        public string? Title { get; set; }

        public string? Content { get; set; }

        public Uri? NavigationUri { get; set; }

        public Uri? Sound { get; set; }

        public void Show()
        {
            System.Diagnostics.Trace.WriteLine($"[wpr-toast] ShellToast \"{Title}\": {Content}");
        }
    }
}
