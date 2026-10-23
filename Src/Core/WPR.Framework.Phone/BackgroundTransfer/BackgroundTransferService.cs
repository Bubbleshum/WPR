using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WPR.SilverlightCompability;
using Path = System.IO.Path;

namespace Microsoft.Phone.BackgroundTransfer
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.BackgroundTransfer.BackgroundTransferService</c>: the phone's
    /// queue of HTTP transfers that outlive a page (YouTube's video uploads).
    /// </summary>
    /// <remarks>
    /// <para>Transfers really run, with <see cref="HttpClient"/>: a download writes the response to
    /// <see cref="BackgroundTransferRequest.DownloadLocation"/> in the app's isolated store, an
    /// upload sends the file at <see cref="BackgroundTransferRequest.UploadLocation"/> as the body.
    /// Status and progress events are raised on the UI thread. As on the phone, both locations
    /// must be under <c>shared/transfers</c>, an upload must be a POST, and a finished transfer
    /// (failed or not) reports <see cref="TransferStatus.Completed"/> with the failure in
    /// <see cref="BackgroundTransferRequest.TransferError"/>.</para>
    ///
    /// <para>The queue lasts for the run. The phone kept requests across launches (an app restores
    /// them from <see cref="Requests"/> at start-up, as YouTube does); here a new launch starts with
    /// none, which is what the phone shows once they have finished.</para>
    /// </remarks>
    public static class BackgroundTransferService
    {
        private const string TransfersFolder = "shared/transfers";
        private static readonly List<BackgroundTransferRequest> s_requests = new();
        private static readonly HttpClient s_http = new();

        public static IEnumerable<BackgroundTransferRequest> Requests
        {
            get { lock (s_requests) return s_requests.ToArray(); }
        }

        public static BackgroundTransferRequest? Find(string requestId)
        {
            lock (s_requests) return s_requests.FirstOrDefault(r => r.RequestId == requestId);
        }

        public static void Add(BackgroundTransferRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            bool upload = request.UploadLocation != null;
            Uri? local = upload ? request.UploadLocation : request.DownloadLocation;
            if (local == null || !IsUnderTransfers(local))
                throw new InvalidOperationException($"The transfer location must be under {TransfersFolder}.");
            if (upload && !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An upload must use the POST method.");

            lock (s_requests)
            {
                if (s_requests.Contains(request))
                    throw new InvalidOperationException("The request has already been added.");
                s_requests.Add(request);
            }

            SetStatus(request, TransferStatus.Transferring);
            _ = Task.Run(() => RunAsync(request, upload, LocalPath(local)));
        }

        public static void Remove(BackgroundTransferRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (s_requests) s_requests.Remove(request);
            lock (s_cancel) { if (s_cancel.TryGetValue(request, out var cts)) cts.Cancel(); }
        }

        private static readonly Dictionary<BackgroundTransferRequest, CancellationTokenSource> s_cancel = new();

        private static async Task RunAsync(BackgroundTransferRequest request, bool upload, string localPath)
        {
            var cts = new CancellationTokenSource();
            lock (s_cancel) s_cancel[request] = cts;
            try
            {
                using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.RequestUri);
                Stream? body = null;
                if (upload)
                {
                    body = File.OpenRead(localPath);
                    request.TotalBytesToSend = body.Length;
                    message.Content = new ProgressContent(body, sent => { request.BytesSent = sent; Post(request.RaiseProgressChanged); });
                }
                foreach (KeyValuePair<string, string> header in request.Headers)
                {
                    if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using HttpResponseMessage response = await s_http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                request.StatusCode = (long)response.StatusCode;
                request.TotalBytesToReceive = response.Content.Headers.ContentLength ?? -1;

                Stream? target = null;
                if (!upload)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    target = File.Create(localPath);
                }
                using (target)
                using (Stream source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        if (target != null) await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        request.BytesReceived += read;
                        Post(request.RaiseProgressChanged);
                    }
                }
                body?.Dispose();
            }
            catch (Exception ex)
            {
                request.TransferError = ex;
                System.Diagnostics.Trace.WriteLine($"[wpr-transfer] {request.Method} {request.RequestUri} failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (s_cancel) s_cancel.Remove(request);
                SetStatus(request, TransferStatus.Completed);
            }
        }

        private static void SetStatus(BackgroundTransferRequest request, TransferStatus status)
        {
            request.TransferStatus = status;
            Post(request.RaiseStatusChanged);
        }

        private static void Post(Action action)
        {
            try { Deployment.Current.Dispatcher.BeginInvoke(action); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[wpr-transfer] event handler threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsUnderTransfers(Uri location)
        {
            string path = Normalise(location);
            return path.StartsWith(TransfersFolder + "/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, TransfersFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalise(Uri location) =>
            (location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString).Replace('\\', '/').TrimStart('/');

        private static string LocalPath(Uri location)
        {
            string relative = Normalise(location).Replace('/', Path.DirectorySeparatorChar);
            string? productId = WPR.Common.WprHostEnvironment.CurrentProductId;
            string root = string.IsNullOrEmpty(productId)
                ? Path.Combine(Path.GetTempPath(), "wpr-transfers")
                : WPR.WindowsCompability.PerGameIsolatedStorage.GameRoot(productId!);
            return Path.Combine(root, relative);
        }

        /// <summary>A request body that reports how much of it has gone.</summary>
        private sealed class ProgressContent : HttpContent
        {
            private readonly Stream _source;
            private readonly Action<long> _progress;

            public ProgressContent(Stream source, Action<long> progress)
            {
                _source = source;
                _progress = progress;
                Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            }

            protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            {
                var buffer = new byte[64 * 1024];
                long sent = 0;
                int read;
                while ((read = await _source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    sent += read;
                    _progress(sent);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _source.CanSeek ? _source.Length : -1;
                return _source.CanSeek;
            }
        }
    }
}
