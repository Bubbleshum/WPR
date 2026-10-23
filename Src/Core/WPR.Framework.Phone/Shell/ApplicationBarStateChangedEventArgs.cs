using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>Shim for <c>Microsoft.Phone.Shell.ApplicationBarStateChangedEventArgs</c>.</summary>
    public class ApplicationBarStateChangedEventArgs : EventArgs
    {
        public ApplicationBarStateChangedEventArgs(bool isMenuVisible) { IsMenuVisible = isMenuVisible; }

        public bool IsMenuVisible { get; }
    }
}
