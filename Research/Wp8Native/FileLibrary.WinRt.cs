namespace WPR.Wp8Native
{
    /// <summary>
    /// The two roots, and the path rules, exposed to the WinRT storage classes
    /// (<see cref="WinRtRuntime"/>'s <c>StorageFolder</c>/<c>StorageFile</c>), so a WinRT path and a
    /// CRT path to the same file always land on the same host file.
    /// </summary>
    public sealed partial class FileLibrary
    {
        /// <summary>The unpacked package: read-only while the game runs.</summary>
        public string PackageRoot => _readRoot;

        /// <summary>The app's writable local folder on the host.</summary>
        public string LocalRoot => _writeRoot;

        /// <summary>
        /// Maps a guest path to the host file it means, by the same rules <c>CreateFile2</c> and
        /// <c>fopen</c> use: the local folder, then the package (case-insensitively), and anything
        /// else is something the image creates, in the sandbox.
        /// </summary>
        public string ResolveGuestPath(string emulatedPath, out bool writable) => Resolve(emulatedPath, out writable);
    }
}
