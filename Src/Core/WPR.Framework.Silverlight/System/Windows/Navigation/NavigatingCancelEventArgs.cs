using System;
using System.ComponentModel;

namespace WPR.SilverlightCompability
{
    public class NavigatingCancelEventArgs : CancelEventArgs
    {
        public Uri? Uri { get; }
        public NavigationMode NavigationMode { get; }

        public NavigatingCancelEventArgs(Uri? uri, NavigationMode mode)
        {
            Uri = uri;
            NavigationMode = mode;
        }
    }
}
