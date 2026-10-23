using System;

namespace Microsoft.Phone.Controls
{
    /// <summary>Shim for <c>Microsoft.Phone.Controls.PivotItemEventArgs</c>.</summary>
    public class PivotItemEventArgs : EventArgs
    {
        public PivotItemEventArgs() { }

        public PivotItemEventArgs(PivotItem item) { Item = item; }

        public PivotItem? Item { get; private set; }
    }
}
