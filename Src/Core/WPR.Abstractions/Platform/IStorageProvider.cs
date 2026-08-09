using System.Collections.Generic;
using System.IO;

namespace WPR.Abstractions.Platform;

/// <summary>
/// Backing store for WP7 Isolated Storage. A <see cref="WPR.Platform"/> provider maps
/// it to the native filesystem; the <c>IsolatedStorage</c> shims consume it so they
/// never touch platform paths directly. Paths are provider-relative, '/'-separated.
/// </summary>
public interface IStorageProvider
{
    bool FileExists(string path);
    Stream OpenRead(string path);
    Stream OpenWrite(string path);
    void Delete(string path);
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern);
}
