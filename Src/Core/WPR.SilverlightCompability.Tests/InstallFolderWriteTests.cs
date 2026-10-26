using System;
using System.IO;
using WPR.Common;
using WPR.Engine.Content;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// A game's install folder was read-only on Windows Phone, and a relative path meant it. PAC-MAN
/// Kart Rally creates <c>save</c> with <c>new StreamWriter("save")</c> at start-up and relies on
/// that failing: when it succeeded, the next launch read the file through the game's own broken
/// reader and <c>Game.Initialize</c> threw.
/// </summary>
[CollectionDefinition(nameof(InstallFolderWriteTests), DisableParallelization = true)]
[Collection(nameof(InstallFolderWriteTests))]
public class InstallFolderWriteTests : IDisposable
{
    private readonly string _install = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-install-" + Guid.NewGuid().ToString("N"));
    private readonly string _elsewhere = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-store-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = WprHostEnvironment.CurrentInstallFolder;

    public InstallFolderWriteTests()
    {
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_elsewhere);
        WprHostEnvironment.CurrentInstallFolder = _install;
    }

    public void Dispose()
    {
        WprHostEnvironment.CurrentInstallFolder = _previous;
        try { Directory.Delete(_install, true); } catch (IOException) { }
        try { Directory.Delete(_elsewhere, true); } catch (IOException) { }
    }

    [Fact]
    public void A_relative_write_is_refused_while_a_game_runs()
    {
        Assert.Throws<UnauthorizedAccessException>(() => ContentPaths.ForWrite("save"));
        Assert.Throws<UnauthorizedAccessException>(() => new NormalizedPathStreamWriter("save"));
    }

    [Fact]
    public void An_absolute_write_inside_the_install_folder_is_refused()
    {
        string path = System.IO.Path.Combine(_install, "sub", "x.bin");
        Assert.Throws<UnauthorizedAccessException>(() => File2.WriteAllBytes(path, new byte[1]));
        Assert.Throws<UnauthorizedAccessException>(() => new NormalizedPathFileStream(path, FileMode.Create));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_write_outside_the_install_folder_goes_through()
    {
        string path = System.IO.Path.Combine(_elsewhere, "ok.txt");
        using (var w = new NormalizedPathStreamWriter(path)) w.Write("hi");
        Assert.Equal("hi", File.ReadAllText(path));

        // A sibling whose name starts with the install folder's is not inside it.
        Assert.Equal(_install + "-x", ContentPaths.ForWrite(_install + "-x"));
    }

    [Fact]
    public void Reading_the_install_folder_still_works()
    {
        File.WriteAllText(System.IO.Path.Combine(_install, "data.txt"), "content");
        using var r = new NormalizedPathFileStream(System.IO.Path.Combine(_install, "data.txt"), FileMode.Open, FileAccess.Read);
        Assert.Equal(7, r.Length);
    }

    [Fact]
    public void Nothing_is_refused_outside_a_game_run()
    {
        WprHostEnvironment.CurrentInstallFolder = null;
        Assert.Equal("save", ContentPaths.ForWrite("save"));
    }
}
