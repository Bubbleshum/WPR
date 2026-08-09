using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.ExceptionRoutedEventArgs</c>. Silverlight uses this
    /// as the args type for failure events such as <c>Image.ImageFailed</c> and
    /// <c>MediaElement.MediaFailed</c>. The single payload is <see cref="ErrorException"/>;
    /// since our shim never raises these events at present, the field is here purely
    /// to satisfy delegate type signatures the user IL still references.
    /// </summary>
    public class ExceptionRoutedEventArgs : RoutedEventArgs
    {
        public Exception? ErrorException { get; set; }
    }
}
