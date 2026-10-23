using System;
using System.Collections.Generic;

namespace Microsoft.Phone.BackgroundTransfer
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.BackgroundTransfer.BackgroundTransferRequest</c>: one HTTP
    /// download into, or upload from, the app's isolated storage, run by
    /// <see cref="BackgroundTransferService"/>.
    /// </summary>
    public sealed class BackgroundTransferRequest : IDisposable
    {
        public BackgroundTransferRequest(Uri requestUri)
        {
            RequestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        }

        public BackgroundTransferRequest(Uri requestUri, Uri downloadLocation) : this(requestUri)
        {
            DownloadLocation = downloadLocation;
        }

        public string RequestId { get; } = Guid.NewGuid().ToString();
        public Uri RequestUri { get; }
        public string Method { get; set; } = "GET";
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

        /// <summary>Isolated-storage path the download is written to (under <c>shared/transfers</c>).</summary>
        public Uri? DownloadLocation { get; set; }

        /// <summary>Isolated-storage path the upload is read from (under <c>shared/transfers</c>).</summary>
        public Uri? UploadLocation { get; set; }

        public string? Tag { get; set; }
        public TransferPreferences TransferPreferences { get; set; }

        public TransferStatus TransferStatus { get; internal set; } = TransferStatus.None;
        public Exception? TransferError { get; internal set; }
        public long StatusCode { get; internal set; }
        public long BytesReceived { get; internal set; }
        public long BytesSent { get; internal set; }
        public long TotalBytesToReceive { get; internal set; } = -1;
        public long TotalBytesToSend { get; internal set; }

        public event EventHandler<BackgroundTransferEventArgs>? TransferStatusChanged;
        public event EventHandler<BackgroundTransferEventArgs>? TransferProgressChanged;

        internal bool Disposed { get; private set; }

        internal void RaiseStatusChanged() => TransferStatusChanged?.Invoke(this, new BackgroundTransferEventArgs(this));

        internal void RaiseProgressChanged() => TransferProgressChanged?.Invoke(this, new BackgroundTransferEventArgs(this));

        public void Dispose() => Disposed = true;
    }
}
