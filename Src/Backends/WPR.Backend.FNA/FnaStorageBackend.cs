using WPR.Engine.Audio;
using System.IO;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// FNA implementation of <see cref="IStorageBackend"/> — Stage 5c-5 (Plans/STAGE5C-SCOPE.md).
    /// Forwards to FNA's <c>FNAPlatform</c> table, which resolves the SDL per-user preferences path.
    /// </summary>
    public sealed class FnaStorageBackend : IStorageBackend
    {
        public string GetStorageRoot() => Microsoft.Xna.Framework.FNAPlatform.GetStorageRoot();

        public DriveInfo GetDriveInfo(string storageRoot) =>
            Microsoft.Xna.Framework.FNAPlatform.GetDriveInfo(storageRoot);
    }
}
