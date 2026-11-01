using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Web.Http.Filters;
using Windows.Web.Http.Headers;
using WPR.WinRT;
using NetHttp = System.Net.Http;

namespace Windows.Web.Http
{
    /// <summary>Shim for <c>Windows.Web.Http.HttpCompletionOption</c>.</summary>
    public enum HttpCompletionOption
    {
        ResponseContentRead = 0,
        ResponseHeadersRead = 1,
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpProgressStage</c>.</summary>
    public enum HttpProgressStage
    {
        None = 0,
        DetectingProxy = 10,
        ResolvingName = 20,
        ConnectingToServer = 30,
        NegotiatingSsl = 40,
        SendingHeaders = 50,
        SendingContent = 60,
        WaitingForResponse = 70,
        ReceivingHeaders = 80,
        ReceivingContent = 90,
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpProgress</c>. WPR reports none.</summary>
    public struct HttpProgress
    {
        public HttpProgressStage Stage;
        public ulong BytesSent;
        public ulong? TotalBytesToSend;
        public ulong BytesReceived;
        public ulong? TotalBytesToReceive;
        public uint Retries;
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpStatusCode</c>: the HTTP status, by number.</summary>
    public enum HttpStatusCode
    {
        None = 0,
        Continue = 100,
        SwitchingProtocols = 101,
        Processing = 102,
        Ok = 200,
        Created = 201,
        Accepted = 202,
        NonAuthoritativeInformation = 203,
        NoContent = 204,
        ResetContent = 205,
        PartialContent = 206,
        MultipleChoices = 300,
        MovedPermanently = 301,
        Found = 302,
        SeeOther = 303,
        NotModified = 304,
        UseProxy = 305,
        TemporaryRedirect = 307,
        PermanentRedirect = 308,
        BadRequest = 400,
        Unauthorized = 401,
        PaymentRequired = 402,
        Forbidden = 403,
        NotFound = 404,
        MethodNotAllowed = 405,
        NotAcceptable = 406,
        ProxyAuthenticationRequired = 407,
        RequestTimeout = 408,
        Conflict = 409,
        Gone = 410,
        LengthRequired = 411,
        PreconditionFailed = 412,
        RequestEntityTooLarge = 413,
        RequestUriTooLong = 414,
        UnsupportedMediaType = 415,
        RequestedRangeNotSatisfiable = 416,
        ExpectationFailed = 417,
        UnprocessableEntity = 422,
        Locked = 423,
        FailedDependency = 424,
        UpgradeRequired = 426,
        PreconditionRequired = 428,
        TooManyRequests = 429,
        RequestHeaderFieldsTooLarge = 431,
        InternalServerError = 500,
        NotImplemented = 501,
        BadGateway = 502,
        ServiceUnavailable = 503,
        GatewayTimeout = 504,
        HttpVersionNotSupported = 505,
        VariantAlsoNegotiates = 506,
        InsufficientStorage = 507,
        LoopDetected = 508,
        NotExtended = 510,
        NetworkAuthenticationRequired = 511,
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpVersion</c>.</summary>
    public enum HttpVersion
    {
        None = 0,
        Http10 = 1,
        Http11 = 2,
        Http20 = 3,
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpResponseMessageSource</c>.</summary>
    public enum HttpResponseMessageSource
    {
        None = 0,
        Cache = 1,
        Network = 2,
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpMethod</c>.</summary>
    public sealed class HttpMethod
    {
        public HttpMethod(string method) => Method = method ?? throw new ArgumentNullException(nameof(method));

        public string Method { get; }

        public static HttpMethod Delete { get; } = new("DELETE");

        public static HttpMethod Get { get; } = new("GET");

        public static HttpMethod Head { get; } = new("HEAD");

        public static HttpMethod Options { get; } = new("OPTIONS");

        public static HttpMethod Patch { get; } = new("PATCH");

        public static HttpMethod Post { get; } = new("POST");

        public static HttpMethod Put { get; } = new("PUT");

        public override string ToString() => Method;
    }

    /// <summary>Shim for <c>Windows.Web.Http.IHttpContent</c>.</summary>
    public interface IHttpContent : IDisposable
    {
        HttpContentHeaderCollection Headers { get; }

        IAsyncOperationWithProgress<ulong, ulong> BufferAllAsync();

        IAsyncOperationWithProgress<IBuffer, ulong> ReadAsBufferAsync();

        IAsyncOperationWithProgress<IInputStream, ulong> ReadAsInputStreamAsync();

        IAsyncOperationWithProgress<string, ulong> ReadAsStringAsync();

        bool TryComputeLength(out ulong length);

        IAsyncOperationWithProgress<ulong, ulong> WriteToStreamAsync(IOutputStream outputStream);
    }

    /// <summary>
    /// The shared body of WPR's <see cref="IHttpContent"/> classes: the bytes are known up front
    /// (a string, a buffer, a response already read), so every read answers at once.
    /// </summary>
    public abstract class WprHttpContent : IHttpContent
    {
        private protected WprHttpContent()
        {
        }

        public HttpContentHeaderCollection Headers { get; } = new();

        /// <summary>The body. Multipart content builds it from its parts when asked.</summary>
        internal abstract byte[] Bytes { get; }

        internal virtual NetHttp.HttpContent ToNet()
        {
            var content = new NetHttp.ByteArrayContent(Bytes);
            Headers.CopyTo(content.Headers);
            return content;
        }

        public IAsyncOperationWithProgress<ulong, ulong> BufferAllAsync() => Done((ulong)Bytes.Length);

        public IAsyncOperationWithProgress<IBuffer, ulong> ReadAsBufferAsync() => Done(Bytes.AsBuffer());

        public IAsyncOperationWithProgress<IInputStream, ulong> ReadAsInputStreamAsync() =>
            Done<IInputStream>(new RandomAccessStream(new MemoryStream(Bytes, writable: false)));

        public IAsyncOperationWithProgress<string, ulong> ReadAsStringAsync() => Done(Decode(Bytes, Headers.ContentType?.CharSet));

        public bool TryComputeLength(out ulong length)
        {
            length = (ulong)Bytes.Length;
            return true;
        }

        public IAsyncOperationWithProgress<ulong, ulong> WriteToStreamAsync(IOutputStream outputStream) =>
            new TaskAsyncOperationWithProgress<ulong, ulong>(Task.Run(async () =>
            {
                await outputStream.WriteAsync(Bytes.AsBuffer()).AsTask().ConfigureAwait(false);
                return (ulong)Bytes.Length;
            }));

        public virtual void Dispose()
        {
        }

        public override string ToString() => Decode(Bytes, Headers.ContentType?.CharSet);

        private static IAsyncOperationWithProgress<T, ulong> Done<T>(T value) =>
            new TaskAsyncOperationWithProgress<T, ulong>(Task.FromResult(value));

        /// <summary>WinRT reads text as the charset says, UTF-8 when it says nothing, and drops a BOM.</summary>
        internal static string Decode(byte[] bytes, string? charSet)
        {
            Encoding encoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(charSet))
            {
                try { encoding = Encoding.GetEncoding(charSet.Trim('"')); }
                catch (ArgumentException) { }
            }

            using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpStringContent</c>.</summary>
    public sealed class HttpStringContent : WprHttpContent
    {
        private readonly byte[] _bytes;

        public HttpStringContent(string content) : this(content, UnicodeEncoding.Utf8)
        {
        }

        public HttpStringContent(string content, UnicodeEncoding encoding) : this(content, encoding, "text/plain")
        {
        }

        public HttpStringContent(string content, UnicodeEncoding encoding, string mediaType)
        {
            Encoding net = encoding switch
            {
                UnicodeEncoding.Utf16LE => new global::System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: false),
                UnicodeEncoding.Utf16BE => new global::System.Text.UnicodeEncoding(bigEndian: true, byteOrderMark: false),
                _ => new UTF8Encoding(false),
            };
            _bytes = net.GetBytes(content ?? string.Empty);
            Headers.put_ContentType(new HttpMediaTypeHeaderValue(mediaType) { CharSet = net.WebName.ToUpperInvariant() });
        }

        internal override byte[] Bytes => _bytes;
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpBufferContent</c>.</summary>
    public sealed class HttpBufferContent : WprHttpContent
    {
        private readonly byte[] _bytes;

        public HttpBufferContent(IBuffer content) => _bytes = content.ToArray();

        public HttpBufferContent(IBuffer content, uint offset, uint count) => _bytes = content.ToArray(offset, (int)count);

        internal override byte[] Bytes => _bytes;
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpStreamContent</c>: the stream is read when the content is made.</summary>
    public sealed class HttpStreamContent : WprHttpContent
    {
        private readonly byte[] _bytes;

        public HttpStreamContent(IInputStream content)
        {
            using var copy = new MemoryStream();
            content.AsStreamForRead().CopyTo(copy);
            _bytes = copy.ToArray();
        }

        internal override byte[] Bytes => _bytes;
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpFormUrlEncodedContent</c>.</summary>
    public sealed class HttpFormUrlEncodedContent : WprHttpContent
    {
        private readonly byte[] _bytes;

        public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> content)
        {
            string body = string.Join("&", (content ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Select(p => Uri.EscapeDataString(p.Key ?? string.Empty).Replace("%20", "+") + "=" +
                             Uri.EscapeDataString(p.Value ?? string.Empty).Replace("%20", "+")));
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.put_ContentType(new HttpMediaTypeHeaderValue("application/x-www-form-urlencoded"));
        }

        internal override byte[] Bytes => _bytes;
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpMultipartContent</c> and its form-data subclass's parts.</summary>
    public class HttpMultipartContent : WprHttpContent, IEnumerable<IHttpContent>
    {
        private protected readonly List<(IHttpContent Content, string? Name, string? FileName)> Parts = new();
        private readonly string _subtype;
        private readonly string _boundary;

        public HttpMultipartContent() : this("mixed", Guid.NewGuid().ToString())
        {
        }

        public HttpMultipartContent(string subtype) : this(subtype, Guid.NewGuid().ToString())
        {
        }

        public HttpMultipartContent(string subtype, string boundary)
        {
            _subtype = subtype;
            _boundary = boundary;
        }

        public void Add(IHttpContent content) => Parts.Add((content, null, null));

        internal override byte[] Bytes
        {
            get
            {
                using var net = ToNet();
                return net.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
        }

        internal override NetHttp.HttpContent ToNet()
        {
            NetHttp.MultipartContent multi = _subtype == "form-data"
                ? new NetHttp.MultipartFormDataContent(_boundary)
                : new NetHttp.MultipartContent(_subtype, _boundary);
            foreach (var (content, name, fileName) in Parts)
            {
                NetHttp.HttpContent part = content is WprHttpContent own
                    ? own.ToNet()
                    : new NetHttp.ByteArrayContent(content.ReadAsBufferAsync().AsTask().GetAwaiter().GetResult().ToArray());
                if (multi is NetHttp.MultipartFormDataContent form && name != null)
                {
                    if (fileName != null) form.Add(part, name, fileName);
                    else form.Add(part, name);
                }
                else
                {
                    multi.Add(part);
                }
            }

            return multi;
        }

        public IEnumerator<IHttpContent> GetEnumerator() => Parts.Select(p => p.Content).GetEnumerator();

        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpMultipartFormDataContent</c>.</summary>
    public sealed class HttpMultipartFormDataContent : HttpMultipartContent
    {
        public HttpMultipartFormDataContent() : base("form-data")
        {
        }

        public HttpMultipartFormDataContent(string boundary) : base("form-data", boundary)
        {
        }

        public void Add(IHttpContent content, string name) => Parts.Add((content, name, null));

        public void Add(IHttpContent content, string name, string fileName) => Parts.Add((content, name, fileName));
    }

    /// <summary>A response body, read whole when the response arrived.</summary>
    internal sealed class ResponseContent : WprHttpContent
    {
        private readonly byte[] _bytes;

        internal ResponseContent(byte[] bytes) => _bytes = bytes;

        internal override byte[] Bytes => _bytes;
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpRequestMessage</c>.</summary>
    public sealed class HttpRequestMessage : IDisposable
    {
        public HttpRequestMessage() : this(HttpMethod.Get, null)
        {
        }

        public HttpRequestMessage(HttpMethod method, Uri? uri)
        {
            Method = method;
            RequestUri = uri;
        }

        public HttpMethod Method { get; internal set; }

        public void put_Method(HttpMethod value) => Method = value;

        public Uri? RequestUri { get; internal set; }

        public void put_RequestUri(Uri value) => RequestUri = value;

        public IHttpContent? Content { get; internal set; }

        public void put_Content(IHttpContent value) => Content = value;

        public HttpRequestHeaderCollection Headers { get; } = new();

        public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        public void Dispose() => Content?.Dispose();

        public override string ToString() => $"{Method} {RequestUri}";
    }

    /// <summary>Shim for <c>Windows.Web.Http.HttpResponseMessage</c>.</summary>
    public sealed class HttpResponseMessage : IDisposable
    {
        public HttpResponseMessage() : this(HttpStatusCode.Ok)
        {
        }

        public HttpResponseMessage(HttpStatusCode statusCode)
        {
            StatusCode = statusCode;
            Content = new ResponseContent(Array.Empty<byte>());
        }

        public HttpStatusCode StatusCode { get; internal set; }

        public void put_StatusCode(HttpStatusCode value) => StatusCode = value;

        public string ReasonPhrase { get; internal set; } = string.Empty;

        public void put_ReasonPhrase(string value) => ReasonPhrase = value;

        public IHttpContent Content { get; internal set; }

        public void put_Content(IHttpContent value) => Content = value;

        public HttpResponseHeaderCollection Headers { get; } = new();

        public HttpRequestMessage? RequestMessage { get; internal set; }

        public void put_RequestMessage(HttpRequestMessage value) => RequestMessage = value;

        public HttpResponseMessageSource Source { get; internal set; } = HttpResponseMessageSource.Network;

        public void put_Source(HttpResponseMessageSource value) => Source = value;

        public HttpVersion Version { get; internal set; } = HttpVersion.Http11;

        public void put_Version(HttpVersion value) => Version = value;

        public bool IsSuccessStatusCode => (int)StatusCode >= 200 && (int)StatusCode <= 299;

        /// <summary>As WinRT: throws for anything outside 2xx, the HRESULT carrying the status.</summary>
        public HttpResponseMessage EnsureSuccessStatusCode()
        {
            if (!IsSuccessStatusCode)
            {
                throw new Exception($"Response status code does not indicate success: {(int)StatusCode} ({ReasonPhrase}).")
                {
                    HResult = unchecked((int)0x80190000) | (int)StatusCode,
                };
            }

            return this;
        }

        public void Dispose() => Content.Dispose();

        public override string ToString() => $"StatusCode: {(int)StatusCode}, ReasonPhrase: '{ReasonPhrase}'";
    }

    /// <summary>
    /// Shim for <c>Windows.Web.Http.HttpClient</c>, over .NET's. WinRT's differs from .NET's in
    /// ways titles rely on, and those are kept: a non-2xx status is a response, not an exception;
    /// bodies are decompressed; the filter's settings (redirects, cookies) are the handler's. The
    /// body is read whole before the operation completes, whatever the completion option.
    /// </summary>
    public sealed class HttpClient : IDisposable
    {
        private readonly NetHttp.HttpClient _client;

        public HttpClient() : this(new HttpBaseProtocolFilter())
        {
        }

        public HttpClient(IHttpFilter filter)
        {
            var settings = filter as HttpBaseProtocolFilter ?? new HttpBaseProtocolFilter();
            var handler = new NetHttp.HttpClientHandler
            {
                AllowAutoRedirect = settings.AllowAutoRedirect,
                AutomaticDecompression = settings.AutomaticDecompression ? DecompressionMethods.All : DecompressionMethods.None,
                UseCookies = settings.CookieUsageBehavior == HttpCookieUsageBehavior.Default,
            };
            _client = new NetHttp.HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public HttpRequestHeaderCollection DefaultRequestHeaders { get; } = new();

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> GetAsync(Uri uri) =>
            SendRequestAsync(new HttpRequestMessage(HttpMethod.Get, uri));

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> GetAsync(Uri uri, HttpCompletionOption completionOption) =>
            SendRequestAsync(new HttpRequestMessage(HttpMethod.Get, uri), completionOption);

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> DeleteAsync(Uri uri) =>
            SendRequestAsync(new HttpRequestMessage(HttpMethod.Delete, uri));

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> PostAsync(Uri uri, IHttpContent content) =>
            SendRequestAsync(new HttpRequestMessage(HttpMethod.Post, uri) { Content = content });

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> PutAsync(Uri uri, IHttpContent content) =>
            SendRequestAsync(new HttpRequestMessage(HttpMethod.Put, uri) { Content = content });

        public IAsyncOperationWithProgress<string, HttpProgress> GetStringAsync(Uri uri) =>
            Then(GetAsync(uri), r => r.EnsureSuccessStatusCode().Content.ReadAsStringAsync().AsTask());

        public IAsyncOperationWithProgress<IBuffer, HttpProgress> GetBufferAsync(Uri uri) =>
            Then(GetAsync(uri), r => r.EnsureSuccessStatusCode().Content.ReadAsBufferAsync().AsTask());

        public IAsyncOperationWithProgress<IInputStream, HttpProgress> GetInputStreamAsync(Uri uri) =>
            Then(GetAsync(uri), r => r.EnsureSuccessStatusCode().Content.ReadAsInputStreamAsync().AsTask());

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request) =>
            SendRequestAsync(request, HttpCompletionOption.ResponseContentRead);

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request, HttpCompletionOption completionOption)
        {
            var cancel = new CancellationTokenSource();
            return new TaskAsyncOperationWithProgress<HttpResponseMessage, HttpProgress>(Send(request, cancel.Token), cancel);
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancel)
        {
            using var net = new NetHttp.HttpRequestMessage(new NetHttp.HttpMethod(request.Method.Method), request.RequestUri);
            if (request.Content is WprHttpContent own)
            {
                net.Content = own.ToNet();
            }
            else if (request.Content != null)
            {
                IBuffer body = await request.Content.ReadAsBufferAsync().AsTask().ConfigureAwait(false);
                net.Content = new NetHttp.ByteArrayContent(body.ToArray());
                request.Content.Headers.CopyTo(net.Content.Headers);
            }

            DefaultRequestHeaders.CopyTo(net.Headers);
            request.Headers.CopyTo(net.Headers);

            try
            {
                using var reply = await _client.SendAsync(net, NetHttp.HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
                byte[] bytes = await reply.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                var response = new HttpResponseMessage((HttpStatusCode)(int)reply.StatusCode)
                {
                    ReasonPhrase = reply.ReasonPhrase ?? string.Empty,
                    RequestMessage = request,
                    Content = new ResponseContent(bytes),
                };
                response.Headers.CopyFrom(reply.Headers);
                ((ResponseContent)response.Content).Headers.CopyFrom(reply.Content.Headers);
                Log($"{request.Method} {request.RequestUri} -> {(int)reply.StatusCode}, {bytes.Length} bytes");
                return response;
            }
            catch (NetHttp.HttpRequestException ex)
            {
                Log($"{request.Method} {request.RequestUri} failed: {ex.Message}");
                // WinRT reported a network failure as an HRESULT the title can map
                // (WebError.GetStatus); 0x80072EE7 is "the server name could not be resolved",
                // the usual meaning of a dead host.
                throw new Exception(ex.Message, ex) { HResult = unchecked((int)0x80072EE7) };
            }
        }

        private static IAsyncOperationWithProgress<T, HttpProgress> Then<T>(
            IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> first, Func<HttpResponseMessage, Task<T>> next) =>
            new TaskAsyncOperationWithProgress<T, HttpProgress>(Task.Run(async () =>
            {
                using HttpResponseMessage response = await first.AsTask().ConfigureAwait(false);
                return await next(response).ConfigureAwait(false);
            }));

        private static void Log(string line) => global::System.Diagnostics.Trace.WriteLine("[wpr-http] " + line);

        public void Dispose() => _client.Dispose();

        public override string ToString() => nameof(HttpClient);
    }
}

namespace Windows.Web.Http.Filters
{
    /// <summary>Shim for <c>Windows.Web.Http.Filters.IHttpFilter</c>.</summary>
    public interface IHttpFilter : IDisposable
    {
        IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request);
    }

    /// <summary>Shim for <c>Windows.Web.Http.Filters.HttpCookieUsageBehavior</c>.</summary>
    public enum HttpCookieUsageBehavior
    {
        Default = 0,
        NoCookies = 1,
    }

    /// <summary>Shim for <c>Windows.Web.Http.Filters.HttpCacheReadBehavior</c>.</summary>
    public enum HttpCacheReadBehavior
    {
        Default = 0,
        MostRecent = 1,
        OnlyFromCache = 2,
    }

    /// <summary>Shim for <c>Windows.Web.Http.Filters.HttpCacheWriteBehavior</c>.</summary>
    public enum HttpCacheWriteBehavior
    {
        Default = 0,
        NoCache = 1,
    }

    /// <summary>Shim for <c>Windows.Web.Http.Filters.HttpCacheControl</c>. WPR keeps no cache.</summary>
    public sealed class HttpCacheControl
    {
        internal HttpCacheControl()
        {
        }

        public HttpCacheReadBehavior ReadBehavior { get; internal set; }

        public void put_ReadBehavior(HttpCacheReadBehavior value) => ReadBehavior = value;

        public HttpCacheWriteBehavior WriteBehavior { get; internal set; }

        public void put_WriteBehavior(HttpCacheWriteBehavior value) => WriteBehavior = value;
    }

    /// <summary>
    /// Shim for <c>Windows.Web.Http.Filters.HttpBaseProtocolFilter</c>: the settings an
    /// <see cref="HttpClient"/> made over it uses. As a filter on its own it sends through a
    /// default client.
    /// </summary>
    public sealed class HttpBaseProtocolFilter : IHttpFilter
    {
        public bool AllowAutoRedirect { get; internal set; } = true;

        public void put_AllowAutoRedirect(bool value) => AllowAutoRedirect = value;

        public bool AllowUI { get; internal set; } = true;

        public void put_AllowUI(bool value) => AllowUI = value;

        public bool AutomaticDecompression { get; internal set; } = true;

        public void put_AutomaticDecompression(bool value) => AutomaticDecompression = value;

        public HttpCacheControl CacheControl { get; } = new();

        public HttpCookieUsageBehavior CookieUsageBehavior { get; internal set; }

        public void put_CookieUsageBehavior(HttpCookieUsageBehavior value) => CookieUsageBehavior = value;

        public uint MaxConnectionsPerServer { get; internal set; } = 6;

        public void put_MaxConnectionsPerServer(uint value) => MaxConnectionsPerServer = value;

        public bool UseProxy { get; internal set; } = true;

        public void put_UseProxy(bool value) => UseProxy = value;

        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request) =>
            new HttpClient(this).SendRequestAsync(request);

        public void Dispose()
        {
        }
    }
}

namespace Windows.Web.Http.Headers
{
    /// <summary>
    /// What WinRT's header collections share: an <c>IMap&lt;string, string&gt;</c> (projected as
    /// a dictionary), case-insensitive, plus the typed properties of each subclass, which are
    /// stored in it as text.
    /// </summary>
    public abstract class WprHttpHeaderCollection : IDictionary<string, string>
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        private protected WprHttpHeaderCollection()
        {
        }

        public string this[string key]
        {
            get => _values[key];
            set => _values[key] = value;
        }

        public ICollection<string> Keys => _values.Keys;

        public ICollection<string> Values => _values.Values;

        public int Count => _values.Count;

        public bool IsReadOnly => false;

        public uint Size => (uint)_values.Count;

        public void Add(string key, string value) => _values[key] = value;

        public void Append(string name, string value) =>
            _values[name] = _values.TryGetValue(name, out string? old) && old.Length > 0 ? old + ", " + value : value;

        public bool TryAppendWithoutValidation(string name, string value)
        {
            Append(name, value);
            return true;
        }

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool Remove(string key) => _values.Remove(key);

        public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

        public void Clear() => _values.Clear();

        void ICollection<KeyValuePair<string, string>>.Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

        bool ICollection<KeyValuePair<string, string>>.Contains(KeyValuePair<string, string> item) =>
            ((ICollection<KeyValuePair<string, string>>)_values).Contains(item);

        void ICollection<KeyValuePair<string, string>>.CopyTo(KeyValuePair<string, string>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<string, string>>)_values).CopyTo(array, arrayIndex);

        bool ICollection<KeyValuePair<string, string>>.Remove(KeyValuePair<string, string> item) =>
            ((ICollection<KeyValuePair<string, string>>)_values).Remove(item);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Snapshot().GetEnumerator();

        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>The values, typed properties included, as they go on the wire.</summary>
        private protected virtual IEnumerable<KeyValuePair<string, string>> Snapshot() => _values.ToList();

        internal void CopyTo(global::System.Net.Http.Headers.HttpHeaders target)
        {
            foreach (var (name, value) in Snapshot())
            {
                target.Remove(name);
                target.TryAddWithoutValidation(name, value);
            }
        }

        internal void CopyFrom(global::System.Net.Http.Headers.HttpHeaders source)
        {
            foreach (var header in source)
            {
                _values[header.Key] = string.Join(", ", header.Value);
            }
        }

        private protected string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;

        private protected void Set(string name, object? value)
        {
            if (value == null) _values.Remove(name);
            else _values[name] = value.ToString()!;
        }

        public override string ToString() => string.Join("\r\n", Snapshot().Select(p => p.Key + ": " + p.Value));
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpRequestHeaderCollection</c>.</summary>
    public sealed class HttpRequestHeaderCollection : WprHttpHeaderCollection
    {
        public HttpMediaTypeWithQualityHeaderValueCollection Accept { get; } = new();

        public HttpCredentialsHeaderValue? Authorization =>
            Get("Authorization") is string a ? HttpCredentialsHeaderValue.Parse(a) : null;

        public void put_Authorization(HttpCredentialsHeaderValue? value) => Set("Authorization", value);

        public Uri? Referer => Get("Referer") is string r ? new Uri(r, UriKind.RelativeOrAbsolute) : null;

        public void put_Referer(Uri? value) => Set("Referer", value);

        public string? Host => Get("Host");

        public HttpProductInfoHeaderValueCollection UserAgent { get; } = new();

        private protected override IEnumerable<KeyValuePair<string, string>> Snapshot()
        {
            var all = base.Snapshot().ToList();
            if (Accept.Count > 0) all.Add(new("Accept", Accept.ToString()));
            if (UserAgent.Count > 0) all.Add(new("User-Agent", UserAgent.ToString()));
            return all;
        }
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpResponseHeaderCollection</c>.</summary>
    public sealed class HttpResponseHeaderCollection : WprHttpHeaderCollection
    {
        public Uri? Location => Get("Location") is string l ? new Uri(l, UriKind.RelativeOrAbsolute) : null;

        public void put_Location(Uri? value) => Set("Location", value);

        public TimeSpan? Age => Get("Age") is string a && int.TryParse(a, out int s) ? TimeSpan.FromSeconds(s) : null;
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpContentHeaderCollection</c>.</summary>
    public sealed class HttpContentHeaderCollection : WprHttpHeaderCollection
    {
        public HttpMediaTypeHeaderValue? ContentType =>
            Get("Content-Type") is string t ? HttpMediaTypeHeaderValue.Parse(t) : null;

        public void put_ContentType(HttpMediaTypeHeaderValue? value) => Set("Content-Type", value);

        public ulong? ContentLength => Get("Content-Length") is string l && ulong.TryParse(l, out ulong n) ? n : null;

        public void put_ContentLength(ulong? value) => Set("Content-Length", value);
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpNameValueHeaderValue</c>.</summary>
    public sealed class HttpNameValueHeaderValue
    {
        public HttpNameValueHeaderValue(string name) : this(name, null)
        {
        }

        public HttpNameValueHeaderValue(string name, string? value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public string? Value { get; internal set; }

        public void put_Value(string? value) => Value = value;

        public override string ToString() => Value == null ? Name : Name + "=" + Value;
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpMediaTypeHeaderValue</c>.</summary>
    public sealed class HttpMediaTypeHeaderValue
    {
        public HttpMediaTypeHeaderValue(string mediaType) => MediaType = mediaType;

        public string MediaType { get; internal set; }

        public void put_MediaType(string value) => MediaType = value;

        public string? CharSet { get; set; }

        public void put_CharSet(string? value) => CharSet = value;

        public IList<HttpNameValueHeaderValue> Parameters { get; } = new List<HttpNameValueHeaderValue>();

        public static HttpMediaTypeHeaderValue Parse(string input)
        {
            string[] parts = input.Split(';');
            var value = new HttpMediaTypeHeaderValue(parts[0].Trim());
            foreach (string part in parts.Skip(1))
            {
                string[] pair = part.Split('=', 2);
                string name = pair[0].Trim();
                string? v = pair.Length > 1 ? pair[1].Trim() : null;
                if (name.Equals("charset", StringComparison.OrdinalIgnoreCase)) value.CharSet = v;
                else value.Parameters.Add(new HttpNameValueHeaderValue(name, v));
            }

            return value;
        }

        public static bool TryParse(string input, out HttpMediaTypeHeaderValue? result)
        {
            result = string.IsNullOrWhiteSpace(input) ? null : Parse(input);
            return result != null;
        }

        public override string ToString() =>
            MediaType + (CharSet != null ? "; charset=" + CharSet : "") + string.Concat(Parameters.Select(p => "; " + p));
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpMediaTypeWithQualityHeaderValue</c>.</summary>
    public sealed class HttpMediaTypeWithQualityHeaderValue
    {
        public HttpMediaTypeWithQualityHeaderValue(string mediaType) => MediaType = mediaType;

        public HttpMediaTypeWithQualityHeaderValue(string mediaType, double quality) : this(mediaType) => Quality = quality;

        public string MediaType { get; internal set; }

        public void put_MediaType(string value) => MediaType = value;

        public double? Quality { get; internal set; }

        public void put_Quality(double? value) => Quality = value;

        public string? CharSet { get; internal set; }

        public void put_CharSet(string? value) => CharSet = value;

        public static HttpMediaTypeWithQualityHeaderValue Parse(string input) => new(input.Split(';')[0].Trim());

        public override string ToString() =>
            MediaType + (CharSet != null ? "; charset=" + CharSet : "") +
            (Quality is double q ? "; q=" + q.ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture) : "");
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpMediaTypeWithQualityHeaderValueCollection</c>.</summary>
    public sealed class HttpMediaTypeWithQualityHeaderValueCollection : global::System.Collections.ObjectModel.Collection<HttpMediaTypeWithQualityHeaderValue>
    {
        public void ParseAdd(string input)
        {
            foreach (string part in input.Split(','))
            {
                if (!string.IsNullOrWhiteSpace(part)) Add(HttpMediaTypeWithQualityHeaderValue.Parse(part));
            }
        }

        public bool TryParseAdd(string input)
        {
            ParseAdd(input);
            return true;
        }

        public override string ToString() => string.Join(", ", this);
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpProductInfoHeaderValueCollection</c>, kept as text.</summary>
    public sealed class HttpProductInfoHeaderValueCollection : global::System.Collections.ObjectModel.Collection<string>
    {
        public void ParseAdd(string input) => Add(input);

        public bool TryParseAdd(string input)
        {
            Add(input);
            return true;
        }

        public override string ToString() => string.Join(" ", this);
    }

    /// <summary>Shim for <c>Windows.Web.Http.Headers.HttpCredentialsHeaderValue</c>.</summary>
    public sealed class HttpCredentialsHeaderValue
    {
        public HttpCredentialsHeaderValue(string scheme) : this(scheme, null)
        {
        }

        public HttpCredentialsHeaderValue(string scheme, string? token)
        {
            Scheme = scheme;
            Token = token;
        }

        public string Scheme { get; }

        public string? Token { get; }

        public static HttpCredentialsHeaderValue Parse(string input)
        {
            string[] parts = input.Trim().Split(' ', 2);
            return new HttpCredentialsHeaderValue(parts[0], parts.Length > 1 ? parts[1] : null);
        }

        public override string ToString() => Token == null ? Scheme : Scheme + " " + Token;
    }
}
