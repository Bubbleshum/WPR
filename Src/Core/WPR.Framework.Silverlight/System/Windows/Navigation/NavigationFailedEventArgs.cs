using System;

namespace WPR.SilverlightCompability
{
    public class NavigationFailedEventArgs : EventArgs
    {
        public Uri? Uri { get; }
        public Exception? Exception { get; }
        public bool Handled { get; set; }

        public NavigationFailedEventArgs(Uri? uri, Exception? exception)
        {
            Uri = uri;
            Exception = exception;
        }
    }
}
