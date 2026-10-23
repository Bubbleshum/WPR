using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// One HTTP host a game calls that WPR Hub now answers for: requests to <see cref="Host"/>
    /// (and <see cref="Port"/>, when given) go to <see cref="Target"/> instead, path and query kept.
    /// </summary>
    /// <param name="Host">The game's host, e.g. <c>fas.namcowireless.com</c>; <c>*.example.com</c> matches subdomains.</param>
    /// <param name="Port">Only this port, or any when null.</param>
    /// <param name="Target">Where the request goes, e.g. <c>https://hub/svc/pool-pro-online-3</c>.</param>
    /// <param name="Token">The player's service token for this service, sent as <c>X-WPR-Service-Token</c>; null when signed out.</param>
    /// <param name="TokenExpires">When the token stops working; the routes are refreshed shortly before.</param>
    /// <param name="Service">The hub's name for the service, for the log.</param>
    public sealed record HttpServiceRoute(string Host, int? Port, Uri Target, string? Token, DateTimeOffset? TokenExpires, string Service);

    /// <summary>Where a game's online services live now. WPR.Online.Hub implements it over the hub's service directory.</summary>
    public interface IGameServiceDirectory
    {
        /// <summary>The HTTP routes for a title. Empty when the hub has none, or can't be reached.</summary>
        Task<IReadOnlyList<HttpServiceRoute>> GetRoutesAsync(string titleId, CancellationToken ct);
    }

    /// <summary>
    /// Sends a game's HTTP calls for retired online services to WPR Hub.
    /// </summary>
    /// <remarks>
    /// <para>WP7 games reach the network through <see cref="WebRequest.Create(Uri)"/> (and WebClient,
    /// which uses it), resolved to the real .NET classes. .NET lets a process register its own
    /// creator for a URL prefix, so one creator for <c>http://</c> and <c>https://</c>, registered
    /// once per process, sees every request a game makes without any IL rewriting. It consults the
    /// routes of the game being played: a request whose host matches is re-aimed at the hub with the
    /// player's service token, and anything else is created exactly as .NET would have
    /// (<see cref="WebRequest.CreateHttp(Uri)"/> does not go back through the prefix table).</para>
    ///
    /// <para>The routes come from the hub's directory, fetched when the game starts. A game asks for
    /// its first URL within a second or two of starting, so a request that arrives while the fetch
    /// is still running waits for it, up to <see cref="FirstFetchWait"/>; after that, or with no hub,
    /// requests go where the game sent them. Tokens last 15 minutes, so the routes are fetched again
    /// in the background once one is close to expiring.</para>
    ///
    /// <para>TCP is not routed here. A game learns its socket server's address from its own lobby,
    /// which for a hub-served game is a hub module, so the hub hands out the relay's address
    /// itself (Pool Pro Online 3's <c>socket_host</c> / <c>socket_port</c>).</para>
    ///
    /// <para>Per launch: <see cref="Begin"/> from the launch sequence, <see cref="End"/> at teardown.
    /// The registered creator stays for the life of the process and does nothing between games.</para>
    /// </remarks>
    public static class GameNetworkRoutes
    {
        public const string TokenHeader = "X-WPR-Service-Token";

        /// <summary>How long a request may wait for the first directory fetch.</summary>
        public static TimeSpan FirstFetchWait { get; set; } = TimeSpan.FromSeconds(5);

        private static readonly object Gate = new();
        private static int _installed;
        private static Run? _run;

        /// <summary>The routes in force for the game being played (for tests and diagnostics).</summary>
        public static IReadOnlyList<HttpServiceRoute> Current => _run?.Routes ?? Array.Empty<HttpServiceRoute>();

        /// <summary>
        /// A game is starting: fetch its routes from <paramref name="directory"/> (null = no hub, no routes).
        /// </summary>
        public static void Begin(string titleId, IGameServiceDirectory? directory)
        {
            Install();
            var run = new Run(titleId, directory);
            lock (Gate)
            {
                _run?.Cancel();
                _run = run;
            }
            run.Fetch();
        }

        /// <summary>The game has ended: requests go where they were sent again.</summary>
        public static void End()
        {
            lock (Gate)
            {
                _run?.Cancel();
                _run = null;
            }
        }

        /// <summary>Waits for the current fetch (tests).</summary>
        public static Task WhenLoadedAsync() => _run?.Loaded ?? Task.CompletedTask;

        /// <summary>
        /// The hub URL a game's request should go to, or null to leave it alone. Public so it can
        /// be tested without a network.
        /// </summary>
        public static Uri? Rewrite(Uri uri, IReadOnlyList<HttpServiceRoute> routes, out HttpServiceRoute? matched)
        {
            matched = null;
            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }
            foreach (HttpServiceRoute r in routes)
            {
                if (HostMatches(uri.Host, r.Host) && (r.Port is null || r.Port == uri.Port))
                {
                    matched = r;
                    string basePath = r.Target.AbsoluteUri.TrimEnd('/');
                    // PathAndQuery keeps the game's escaping as it sent it.
                    return new Uri(basePath + uri.PathAndQuery);
                }
            }
            return null;
        }

        private static bool HostMatches(string host, string rule)
        {
            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                string suffix = rule.Substring(1); // ".example.com"
                return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length;
            }
            return string.Equals(host, rule, StringComparison.OrdinalIgnoreCase);
        }

        private static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1)
            {
                return;
            }
#pragma warning disable SYSLIB0014 // WebRequest is obsolete for new code; it is exactly what WP7 games call.
            // Longer than .NET's own "http:"/"https:" prefixes, so these win. A game that registers
            // "http://" itself (WebRequestCreator.ClientHttp) gets false back and changes nothing.
            bool http = WebRequest.RegisterPrefix("http://", Creator.Instance);
            bool https = WebRequest.RegisterPrefix("https://", Creator.Instance);
#pragma warning restore SYSLIB0014
            if (!http || !https)
            {
                Trace.WriteLine("[wpr-net] could not take over http(s):// (already registered); game HTTP goes where it was sent");
            }
        }

        private sealed class Creator : IWebRequestCreate
        {
            public static readonly Creator Instance = new();

            public WebRequest Create(Uri uri)
            {
#pragma warning disable SYSLIB0014
                Run? run = _run;
                if (run is not null)
                {
                    IReadOnlyList<HttpServiceRoute> routes = run.RoutesForRequest();
                    Uri? target = Rewrite(uri, routes, out HttpServiceRoute? route);
                    if (target is not null && route is not null)
                    {
                        HttpWebRequest request = WebRequest.CreateHttp(target);
                        if (route.Token is not null)
                        {
                            request.Headers[TokenHeader] = route.Token;
                        }
                        run.Note(uri, target, route);
                        return request;
                    }
                }
                return WebRequest.CreateHttp(uri); // what .NET would have made
#pragma warning restore SYSLIB0014
            }
        }

        /// <summary>One game run's routes, and the fetches that keep them current.</summary>
        private sealed class Run
        {
            private readonly string _titleId;
            private readonly IGameServiceDirectory? _directory;
            private readonly CancellationTokenSource _cts = new();
            private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);
            private int _refreshing;
            private int _announced;
            private string? _summary;

            public Run(string titleId, IGameServiceDirectory? directory)
            {
                _titleId = titleId;
                _directory = directory;
            }

            public volatile IReadOnlyList<HttpServiceRoute> Routes = Array.Empty<HttpServiceRoute>();

            public Task Loaded => _loaded.Task;

            public void Cancel() => _cts.Cancel();

            public void Fetch()
            {
                if (_directory is null)
                {
                    _loaded.TrySetResult();
                    return;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        IReadOnlyList<HttpServiceRoute> routes = await _directory.GetRoutesAsync(_titleId, _cts.Token).ConfigureAwait(false);
                        if (_cts.IsCancellationRequested)
                        {
                            return;
                        }
                        bool first = !_loaded.Task.IsCompleted;
                        Routes = routes;
                        _summary = routes.Count == 0
                            ? $"[wpr-net] WPR Hub has no online services for {_titleId}"
                            : $"[wpr-net] WPR Hub serves {Describe(routes)}";
                        if (!first && routes.Count > 0)
                        {
                            Trace.WriteLine(_summary + " (refreshed)");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
                    {
                        _summary = $"[wpr-net] could not fetch online services for {_titleId} from WPR Hub: {ex.Message}";
                        Trace.WriteLine(_summary);
                    }
                    catch (OperationCanceledException)
                    {
                        // the game ended first
                    }
                    finally
                    {
                        _loaded.TrySetResult();
                        Interlocked.Exchange(ref _refreshing, 0);
                    }
                });
            }

            /// <summary>The routes, waiting briefly for the first fetch; starts a refresh when a token is about to expire.</summary>
            public IReadOnlyList<HttpServiceRoute> RoutesForRequest()
            {
                if (!_loaded.Task.IsCompleted)
                {
                    _loaded.Task.Wait(FirstFetchWait);
                }
                IReadOnlyList<HttpServiceRoute> routes = Routes;
                // Said here, at the game's first request, rather than when the fetch finished: that is
                // usually before the per-game log is attached, which would lose the line.
                if (_summary is { } summary && Interlocked.Exchange(ref _announced, 1) == 0)
                {
                    Trace.WriteLine(summary);
                }
                foreach (HttpServiceRoute r in routes)
                {
                    if (r.TokenExpires is { } expires && expires - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(2)
                        && Interlocked.Exchange(ref _refreshing, 1) == 0)
                    {
                        Fetch();
                        break;
                    }
                }
                return routes;
            }

            public void Note(Uri from, Uri to, HttpServiceRoute route)
            {
                string key = from.Host + from.AbsolutePath;
                lock (_logged)
                {
                    if (_logged.Count > 200 || !_logged.Add(key))
                    {
                        return;
                    }
                }
                Trace.WriteLine($"[wpr-net] {from.GetLeftPart(UriPartial.Path)} -> {to.GetLeftPart(UriPartial.Path)} ({route.Service}"
                    + (route.Token is null ? ", signed out)" : ", signed in)"));
            }

            private static string Describe(IReadOnlyList<HttpServiceRoute> routes)
            {
                var parts = new List<string>();
                foreach (HttpServiceRoute r in routes)
                {
                    parts.Add($"{r.Host}{(r.Port is { } p ? ":" + p : "")} -> {r.Service}");
                }
                return string.Join(", ", parts);
            }
        }
    }
}
