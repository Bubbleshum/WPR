using System;
using System.IO;

using Android.Content;

using WPR.Engine.Online;

namespace WPR.Platform.Android.Online
{
    /// <summary>
    /// Gets an installed game's package back from the document it was picked from.
    /// </summary>
    /// <remarks>
    /// <para>Android installs through <c>ACTION_OPEN_DOCUMENT</c>, and the package is staged into
    /// the cache and deleted once installed (<c>XapInstallFlow</c>), so there is no file left to
    /// point at. What survives is the document's URI, and the read grant on it -
    /// <c>XapInstallFlow</c> persists that grant with <c>TakePersistableUriPermission</c> and the
    /// installer records the URI as <see cref="GamePackageRecord.Source"/>.</para>
    ///
    /// <para>The upload needs a real file (it seeks, chunk by chunk, and resumes), so
    /// <see cref="Acquire"/> copies the document into the cache and <see cref="Release"/> deletes
    /// it again. The copy costs a package's worth of space for as long as the upload runs, which
    /// is why it is made on demand rather than kept.</para>
    ///
    /// <para>A grant the user revoked, a document that was moved or deleted, or a provider that is
    /// offline all read as "not reachable": null, and the reporter tries again later.</para>
    /// </remarks>
    internal sealed class AndroidGamePackageSource : IGamePackageSource
    {
        private readonly Context _context;

        public AndroidGamePackageSource(Context context)
        {
            // The application context: this outlives any activity, and in the :game process it
            // must not pin GameActivity.
            _context = context.ApplicationContext ?? context;
        }

        public string? Acquire(GamePackageRecord package)
        {
            if (string.IsNullOrEmpty(package.Source)) return null;

            try
            {
                global::Android.Net.Uri uri = global::Android.Net.Uri.Parse(package.Source)!;
                using Stream? source = _context.ContentResolver?.OpenInputStream(uri);
                if (source == null) return null;

                string directory = Path.Combine(_context.CacheDir!.AbsolutePath, "hub-upload");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, package.Sha256 + ".xap");

                using (FileStream target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(target, 1 << 20);
                }
                return path;
            }
            catch (Exception ex)
            {
                WPR.Common.Log.Info(WPR.Common.LogCategory.AppList,
                    $"[wpr-online] game package not reachable ({package.PackageFileName}): {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public void Release(GamePackageRecord package, string path)
        {
            try { File.Delete(path); }
            catch (Exception) { /* cache; Android reclaims it */ }
        }
    }
}
