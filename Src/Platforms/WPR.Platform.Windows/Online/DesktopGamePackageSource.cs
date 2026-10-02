using System.IO;
using WPR.Engine.Online;

namespace WPR.Platform.Windows.Online
{
    /// <summary>
    /// On the desktop a game is installed from a file in the game library folder, and that file
    /// is normally still there: the installer records its path, and this hands it back.
    /// </summary>
    /// <remarks>
    /// Nothing is copied, so there is nothing to release. If the file has been moved, deleted, or
    /// replaced by a different build, the upload is skipped: the reporter checks the hash before
    /// sending.
    /// </remarks>
    internal sealed class DesktopGamePackageSource : IGamePackageSource
    {
        public string? Acquire(GamePackageRecord package) =>
            !string.IsNullOrEmpty(package.Source) && File.Exists(package.Source) ? package.Source : null;

        public void Release(GamePackageRecord package, string path)
        {
        }
    }
}
