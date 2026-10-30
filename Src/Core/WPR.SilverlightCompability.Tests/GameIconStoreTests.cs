using System;
using System.IO;
using WPR.Common;
using WPR.Models;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Icon paths from manifests are Windows paths (<c>Assets\ApplicationIcon.png</c>); on Android a
/// <c>\</c> is part of a file name, so they must be stored and read with <c>/</c>.
/// </summary>
[Collection("Configuration")]
public sealed class GameIconStoreTests : IDisposable
{
    private const string Product = "ed04c1b1-f34e-40e0-b997-67cbac8d0ad5";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wpr-gameicons-" + Guid.NewGuid().ToString("N"));
    private readonly Configuration? _previous = Configuration.Current;

    public GameIconStoreTests()
    {
        Directory.CreateDirectory(_root);
        Configuration.Current = new Configuration(_root);
    }

    public void Dispose()
    {
        Configuration.Current = _previous;
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData(@"AppData\" + Product, @"Assets\ApplicationIcon.png", "AppData/" + Product + "/Assets/ApplicationIcon.png")]
    [InlineData("AppData/" + Product, "ApplicationIcon.png", "AppData/" + Product + "/ApplicationIcon.png")]
    [InlineData("AppData/" + Product, @"\Assets\Logo.png", "AppData/" + Product + "/Assets/Logo.png")]
    [InlineData("AppData/" + Product, @" assets\icons\ApplicationIcon.png ", "AppData/" + Product + "/assets/icons/ApplicationIcon.png")]
    public void A_manifest_icon_path_is_stored_with_forward_slashes(string folder, string manifest, string expected)
    {
        Assert.Equal(expected, GameIconStore.InstalledIconRelative(folder, manifest));
    }

    private string PlaceIcon()
    {
        string file = Path.Combine(_root, "AppData", Product, "Assets", "ApplicationIcon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1 });
        return file;
    }

    [Fact]
    public void An_icon_path_written_with_backslashes_still_resolves()
    {
        PlaceIcon();
        string stored = @"AppData\" + Product + @"\Assets\ApplicationIcon.png";

        string? resolved = GameIconStore.Resolve(Product, stored);

        Assert.Equal("AppData/" + Product + "/Assets/ApplicationIcon.png", resolved);
        Assert.True(File.Exists(Configuration.Current!.DataPath(resolved!)));
    }

    [Fact]
    public void An_icon_path_written_with_backslashes_is_captured_into_the_store()
    {
        PlaceIcon();
        GameIconStore.Capture(Product, @"AppData\" + Product + @"\Assets\ApplicationIcon.png");

        Assert.Equal($"{GameIconStore.StoreRoot}/{Product}.png", GameIconStore.Find(Product));
    }
}

[CollectionDefinition("Configuration", DisableParallelization = true)]
public sealed class ConfigurationCollection { }
