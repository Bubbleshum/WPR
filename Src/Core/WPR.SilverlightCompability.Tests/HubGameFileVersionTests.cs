using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WPR.Engine.Online;
using WPR.Online.Hub;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Game file versions (WPR Hub a4b52a9): the package's manifest version goes with reports and
/// uploads, and the hub's "not_needed" stops an upload for good.
/// </summary>
public sealed class HubGameFileVersionTests : IDisposable
{
    private const string Id = "{d289b7d1-60d9-df11-a844-00237de2db9e}";

    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-gamefileversion-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---- reading the version the way the hub does ----

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using MemoryStream m = new();
        using (ZipArchive zip = new(m, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                using Stream s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return m.ToArray();
    }

    private static string WmApp(string version) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Deployment xmlns=\"http://schemas.microsoft.com/windowsphone/2009/deployment\" AppPlatformVersion=\"7.1\"><App ProductID=\"{Id}\" Title=\"Guitar Hero 5\" Version=\"{version}\" Author=\"Glu\" Publisher=\"Glu\"/></Deployment>";

    private static string? FromPackage(byte[] bytes) => PackageManifestVersion.FromPackage(new MemoryStream(bytes));

    [Theory]
    [InlineData("1.2.0.0", "1.2.0.0")]
    [InlineData(" 1.0 ", "1.0")]
    [InlineData("3", "3")]
    [InlineData("1.0.0.0.0", null)]
    [InlineData("1.0 beta", null)]
    [InlineData("", null)]
    public void The_WMAppManifest_version_is_read_with_the_hubs_shape(string attribute, string? expected) =>
        Assert.Equal(expected, FromPackage(Zip(("WMAppManifest.xml", WmApp(attribute)), ("Game.dll", "x"))));

    [Fact]
    public void An_appx_package_gives_its_identity_version()
    {
        const string appx = "<Package xmlns=\"http://schemas.microsoft.com/appx/2010/manifest\"><Identity Name=\"g\" Publisher=\"CN=x\" Version=\"2.1.4.0\"/></Package>";
        Assert.Equal("2.1.4.0", FromPackage(Zip(("AppxManifest.xml", appx))));
    }

    [Fact]
    public void A_manifest_only_in_a_subfolder_or_a_non_zip_gives_none()
    {
        Assert.Null(FromPackage(Zip(("Properties/WMAppManifest.xml", WmApp("1.0.0.0")))));
        Assert.Null(FromPackage(Encoding.ASCII.GetBytes("PRE\u0007 a DRM-wrapped download")));
    }

    [Fact]
    public void Measure_records_the_version_and_puts_the_stream_back()
    {
        MemoryStream package = new(Zip(("WMAppManifest.xml", WmApp("1.4.0.0"))));
        package.Position = 7;
        GamePackageRecord record = GamePackageRecord.Measure(package)!;
        Assert.Equal("1.4.0.0", record.Version);
        Assert.Equal(7, package.Position);
    }

    [Fact]
    public void An_older_record_reads_the_version_from_the_install_and_prefers_the_packages_own_manifest()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(System.IO.Path.Combine(_root, GamePackageRecord.FileName),
            JsonSerializer.Serialize(new { Sha256 = new string('a', 64), Size = 10 }));
        File.WriteAllText(System.IO.Path.Combine(_root, "WMAppManifest.xml"), WmApp("1.1.0.0"));
        Assert.Equal("1.1.0.0", GamePackageRecord.TryLoad(_root)!.Version);

        // WPR completed a broken manifest: the hub reads the package's, kept as .original.
        File.WriteAllText(System.IO.Path.Combine(_root, "WMAppManifest.xml.original"), WmApp("1.0.3.0"));
        Assert.Equal("1.0.3.0", GamePackageRecord.TryLoad(_root)!.Version);
    }

    // ---- the reporter against a hub ----

    /// <summary>The diagnostics and upload calls, answered as WPR Hub a4b52a9 does.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();
        public object GameFile { get; set; } = new { wanted = false };
        public Func<HttpResponseMessage> UploadStart { get; set; } = () => Respond(HttpStatusCode.Conflict,
            new { message = "The hub already has every package it needs for this game.", reason = "not_needed" });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            string path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, body));
            if (path == "/api/diagnostics")
            {
                return Respond(HttpStatusCode.Created, new
                {
                    code = "WPR-TEST0001", status_url = "http://hub.test/api/diagnostics/WPR-TEST0001",
                    log_upload_url = "http://hub.test/api/diagnostics/WPR-TEST0001/log", upload_token = "u", max_log_bytes = 1000,
                    game_file = GameFile,
                });
            }
            if (path.EndsWith("/uploads", StringComparison.Ordinal)) return UploadStart();
            return Respond(HttpStatusCode.NotFound, new { message = "no" });
        }

        public static HttpResponseMessage Respond(HttpStatusCode status, object o) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    private sealed class FilePackages(string path) : IGamePackageSource
    {
        public int Acquired;
        public string? Acquire(GamePackageRecord package) { Acquired++; return path; }
        public void Release(GamePackageRecord package, string path) { }
    }

    private (string InstallFolder, string Sha) Install(string version)
    {
        string install = System.IO.Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        string xap = System.IO.Path.Combine(_root, "game.xap");
        File.WriteAllBytes(xap, Zip(("WMAppManifest.xml", WmApp(version)), ("Game.dll", "x")));
        using (FileStream s = File.OpenRead(xap))
        {
            GamePackageRecord.Measure(s)!.Save(install);
        }
        return (install, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(xap))).ToLowerInvariant());
    }

    private HubCrashReporter Reporter(FakeHub hub, IGamePackageSource? packages = null) => new(new HubSettings
    {
        BaseUri = () => new Uri("http://hub.test/"),
        AccessToken = () => null,
        ServerLogging = () => true,
        DataDirectory = System.IO.Path.Combine(_root, "online"),
        WprVersion = "test",
        Platform = "windows",
        Handler = hub,
        Packages = packages,
    });

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task A_report_carries_the_packages_manifest_version()
    {
        (string install, _) = Install("1.2.0.0");
        FakeHub hub = new();
        HubCrashReporter reporter = Reporter(hub);
        reporter.Report(CrashReport.ForInstall(Id, "Guitar Hero 5", install, CrashReport.RuntimePaths.XnaFna));

        await reporter.FlushAsync();

        var post = Assert.Single(hub.Requests);
        Assert.Equal("1.2.0.0", Body(post.Body!).GetProperty("app_version").GetString());
    }

    [Fact]
    public async Task A_not_needed_receipt_queues_no_upload()
    {
        (string install, string sha) = Install("1.2.0.0");
        FakeHub hub = new() { GameFile = new { wanted = false, reason = "not_needed" } };
        FilePackages packages = new(System.IO.Path.Combine(_root, "game.xap"));
        HubCrashReporter reporter = Reporter(hub, packages);
        reporter.Report(CrashReport.ForInstall(Id, "Guitar Hero 5", install, CrashReport.RuntimePaths.XnaFna));

        await reporter.FlushAsync();
        await reporter.FlushAsync();

        Assert.Single(hub.Requests);
        Assert.Equal(0, packages.Acquired);
    }

    [Fact]
    public async Task A_not_needed_upload_start_sends_the_version_and_drops_the_upload_for_good()
    {
        (string install, string sha) = Install("1.2.0.0");
        FakeHub hub = new()
        {
            GameFile = new { wanted = true, sha256 = sha, start_url = $"http://hub.test/api/game-files/{sha}/uploads", token = "t", max_bytes = 1 << 30 },
        };
        FilePackages packages = new(System.IO.Path.Combine(_root, "game.xap"));
        HubCrashReporter reporter = Reporter(hub, packages);
        reporter.Report(CrashReport.ForInstall(Id, "Guitar Hero 5", install, CrashReport.RuntimePaths.XnaFna));

        await reporter.FlushAsync();

        Assert.Equal(2, hub.Requests.Count);
        JsonElement start = Body(hub.Requests[1].Body!);
        Assert.Equal("1.2.0.0", start.GetProperty("version").GetString());
        Assert.Equal("D289B7D1-60D9-DF11-A844-00237DE2DB9E", start.GetProperty("title_id").GetString());
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(_root, "online", "uploads")));

        // Nothing left to retry.
        await reporter.FlushAsync();
        Assert.Equal(2, hub.Requests.Count);
    }

    [Fact]
    public async Task A_receiving_start_is_kept_for_the_next_flush()
    {
        // The counter-case: a start the hub accepts but that does not finish stays queued.
        (string install, string sha) = Install("1.2.0.0");
        FakeHub hub = new()
        {
            GameFile = new { wanted = true, sha256 = sha, start_url = $"http://hub.test/api/game-files/{sha}/uploads", token = "t", max_bytes = 1 << 30 },
            UploadStart = () => FakeHub.Respond(HttpStatusCode.ServiceUnavailable, new { message = "busy" }),
        };
        HubCrashReporter reporter = Reporter(hub, new FilePackages(System.IO.Path.Combine(_root, "game.xap")));
        reporter.Report(CrashReport.ForInstall(Id, "Guitar Hero 5", install, CrashReport.RuntimePaths.XnaFna));

        await reporter.FlushAsync();

        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(_root, "online", "uploads")));
    }
}
