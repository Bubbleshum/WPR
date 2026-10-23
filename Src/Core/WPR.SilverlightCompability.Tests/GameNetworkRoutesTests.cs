using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WPR.Engine.Online;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// A game's HTTP calls to a retired service going to WPR Hub instead. One collection: the routes
/// and the registered WebRequest creator are process-wide.
/// </summary>
[CollectionDefinition(nameof(GameNetworkRoutesTests), DisableParallelization = true)]
public class GameNetworkRoutesCollection { }

[Collection(nameof(GameNetworkRoutesTests))]
public class GameNetworkRoutesTests
{
    private const string PoolPro = "062D24C2-D398-4CFF-9515-1F542495D8C0";

    private static HttpServiceRoute Route(string host, int? port = null, string target = "https://hub.example/svc/lobby", string? token = null) =>
        new(host, port, new Uri(target), token, null, "lobby");

    [Fact]
    public void Rewrite_keeps_path_and_query_and_matches_host_port_and_wildcards()
    {
        var routes = new[] { Route("fas.namcowireless.com"), Route("*.example.org", target: "https://hub.example/svc/other/"), Route("pinned.net", 8080) };

        Uri? to = GameNetworkRoutes.Rewrite(new Uri("http://FAS.namcowireless.com/web_http_app/server/findAvailableServer/?game_id=100&x=a%20b"), routes, out var r);
        Assert.Equal("https://hub.example/svc/lobby/web_http_app/server/findAvailableServer/?game_id=100&x=a%20b", to!.AbsoluteUri);
        Assert.Equal("lobby", r!.Service);

        Assert.Equal("https://hub.example/svc/other/a", GameNetworkRoutes.Rewrite(new Uri("http://api.example.org/a"), routes, out _)!.AbsoluteUri);
        Assert.Null(GameNetworkRoutes.Rewrite(new Uri("http://example.org/a"), routes, out _)); // *.x does not match x itself
        Assert.Null(GameNetworkRoutes.Rewrite(new Uri("http://pinned.net/a"), routes, out _));  // wrong port
        Assert.NotNull(GameNetworkRoutes.Rewrite(new Uri("http://pinned.net:8080/a"), routes, out _));
        Assert.Null(GameNetworkRoutes.Rewrite(new Uri("http://elsewhere.com/"), routes, out _));
        Assert.Null(GameNetworkRoutes.Rewrite(new Uri("ftp://fas.namcowireless.com/"), routes, out _));
    }

    [Fact]
    public void Directory_http_services_become_routes_and_sockets_are_left_alone()
    {
        var dir = new ServiceDirectory(PoolPro, new[]
        {
            new DirectoryService("pool-pro-online-3", "Pool Pro Online 3", "module", "http", "enabled", true, "optional",
                "header:X-WPR-Service-Token", new[] { new ServiceHostRule("fas.namcowireless.com", null, null) },
                new ServiceTarget("https://hub.example/svc/pool-pro-online-3", null, null), "wprst_abc", DateTimeOffset.UtcNow.AddMinutes(15)),
            new DirectoryService("pool-pro-online-3-game", "game server", "external", "tcp", "enabled", true, "optional", "preamble",
                new[] { new ServiceHostRule("game.namcowireless.com", 9000, null) }, new ServiceTarget(null, "games.example", 28000), null, null),
        }, "issued");
        var log = new List<string>();

        IReadOnlyList<HttpServiceRoute> routes = HubGameServices.ToRoutes(dir, log.Add);

        HttpServiceRoute r = Assert.Single(routes);
        Assert.Equal("fas.namcowireless.com", r.Host);
        Assert.Null(r.Port);
        Assert.Equal("wprst_abc", r.Token);
        Assert.Contains(log, l => l.Contains("tcp service at games.example:28000"));
    }

    /// <summary>
    /// The whole path, in one process: the hub's directory over HTTP, then a WP7-style
    /// WebRequest.Create to Namco's host, which must reach the hub's /svc path with the token.
    /// </summary>
    [Fact]
    public async Task A_game_request_to_a_retired_host_reaches_the_hub_with_the_players_token()
    {
        using var hub = new FakeHub();
        hub.Respond("/api/titles/" + PoolPro + "/services", "application/json",
            "{\"title_id\":\"" + PoolPro + "\",\"tokens\":\"issued\",\"services\":[{\"slug\":\"pool-pro-online-3\",\"name\":\"Pool Pro Online 3\","
            + "\"kind\":\"module\",\"protocol\":\"http\",\"status\":\"enabled\",\"healthy\":true,\"auth\":\"optional\","
            + "\"token_via\":\"header:X-WPR-Service-Token\",\"rules\":[{\"host\":\"fas.namcowireless.com\",\"port\":null,\"to_port\":null}],"
            + "\"target\":{\"url\":\"" + hub.Base + "svc/pool-pro-online-3\"},\"token\":\"wprst_player\",\"token_expires_at\":\""
            + DateTimeOffset.UtcNow.AddMinutes(15).ToString("yyyy-MM-ddTHH:mm:sszzz") + "\"}]}");
        hub.Respond("/svc/pool-pro-online-3/web_http_app/server/findAvailableServer/", "text/xml", "<appserver><result id=\"0\"/></appserver>");

        var client = new HubClient(new Uri(hub.Base)) { AccessToken = "account-token" };
        GameNetworkRoutes.Begin(PoolPro, new Directory(client));
        try
        {
#pragma warning disable SYSLIB0014
            var request = (HttpWebRequest)WebRequest.Create("http://fas.namcowireless.com/web_http_app/server/findAvailableServer/");
#pragma warning restore SYSLIB0014
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            byte[] form = Encoding.ASCII.GetBytes("game_id=100");
            using (Stream s = await request.GetRequestStreamAsync())
            {
                await s.WriteAsync(form);
            }
            using var response = (HttpWebResponse)await request.GetResponseAsync();
            string body = await new StreamReader(response.GetResponseStream()).ReadToEndAsync();

            Assert.Equal("<appserver><result id=\"0\"/></appserver>", body);
            FakeHub.Request seen = hub.Requests.Find(r => r.Path.StartsWith("/svc/"))!;
            Assert.Equal("POST", seen.Method);
            Assert.Equal("wprst_player", seen.Header("X-WPR-Service-Token"));
            Assert.Equal("game_id=100", seen.Body);
            Assert.Equal("Bearer account-token", hub.Requests.Find(r => r.Path.StartsWith("/api/"))!.Header("Authorization"));

            // Anything else goes where it was sent.
            Assert.Null(GameNetworkRoutes.Rewrite(new Uri("http://androiddrm.namcowireless.com/x"), GameNetworkRoutes.Current, out _));
        }
        finally
        {
            GameNetworkRoutes.End();
        }
        Assert.Empty(GameNetworkRoutes.Current);
    }

    [Fact]
    public async Task No_hub_or_an_unreachable_hub_leaves_requests_alone()
    {
        GameNetworkRoutes.Begin(PoolPro, null);
        await GameNetworkRoutes.WhenLoadedAsync();
        Assert.Empty(GameNetworkRoutes.Current);
        GameNetworkRoutes.End();

        var dead = new HubClient(new Uri("http://127.0.0.1:1/"));
        GameNetworkRoutes.Begin(PoolPro, new Directory(dead));
        await GameNetworkRoutes.WhenLoadedAsync();
        Assert.Empty(GameNetworkRoutes.Current);
#pragma warning disable SYSLIB0014
        Assert.Equal("fas.namcowireless.com", WebRequest.Create("http://fas.namcowireless.com/").RequestUri.Host);
#pragma warning restore SYSLIB0014
        GameNetworkRoutes.End();
    }

    private sealed class Directory(HubClient client) : IGameServiceDirectory
    {
        public async Task<IReadOnlyList<HttpServiceRoute>> GetRoutesAsync(string titleId, CancellationToken ct) =>
            HubGameServices.ToRoutes(await client.GetServiceDirectoryAsync(titleId, ct));
    }

    /// <summary>A tiny HTTP/1.1 server on loopback: canned answers by path, and a record of what came in.</summary>
    private sealed class FakeHub : IDisposable
    {
        public sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body)
        {
            public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
        }

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Dictionary<string, (string Type, string Body)> _answers = new();
        public readonly List<Request> Requests = new();

        public FakeHub()
        {
            _listener.Start();
            _ = Task.Run(Accept);
        }

        public string Base => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        public void Respond(string path, string type, string body) => _answers[path] = (type, body);

        private async Task Accept()
        {
            while (true)
            {
                TcpClient c;
                try
                {
                    c = await _listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => Serve(c));
            }
        }

        private async Task Serve(TcpClient c)
        {
            using (c)
            {
                var stream = c.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(line))
                    {
                        return;
                    }
                    string[] parts = line.Split(' ');
                    var headers = new Dictionary<string, string>();
                    for (string? h = await reader.ReadLineAsync(); !string.IsNullOrEmpty(h); h = await reader.ReadLineAsync())
                    {
                        int colon = h.IndexOf(':');
                        headers[h[..colon].Trim().ToLowerInvariant()] = h[(colon + 1)..].Trim();
                    }
                    var body = new char[headers.TryGetValue("content-length", out var len) ? int.Parse(len) : 0];
                    int read = 0;
                    while (read < body.Length)
                    {
                        read += await reader.ReadAsync(body, read, body.Length - read);
                    }
                    string path = parts[1].Split('?')[0];
                    lock (Requests)
                    {
                        Requests.Add(new Request(parts[0], path, headers, new string(body)));
                    }
                    (string type, string text) = _answers.TryGetValue(path, out var a) ? a : ("text/plain", "not found");
                    int status = _answers.ContainsKey(path) ? 200 : 404;
                    byte[] payload = Encoding.UTF8.GetBytes(text);
                    byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nContent-Length: {payload.Length}\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(payload);
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
