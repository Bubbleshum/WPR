using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.WebBrowser</c> — WP7's embedded browser control.
    /// </summary>
    /// <remarks>
    /// <para><b>Navigates nowhere.</b> WPR embeds no browser engine, and the pages these
    /// controls were pointed at are gone: in practice a WP7 game used one for an OAuth handshake
    /// against a service that has not existed for a decade. <see cref="Navigate"/> therefore
    /// records the URI and raises nothing — no <see cref="Navigated"/>, no
    /// <see cref="NavigationFailed"/> — which leaves a title waiting for a sign-in that never
    /// completes, exactly as it would on a phone with no network. That is the outcome these
    /// games already handle; synthesising a "failed" event would instead push them down an
    /// error path nobody has tested.</para>
    ///
    /// <para>The type exists mainly so a page can be CONSTRUCTED. Cut the Rope: Experiments
    /// declares one in its GamePage.xaml — collapsed, for Facebook auth — and without the shim
    /// the TypeLoadException propagated out of <c>GamePage</c>'s generated
    /// <c>InitializeComponent</c>, failed the navigation to the start page, and left the game a
    /// black window: its loop never started because nothing ever reached
    /// <c>OnNavigatedTo</c>. One unreachable control cost the whole title.</para>
    /// </remarks>
    public class WebBrowser : Control
    {
        /// <summary>The last URI passed to <see cref="Navigate"/>; nothing fetches it.</summary>
        public Uri? Source { get; set; }

        public bool IsScriptEnabled { get; set; }

        public bool IsGeolocationEnabled { get; set; }

        public event EventHandler<NavigationEventArgs>? Navigated;

        public event EventHandler<NavigatingEventArgs>? Navigating;

        // NavigationFailedEventHandler / LoadCompletedEventHandler, NOT EventHandler<T>. An
        // event's delegate type is part of the signature a game's IL binds to, so the generic
        // form compiles here and is still a MissingMethodException in the game. Measured against
        // Flight Control Rocket, which subscribes to all four of these.
        public event NavigationFailedEventHandler? NavigationFailed;

        public event LoadCompletedEventHandler? LoadCompleted;

        public event EventHandler<NotifyEventArgs>? ScriptNotify;


        public void Navigate(Uri source)
        {
            Source = source;
        }

        public void Navigate(Uri source, byte[] postData, string additionalHeaders)
        {
            Source = source;
        }

        public void NavigateToString(string html)
        {
        }

        /// <summary>Always empty — there is no document to script against.</summary>
        public object? InvokeScript(string scriptName, params string[] args) => null;

        public string SaveToString() => string.Empty;
    }

    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.NavigatingEventArgs</c>.
    /// </summary>
    public class NavigatingEventArgs : EventArgs
    {
        public Uri? Uri { get; set; }
        public bool Cancel { get; set; }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.NotifyEventArgs</c>.
    /// </summary>
    public class NotifyEventArgs : EventArgs
    {
        public string? Value { get; set; }
    }
}
