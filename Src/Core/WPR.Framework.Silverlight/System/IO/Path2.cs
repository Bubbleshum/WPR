using System.IO;
using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for the <c>System.IO.Path</c> members that have to understand a Windows-style path.
    /// Logic-free by design - see <see cref="File2"/>.
    ///
    /// <para>These parse a path rather than open one, so they use
    /// <see cref="ContentPaths.Normalize"/>: there is no file to look for, only separators to
    /// agree on. Without it <c>Path.GetFileName("Content\Foo.xml")</c> returns the whole string on
    /// Android, because nothing there considers <c>\</c> a separator.</para>
    ///
    /// <para>This predates the engine subsystem and carried its own copy of the translation, three
    /// times over; it now shares the one the platform's rules drive.</para>
    /// </summary>
    public static class Path2
    {
        public static string? GetDirectoryName(string? path) =>
            path == null ? null : Path.GetDirectoryName(ContentPaths.Normalize(path));

        public static string? GetFileName(string? path) =>
            path == null ? null : Path.GetFileName(ContentPaths.Normalize(path));

        public static string? GetFileNameWithoutExtension(string? path) =>
            path == null ? null : Path.GetFileNameWithoutExtension(ContentPaths.Normalize(path));
    }
}
