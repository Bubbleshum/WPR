using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>Shim for <c>Microsoft.Phone.Shell.ApplicationBarMenuItem</c>.</summary>
    public class ApplicationBarMenuItem : IApplicationBarMenuItem
    {
        public ApplicationBarMenuItem() { }

        public ApplicationBarMenuItem(string text) { Text = text; }

        public bool IsEnabled { get; set; } = true;

        public string Text { get; set; } = string.Empty;

        public event EventHandler? Click;

        /// <summary>For a host that draws the bar: what a tap on this item does.</summary>
        internal void RaiseClick()
        {
            if (IsEnabled) Click?.Invoke(this, EventArgs.Empty);
        }
    }
}
