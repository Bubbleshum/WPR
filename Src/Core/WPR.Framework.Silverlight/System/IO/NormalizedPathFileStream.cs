using System.IO;
using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// <see cref="FileStream"/> that runs its path through <see cref="ContentPaths"/> before
    /// opening it. Logic-free by design - see <see cref="File2"/>.
    ///
    /// <para>A SUBCLASS rather than a static shim because <c>ApplicationPatcher.MemberPatches</c>
    /// installs this by retargeting a <c>newobj</c>'s declaring type, so the replacement must be
    /// substitutable for the original wherever the constructed object lands - same reasoning as
    /// <see cref="SharedIsolatedStorageFileStream"/>. <see cref="FileStream"/> is not sealed, so
    /// this works; <c>FileInfo</c> and <c>DirectoryInfo</c> are, which is why they are not
    /// covered.</para>
    /// </summary>
    public class NormalizedPathFileStream : FileStream
    {
        public NormalizedPathFileStream(string path, FileMode mode)
            : base(For(path, mode), mode)
        {
        }

        public NormalizedPathFileStream(string path, FileMode mode, FileAccess access)
            : base(For(path, mode), mode, access)
        {
        }

        public NormalizedPathFileStream(string path, FileMode mode, FileAccess access, FileShare share)
            : base(For(path, mode), mode, access, share)
        {
        }

        /// <summary>
        /// Unlike the other stream shims this one has to choose, because the mode says whether
        /// this is a read or a create. Only <see cref="FileMode.Open"/> requires the file to exist
        /// already, so only it may follow <see cref="ContentPaths.Resolve"/> into the install
        /// folder; every other mode can create, and resolving one of those could land a brand-new
        /// file on an existing one somewhere else entirely.
        /// </summary>
        private static string For(string path, FileMode mode) =>
            (mode == FileMode.Open
                ? ContentPaths.Resolve(path)
                : ContentPaths.Normalize(path))!;
    }
}
