using System;
using System.IO;
using System.IO.IsolatedStorage;
using WPR.Common;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

[CollectionDefinition(nameof(WindowsPhoneTargetTests), DisableParallelization = true)]
public class WindowsPhoneTargetCollection { }

/// <summary>
/// The per-launch target OS version (WMAppManifest AppPlatformVersion) and the phone's by-target
/// answers that read it.
/// </summary>
[Collection(nameof(WindowsPhoneTargetTests))]
public class WindowsPhoneTargetTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-target-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsPhoneTarget? _saved = WprHostEnvironment.CurrentTarget;

    public void Dispose()
    {
        WprHostEnvironment.CurrentTarget = _saved;
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    [Theory]
    [InlineData("7.0", 7, 0)]
    [InlineData("7.1", 7, 1)]
    [InlineData("8.0", 8, 0)]
    [InlineData("8.1", 8, 1)]
    [InlineData(" 7.1 ", 7, 1)]
    [InlineData("7.1.0.0", 7, 1)]
    [InlineData("8", 8, 0)]
    public void Parses_windows_phone_versions(string text, int major, int minor)
    {
        Assert.True(WindowsPhoneTarget.TryParse(text, out WindowsPhoneTarget t));
        Assert.Equal(new WindowsPhoneTarget(major, minor), t);
        Assert.Equal(major >= 8, t.IsWp8OrLater);
        Assert.Equal(major < 8, t.IsWp7);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("9.0")]
    [InlineData("10.0")]
    [InlineData("6.5")]
    [InlineData("7.x")]
    [InlineData("-7.1")]
    public void Refuses_anything_else(string? text)
    {
        Assert.False(WindowsPhoneTarget.TryParse(text, out _));
    }

    [Fact]
    public void Falls_back_to_71_managed_and_80_native()
    {
        Assert.Equal(WindowsPhoneTarget.Wp71, WindowsPhoneTarget.Fallback(native: false));
        Assert.Equal(WindowsPhoneTarget.Wp80, WindowsPhoneTarget.Fallback(native: true));
    }

    [Fact]
    public void Reads_the_install_manifest_and_says_when_it_fell_back()
    {
        string folder = Install("8.0");
        Assert.Equal(WindowsPhoneTarget.Wp80, WindowsPhoneTarget.ReadFromInstall(folder, native: false, out bool declared));
        Assert.True(declared);

        string noAttr = Install(null);
        Assert.Equal(WindowsPhoneTarget.Wp71, WindowsPhoneTarget.ReadFromInstall(noAttr, native: false, out declared));
        Assert.False(declared);
        Assert.Equal(WindowsPhoneTarget.Wp80, WindowsPhoneTarget.ReadFromInstall(noAttr, native: true, out _));

        string missing = System.IO.Path.Combine(_root, "missing");
        Assert.Equal(WindowsPhoneTarget.Wp71, WindowsPhoneTarget.ReadFromInstall(missing, native: false, out declared));
        Assert.False(declared);
        Assert.Equal(WindowsPhoneTarget.Wp71, WindowsPhoneTarget.ReadFromInstall(null, native: false, out _));

        string bad = Install("12.5");
        Assert.Equal(WindowsPhoneTarget.Wp80, WindowsPhoneTarget.ReadFromInstall(bad, native: true, out declared));
        Assert.False(declared);
    }

    [Fact]
    public void Reads_single_quotes_and_attribute_order()
    {
        Assert.True(WindowsPhoneTarget.TryReadManifestText(
            "<?xml version='1.0'?><Deployment xmlns='http://x' AppPlatformVersion='7.0'><App/></Deployment>", out var t));
        Assert.Equal(WindowsPhoneTarget.Wp70, t);
    }

    [Fact]
    public void Orders_and_prints()
    {
        Assert.True(WindowsPhoneTarget.Wp70 < WindowsPhoneTarget.Wp71);
        Assert.True(WindowsPhoneTarget.Wp71 < WindowsPhoneTarget.Wp80);
        Assert.True(WindowsPhoneTarget.Wp81 >= WindowsPhoneTarget.Wp80);
        Assert.Equal("7.1", WindowsPhoneTarget.Wp71.ToString());
        Assert.Equal("Windows Phone 7", WindowsPhoneTarget.Wp70.HubPlatformName);
        Assert.Equal("Windows Phone 8.1", WindowsPhoneTarget.Wp81.HubPlatformName);
    }

    [Fact]
    public void No_game_means_71()
    {
        WprHostEnvironment.CurrentTarget = null;
        Assert.Equal(WindowsPhoneTarget.Wp71, WprHostEnvironment.Target);
    }

    [Fact]
    public void Host_content_scale_factor_is_100_for_7x_and_the_device_scale_for_8x()
    {
        int savedScale = SilverlightHostContent.Wp8ScaleFactor;
        try
        {
            SilverlightHostContent.Wp8ScaleFactor = 160;
            WprHostEnvironment.CurrentTarget = WindowsPhoneTarget.Wp71;
            Assert.Equal(100, SilverlightHostContent.PlatformScaleFactor);
            WprHostEnvironment.CurrentTarget = WindowsPhoneTarget.Wp70;
            Assert.Equal(100, SilverlightHostContent.PlatformScaleFactor);
            WprHostEnvironment.CurrentTarget = WindowsPhoneTarget.Wp80;
            Assert.Equal(160, SilverlightHostContent.PlatformScaleFactor);
            WprHostEnvironment.CurrentTarget = WindowsPhoneTarget.Wp81;
            Assert.Equal(160, SilverlightHostContent.PlatformScaleFactor);
            WprHostEnvironment.CurrentTarget = null;
            Assert.Equal(100, SilverlightHostContent.PlatformScaleFactor);
        }
        finally
        {
            SilverlightHostContent.Wp8ScaleFactor = savedScale;
        }
    }

    [Fact]
    public void FileExists_of_null_is_false_for_7x_and_throws_for_8x()
    {
        using IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForAssembly();

        WprHostEnvironment.CurrentTarget = WindowsPhoneTarget.Wp71;
        Assert.False(SharedIsolatedStorage.FileExists(store, null!));

        WprHostEnvironment.CurrentTarget = WindowsPhoneTarget.Wp80;
        Assert.Throws<ArgumentNullException>(() => SharedIsolatedStorage.FileExists(store, null!));

        // A real path is unaffected either way.
        WprHostEnvironment.CurrentTarget = WindowsPhoneTarget.Wp71;
        Assert.False(SharedIsolatedStorage.FileExists(store, "wpr-target-" + Guid.NewGuid().ToString("N") + ".txt"));
    }

    private string Install(string? version)
    {
        string folder = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string attr = version == null ? "" : $" AppPlatformVersion=\"{version}\"";
        File.WriteAllText(System.IO.Path.Combine(folder, "WMAppManifest.xml"), $"<Deployment{attr}><App ProductID=\"{{1}}\" /></Deployment>");
        return folder;
    }
}
