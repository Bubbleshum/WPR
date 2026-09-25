using System;
using System.Diagnostics;
using WPR.Engine.Launchers;

namespace WPR.Launchers.ShellExecute
{
    /// <summary>
    /// Opens a URI through the OS shell — the default browser for http/https.
    /// </summary>
    public sealed class ShellExecuteUriLauncher : IUriLauncher
    {
        public bool TryOpen(Uri uri)
        {
            try
            {
                // UseShellExecute is what hands the string to the shell's protocol handlers; with it
                // off (the .NET Core default) Process.Start would try to run the URL as a file.
                using Process? p = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                Trace.WriteLine("[wpr-launch] opened " + uri.AbsoluteUri);
                return true;
            }
            catch (Exception ex)
            {
                // Trace, not WPR.Common.Log: that writes to stdout, which a WinExe discards.
                Trace.WriteLine($"[wpr-launch] could not open {uri.AbsoluteUri}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
