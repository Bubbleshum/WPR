using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WPR.Engine.Online;
using WPR.Online.Hub;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>Installed games' names and icons offered to WPR Hub (HubTitleDetails).</summary>
public sealed class HubTitleDetailsTests : IDisposable
{
    private const string Id = "{d289b7d1-60d9-df11-a844-00237de2db9e}";
    private const string HubId = "D289B7D1-60D9-DF11-A844-00237DE2DB9E";

    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-titledetails-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHub _hub = new();
    private bool _serverLogging = true;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Answers the two details calls like the hub: the first contribution fills the title.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();
        public bool HasIcon { get; set; }
        public bool HasName { get; set; }
        public HttpStatusCode? Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            if (Fail is { } status) return new HttpResponseMessage(status) { Content = Json(new { message = "no" }) };

            if (request.Method == HttpMethod.Post)
            {
                using JsonDocument doc = JsonDocument.Parse(body!);
                List<string> accepted = new();
                if (!HasName && doc.RootElement.TryGetProperty("name", out _)) { HasName = true; accepted.Add("name"); }
                if (!HasIcon && doc.RootElement.TryGetProperty("icon", out _)) { HasIcon = true; accepted.Add("icon"); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new { title_id = HubId, accepted, icon_url = (string?)null, wants = Wants() }) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new { title_id = HubId, known = HasName, name = (string?)null, icon_url = (string?)null, wants = Wants() }) };
        }

        private object Wants() => new { icon = !HasIcon, name = !HasName, publisher = false, developer = false, platform = false };

        private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    }

    private HubTitleDetails Create(Func<IEnumerable<TitleDetails>>? installed = null) => new(new HubSettings
    {
        BaseUri = () => new Uri("http://hub.test/"),
        AccessToken = () => null,
        ServerLogging = () => _serverLogging,
        DataDirectory = _root,
        WprVersion = "test",
        Platform = "windows",
        InstalledTitles = installed,
        Handler = _hub,
    });

    private string Icon(byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] Png = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    private TitleDetails Game(string? name = "Guitar Hero 5", string? icon = null) =>
        new(Id, name, "Glu", "Glu Mobile", "Windows Phone 7", icon ?? Icon(Png));

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task An_install_sends_what_the_hub_lacks_once()
    {
        HubTitleDetails sync = Create();
        sync.Offer(Game());
        Assert.Empty(_hub.Requests); // queued only

        await sync.FlushAsync();

        Assert.Equal(2, _hub.Requests.Count);
        Assert.Equal(HttpMethod.Get, _hub.Requests[0].Method);
        Assert.Equal($"/api/titles/{HubId}/details", _hub.Requests[0].Path);
        JsonElement sent = Body(_hub.Requests[1].Body!);
        Assert.Equal("Guitar Hero 5", sent.GetProperty("name").GetString());
        Assert.Equal(Convert.ToBase64String(Png), sent.GetProperty("icon").GetString());
        // The hub said it has a publisher: not sent.
        Assert.False(sent.TryGetProperty("publisher", out _));

        // A second install (or a repatch) of the same title sends nothing, not even the question.
        sync.Offer(Game());
        await sync.FlushAsync();
        await Create().FlushAsync();
        Assert.Equal(2, _hub.Requests.Count);
    }

    [Fact]
    public async Task Nothing_is_posted_when_the_hub_has_everything()
    {
        _hub.HasIcon = _hub.HasName = true;
        HubTitleDetails sync = Create();
        sync.Offer(Game());
        await sync.FlushAsync();

        Assert.Single(_hub.Requests);
        Assert.Equal(HttpMethod.Get, _hub.Requests[0].Method);
    }

    [Fact]
    public async Task Server_logging_off_queues_and_sends_nothing()
    {
        _serverLogging = false;
        HubTitleDetails sync = Create(() => new[] { Game() });
        sync.Offer(Game());
        await sync.FlushAsync();
        Assert.Empty(_hub.Requests);

        // Turned on later, the backfill of a fresh process still finds the game.
        _serverLogging = true;
        await Create(() => new[] { Game() }).FlushAsync();
        Assert.Equal(2, _hub.Requests.Count);
    }

    [Fact]
    public async Task Unresolved_names_and_non_images_are_left_out()
    {
        HubTitleDetails sync = Create();
        sync.Offer(Game(name: "@AppResLib.dll,-100", icon: Icon(Encoding.ASCII.GetBytes("GIF89a not a png"))));
        await sync.FlushAsync();

        // Nothing usable to give: asked, and nothing posted.
        Assert.Single(_hub.Requests);
    }

    [Fact]
    public async Task A_failed_flush_keeps_the_offer_for_the_next_one()
    {
        _hub.Fail = HttpStatusCode.TooManyRequests;
        HubTitleDetails sync = Create();
        sync.Offer(Game());
        await sync.FlushAsync();
        Assert.Single(_hub.Requests);

        _hub.Fail = null;
        await sync.FlushAsync();
        Assert.Equal(3, _hub.Requests.Count);
        Assert.True(_hub.HasIcon);
    }

    [Fact]
    public async Task A_refused_offer_is_not_retried()
    {
        _hub.Fail = HttpStatusCode.UnprocessableEntity;
        HubTitleDetails sync = Create();
        sync.Offer(Game());
        await sync.FlushAsync();
        _hub.Fail = null;
        await sync.FlushAsync();

        Assert.Single(_hub.Requests);
    }

    [Fact]
    public async Task Older_installs_are_backfilled_once_per_process()
    {
        int listed = 0;
        HubTitleDetails sync = Create(() => { listed++; return new[] { Game() }; });

        await sync.FlushAsync();
        await sync.FlushAsync();

        Assert.Equal(1, listed);
        Assert.Equal(2, _hub.Requests.Count);
        Assert.True(_hub.HasIcon);
    }

    [Theory]
    [InlineData("7.1", "Windows Phone 7")]
    [InlineData("8.0", "Windows Phone 8")]
    [InlineData("8.1", "Windows Phone 8.1")]
    [InlineData("9.0", null)]
    public void The_platform_comes_from_the_manifest(string version, string? expected)
    {
        string folder = System.IO.Path.Combine(_root, "install-" + version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(System.IO.Path.Combine(folder, "WMAppManifest.xml"),
            $"<Deployment AppPlatformVersion=\"{version}\"><App ProductID=\"{Id}\" /></Deployment>");

        Assert.Equal(expected, TitleDetails.ReadPlatform(folder));
        Assert.Null(TitleDetails.ReadPlatform(System.IO.Path.Combine(_root, "missing")));
    }
}
