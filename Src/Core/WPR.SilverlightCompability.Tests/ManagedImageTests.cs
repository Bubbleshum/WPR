using System;
using System.IO;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// <see cref="ManagedImage"/> tells managed assemblies from native DLLs by their headers, so the
/// patcher never hands a native WP8 component to Cecil (which throws, and the throw reached WPR
/// Hub as a bad-assembly report).
/// </summary>
public sealed class ManagedImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wpr-managedimage-" + Guid.NewGuid().ToString("N"));

    public ManagedImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_managed_assembly_is_managed()
    {
        Assert.True(ManagedImage.IsManaged(typeof(ManagedImageTests).Assembly.Location));
    }

    [Fact]
    public void A_native_dll_is_not()
    {
        string native = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (!File.Exists(native))
        {
            return; // not a Windows host
        }

        Assert.False(ManagedImage.IsManaged(native));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { (byte)'M', (byte)'Z' })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void Something_that_is_not_an_image_is_not(byte[] bytes)
    {
        string path = Path.Combine(_dir, "junk.dll");
        File.WriteAllBytes(path, bytes);
        Assert.False(ManagedImage.IsManaged(path));
    }

    [Fact]
    public void A_missing_file_is_not()
    {
        Assert.False(ManagedImage.IsManaged(Path.Combine(_dir, "missing.dll")));
    }
}
