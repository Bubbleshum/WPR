using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Stub for <c>Microsoft.Phone.Shell.RunningInBackgroundEventArgs</c>. Raised by WP8 when
    /// the app moves to the background under fast-app-resume. WPR has no equivalent lifecycle
    /// transition — the args type exists only so the user app's event handler signature
    /// resolves cleanly when the XAML parser binds the handler delegate.
    /// </summary>
    public class RunningInBackgroundEventArgs : EventArgs { }
}
