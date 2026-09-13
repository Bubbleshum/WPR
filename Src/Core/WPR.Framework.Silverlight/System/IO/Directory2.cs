using System.IO;
using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.IO.Directory</c>. Logic-free by design - see <see cref="File2"/>.
    ///
    /// <para>Directory operations use <see cref="ContentPaths.Normalize"/> throughout rather than
    /// <c>Resolve</c>: the install-folder probe tests for an existing FILE, so it has nothing
    /// useful to say about a directory.</para>
    /// </summary>
    public static class Directory2
    {
        public static bool Exists(string? path) =>
            Directory.Exists(ContentPaths.Normalize(path));

        public static DirectoryInfo CreateDirectory(string path) =>
            Directory.CreateDirectory(ContentPaths.Normalize(path)!);

        public static void Delete(string path) =>
            Directory.Delete(ContentPaths.Normalize(path)!);

        public static void Delete(string path, bool recursive) =>
            Directory.Delete(ContentPaths.Normalize(path)!, recursive);

        public static string[] GetFiles(string path) =>
            Directory.GetFiles(ContentPaths.Normalize(path)!);

        public static string[] GetFiles(string path, string searchPattern) =>
            Directory.GetFiles(ContentPaths.Normalize(path)!, searchPattern);

        public static string[] GetDirectories(string path) =>
            Directory.GetDirectories(ContentPaths.Normalize(path)!);

        public static string[] GetDirectories(string path, string searchPattern) =>
            Directory.GetDirectories(ContentPaths.Normalize(path)!, searchPattern);
    }
}
