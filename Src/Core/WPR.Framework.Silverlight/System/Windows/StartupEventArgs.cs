using System;
using System.Collections.Generic;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.StartupEventArgs</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="InitParams"/> carried the parameters a Silverlight plugin was embedded with on
    /// a web page. A phone app is not embedded in anything, so on WP7 this was already always
    /// empty — an empty dictionary is the faithful value, not a placeholder.
    /// </remarks>
    public class StartupEventArgs : EventArgs
    {
        public IDictionary<string, string> InitParams { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Shim for <c>System.Windows.StartupEventHandler</c>.
    /// </summary>
    public delegate void StartupEventHandler(object sender, StartupEventArgs e);
}
