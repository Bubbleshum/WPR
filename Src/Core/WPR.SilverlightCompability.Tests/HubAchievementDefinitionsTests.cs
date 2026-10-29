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

/// <summary>Games' achievement definitions and icons offered to WPR Hub (HubAchievementDefinitions).</summary>
public sealed class HubAchievementDefinitionsTests : IDisposable
{
    private const string Id = "{bda6fdfb-6556-4a85-a077-573e2f5c177c}";
    private const string HubId = "BDA6FDFB-6556-4A85-A077-573E2F5C177C";

    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-achdefs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHub _hub = new();
    private bool _serverLogging = true;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Answers like the hub: rows it has, each with what it still wants; a POST creates rows.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();
        public Dictionary<string, (bool Details, bool Icon)> Rows { get; } = new();
        public HttpStatusCode? Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            if (Fail is { } status) return new HttpResponseMessage(status) { Content = Json(new { message = "no" }) };

            if (request.Method == HttpMethod.Post)
            {
                int created = 0, icons = 0;
                foreach (JsonElement a in JsonDocument.Parse(body!).RootElement.GetProperty("achievements").EnumerateArray())
                {
                    string key = a.GetProperty("key").GetString()!;
                    bool icon = a.TryGetProperty("icon", out _);
                    if (!Rows.ContainsKey(key)) created++;
                    if (icon) icons++;
                    Rows[key] = (false, !icon && (!Rows.TryGetValue(key, out var r) || r.Icon));
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new { title_id = HubId, created, filled = 0, icons }) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json(new
                {
                    title_id = HubId,
                    known = Rows.Count > 0,
                    achievements = Rows.Select(r => new { key = r.Key, wants_details = r.Value.Details, wants_icon = r.Value.Icon }),
                }),
            };
        }

        private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    }

    private HubAchievementDefinitions Create(Func<IEnumerable<TitleAchievements>>? installed = null) => new(new HubSettings
    {
        BaseUri = () => new Uri("http://hub.test/"),
        AccessToken = () => null,
        ServerLogging = () => _serverLogging,
        DataDirectory = _root,
        WprVersion = "test",
        Platform = "windows",
        InstalledAchievementCatalogues = installed,
        Handler = _hub,
    });

    private static readonly byte[] Png = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    private string Icon(byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private TitleAchievements Catalogue(string source = "package", params OfferedAchievement[] achievements) =>
        new(Id, source, achievements.Length > 0 ? achievements : new[]
        {
            new OfferedAchievement("ACHIEVEMENT_COLLECT_STARS", "Collect Stars", true, null, 0, false, Icon(Png)),
            new OfferedAchievement("ACHIEVEMENT_RADIO", "Radio", true, null, 0, false, null),
        });

    private JsonElement Posted(int request) => JsonDocument.Parse(_hub.Requests[request].Body!).RootElement.GetProperty("achievements");

    [Fact]
    public async Task A_catalogue_is_offered_once_and_only_what_the_hub_lacks_is_sent()
    {
        HubAchievementDefinitions sync = Create();
        sync.Offer(Catalogue());
        Assert.Empty(_hub.Requests); // queued only

        await sync.FlushAsync();

        Assert.Equal(2, _hub.Requests.Count);
        Assert.Equal($"/api/titles/{HubId}/achievement-details", _hub.Requests[0].Path);
        JsonElement items = Posted(1);
        Assert.Equal(2, items.GetArrayLength());
        // A name that is only the key made readable is not sent; the hub names the row after its key.
        Assert.False(items[0].TryGetProperty("name", out _));
        Assert.Equal(Convert.ToBase64String(Png), items[0].GetProperty("icon").GetString());
        Assert.False(items[1].TryGetProperty("icon", out _));

        // A second install, a repatch, a new process: nothing more, not even the question.
        sync.Offer(Catalogue());
        await sync.FlushAsync();
        await Create(() => new[] { Catalogue() }).FlushAsync();
        Assert.Equal(2, _hub.Requests.Count);
    }

    [Fact]
    public async Task Rows_the_hub_has_get_only_what_they_want()
    {
        _hub.Rows["A"] = (false, false); // curated, with an icon
        _hub.Rows["B"] = (false, true);  // curated, no icon
        _hub.Rows["C"] = (true, false);  // named after its key by an unlock
        HubAchievementDefinitions sync = Create();
        sync.Offer(Catalogue("bundled",
            new OfferedAchievement("A", "Name A", false, "Desc", 10, false, Icon(Png)),
            new OfferedAchievement("B", "Name B", false, "Desc", 10, false, Icon(Png)),
            new OfferedAchievement("C", "Name C", false, "Desc C", 20, true, null),
            new OfferedAchievement("D", "Name D", false, null, 5, false, null)));

        await sync.FlushAsync();

        JsonElement items = Posted(1);
        Assert.Equal(new[] { "B", "C", "D" }, items.EnumerateArray().Select(i => i.GetProperty("key").GetString()));
        // B: the icon only.
        Assert.False(items[0].TryGetProperty("name", out _));
        Assert.True(items[0].TryGetProperty("icon", out _));
        // C: the details.
        Assert.Equal("Name C", items[1].GetProperty("name").GetString());
        Assert.Equal(20, items[1].GetProperty("points").GetInt32());
        Assert.True(items[1].GetProperty("secret").GetBoolean());
    }

    [Fact]
    public async Task A_readable_key_never_replaces_a_name_the_hub_wants_filled()
    {
        _hub.Rows["K"] = (true, false);
        HubAchievementDefinitions sync = Create();
        sync.Offer(Catalogue("package", new OfferedAchievement("K", "K", true, null, 0, false, null)));
        await sync.FlushAsync();

        Assert.Single(_hub.Requests); // asked, nothing to give
    }

    [Fact]
    public async Task Server_logging_off_queues_and_sends_nothing()
    {
        _serverLogging = false;
        HubAchievementDefinitions sync = Create(() => new[] { Catalogue() });
        sync.Offer(Catalogue());
        await sync.FlushAsync();
        Assert.Empty(_hub.Requests);
        Assert.False(Directory.Exists(System.IO.Path.Combine(_root, "achievement-details")));
    }

    [Fact]
    public async Task Installed_catalogues_are_backfilled_once_per_process()
    {
        int listed = 0;
        HubAchievementDefinitions sync = Create(() => { listed++; return new[] { Catalogue() }; });
        await sync.FlushAsync();
        await sync.FlushAsync();

        Assert.Equal(1, listed);
        Assert.Equal(2, _hub.Requests.Count);
    }

    [Fact]
    public async Task A_rate_limited_flush_keeps_the_offer_and_a_refused_one_is_dropped()
    {
        _hub.Fail = HttpStatusCode.TooManyRequests;
        HubAchievementDefinitions sync = Create();
        sync.Offer(Catalogue());
        await sync.FlushAsync();
        _hub.Fail = null;
        await sync.FlushAsync();
        Assert.Equal(3, _hub.Requests.Count);

        _hub.Fail = HttpStatusCode.UnprocessableEntity;
        sync.Offer(Catalogue("bundled"));
        await sync.FlushAsync();
        _hub.Fail = null;
        await sync.FlushAsync();
        Assert.Equal(4, _hub.Requests.Count);
    }

    [Fact]
    public async Task A_shipped_catalogue_is_offered_again_after_a_generated_one()
    {
        HubAchievementDefinitions sync = Create();
        sync.Offer(Catalogue("package"));
        await sync.FlushAsync();
        sync.Offer(Catalogue("bundled"));
        await sync.FlushAsync();

        Assert.Equal(3, _hub.Requests.Count); // the second asks, and the hub has everything
    }

    [Fact]
    public async Task Large_icon_sets_go_in_several_requests()
    {
        byte[] big = new byte[200 * 1024];
        Png.CopyTo(big, 0);
        string icon = Icon(big);
        OfferedAchievement[] many = Enumerable.Range(0, 8)
            .Select(i => new OfferedAchievement("K" + i, "N" + i, false, null, 0, false, icon)).ToArray();
        HubAchievementDefinitions sync = Create();
        sync.Offer(Catalogue("bundled", many));
        await sync.FlushAsync();

        List<int> sizes = _hub.Requests.Skip(1).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("achievements").GetArrayLength()).ToList();
        Assert.True(sizes.Count > 1);
        Assert.Equal(8, sizes.Sum());
        Assert.All(_hub.Requests.Skip(1), r => Assert.True(r.Body!.Length < 1200 * 1024));
    }
}
