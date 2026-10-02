namespace WPR.Engine.Online
{
    /// <summary>
    /// How the head gets hold of a game's original package (XAP/APPX) again after install, so a
    /// build the hub has never seen can be uploaded when it asks for one.
    /// </summary>
    /// <remarks>
    /// <para>A head concern because the two platforms keep the package in different places. The
    /// desktop installs from a file that is usually still in the game library folder. Android
    /// installs from a document-picker URI whose read grant has to be persisted, and whose bytes
    /// have to be copied back to a real file before they can be streamed.</para>
    ///
    /// <para>The caller checks the file against the SHA-256 recorded at install, so a source may
    /// safely return a path that has since been replaced by a different build: it will not be
    /// sent.</para>
    /// </remarks>
    public interface IGamePackageSource
    {
        /// <summary>
        /// A readable local path to the package, or null when it is no longer reachable. Must not
        /// throw. May be slow (Android copies the package out).
        /// </summary>
        string? Acquire(GamePackageRecord package);

        /// <summary>Called once the upload has finished with a path <see cref="Acquire"/> returned.</summary>
        void Release(GamePackageRecord package, string path);
    }
}
