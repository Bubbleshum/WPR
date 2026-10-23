using System;
using System.IO;
using Microsoft.Xna.Framework;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// <see cref="TitleContainer.OpenStream"/> must throw <see cref="FileNotFoundException"/> for a
/// name whose folder is missing too, as WP7's title storage did. SPY Mouse asks for a file under a
/// folder its package does not have and catches only <c>FileNotFoundException</c>.
/// </summary>
[CollectionDefinition(nameof(TitleContainerTests), DisableParallelization = true)]
[Collection(nameof(TitleContainerTests))]
public class TitleContainerTests : IDisposable
{
    // System.IO.Path in full: the shim's Path (a Shape) shadows it inside this namespace.
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-title-" + Guid.NewGuid().ToString("N"));

    public TitleContainerTests()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_root, "res", "map10"));
        File.WriteAllText(System.IO.Path.Combine(_root, "res", "map10", "amb_schemes.xml.brd"), "x");
        XnaBackend.SetTitleLocation(() => _root);
    }

    public void Dispose()
    {
        XnaBackend.SetTitleLocation(null);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void A_missing_folder_is_a_FileNotFoundException()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => TitleContainer.OpenStream("res/sound/sound/amb/int_house/amb.xml.brd"));
        Assert.IsType<DirectoryNotFoundException>(ex.InnerException);
    }

    [Fact]
    public void A_missing_file_in_an_existing_folder_is_still_a_FileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => TitleContainer.OpenStream("res/map10/missing.brd"));
    }

    [Fact]
    public void Windows_separators_still_open_the_file()
    {
        using Stream s = TitleContainer.OpenStream(@"res\map10\amb_schemes.xml.brd");
        Assert.Equal(1, s.Length);
    }
}
