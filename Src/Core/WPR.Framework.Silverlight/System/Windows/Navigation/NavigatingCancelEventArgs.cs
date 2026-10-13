using System;
using System.ComponentModel;

namespace WPR.SilverlightCompability
{
    public class NavigatingCancelEventArgs : CancelEventArgs
    {
        public Uri? Uri { get; }
        public NavigationMode NavigationMode { get; }

        /// <summary>
        /// WP8: true when this app started the navigation, false when it came from outside (a
        /// resume, a launcher). Every navigation WPR performs is the app's own. "What In The
        /// World?" reads it in its start page's OnNavigatingFrom, so without it the start page
        /// could not be navigated to at all.
        /// </summary>
        public bool IsNavigationInitiator => true;

        /// <summary>WP8: whether the navigation can be cancelled. WPR's always can.</summary>
        public bool IsCancelable => true;

        public NavigatingCancelEventArgs(Uri? uri, NavigationMode mode)
        {
            Uri = uri;
            NavigationMode = mode;
        }
    }
}
