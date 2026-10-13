using System;

namespace WPR.SilverlightCompability
{
    public class NavigationEventArgs : EventArgs
    {
        public object? Content { get; }
        public Uri? Uri { get; }
        public NavigationMode NavigationMode { get; }

        /// <summary>WP8: true when this app started the navigation — always, under WPR.</summary>
        public bool IsNavigationInitiator => true;

        public NavigationEventArgs(object? content, Uri? uri, NavigationMode mode)
        {
            Content = content;
            Uri = uri;
            NavigationMode = mode;
        }
    }
}
