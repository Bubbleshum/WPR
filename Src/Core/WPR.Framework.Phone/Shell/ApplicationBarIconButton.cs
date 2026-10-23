using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>Shim for <c>Microsoft.Phone.Shell.ApplicationBarIconButton</c>.</summary>
    public class ApplicationBarIconButton : ApplicationBarMenuItem, IApplicationBarIconButton
    {
        public ApplicationBarIconButton() { }

        public ApplicationBarIconButton(Uri iconUri) { IconUri = iconUri; }

        public Uri IconUri { get; set; } = null!;
    }
}
