using System;

namespace Microsoft.Phone.BackgroundTransfer
{
    /// <summary>Shim for <c>Microsoft.Phone.BackgroundTransfer.BackgroundTransferEventArgs</c>.</summary>
    public sealed class BackgroundTransferEventArgs : EventArgs
    {
        public BackgroundTransferEventArgs(BackgroundTransferRequest request) { Request = request; }

        public BackgroundTransferRequest Request { get; }
    }
}
