using System;

namespace WPR.SilverlightCompability
{
    public class NavigationEventArgs : EventArgs
    {
        public object? Content { get; }
        public Uri? Uri { get; }
        public NavigationMode NavigationMode { get; }

        public NavigationEventArgs(object? content, Uri? uri, NavigationMode mode)
        {
            Content = content;
            Uri = uri;
            NavigationMode = mode;
        }
    }
}
