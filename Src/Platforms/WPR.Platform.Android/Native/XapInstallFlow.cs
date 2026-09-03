using WPR.Shell;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Database;
using Android.Provider;

using WPR.Common;

// Android.Database exports an Observable type of its own, so name Rxs explicitly.
using RxObservable = System.Reactive.Linq.Observable;
using WprApplication = WPR.Models.Application;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Manual XAP install: the only way a game enters the library on Android.
    ///
    /// <para>The desktop head also watches a configured library folder
    /// (<c>WPR.LibraryScanner</c>) and offers everything it finds. That is deliberately not
    /// wired up here. Scoped storage means an app cannot walk shared storage without
    /// broad, user-hostile permissions, and the folder-per-device guessing that would
    /// follow is worse UX than simply asking. So the user picks a .xap through the system
    /// document picker and that file — and only that file — gets installed.</para>
    /// </summary>
    internal static class XapInstallFlow
    {
        /// <summary>Request code the host must route back into <see cref="OnPickResultAsync"/>.</summary>
        public const int RequestPickXap = 4301;

        /// <summary>
        /// Open the system document picker. <c>*/*</c> rather than a MIME type because no
        /// Android provider maps the <c>.xap</c> extension to one — a narrower filter greys
        /// out every file the user is trying to choose.
        /// </summary>
        public static void StartPicker(Activity host)
        {
            Intent intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            host.StartActivityForResult(intent, RequestPickXap);
        }

        /// <summary>
        /// Copy the picked document into cache, read its manifest, and run the installer.
        /// Returns true when a game was actually added, so the caller knows whether to
        /// refresh its list.
        /// </summary>
        public static async Task<bool> OnPickResultAsync(Activity host, Result resultCode, Intent? data)
        {
            if (resultCode != Result.Ok || data?.Data == null) return false;

            global::Android.Net.Uri uri = data.Data;
            string displayName = ResolveDisplayName(host, uri) ?? "package.xap";

            string stagingDir = Path.Combine(host.CacheDir!.AbsolutePath, "xap-import");
            Directory.CreateDirectory(stagingDir);
            string stagedPath = Path.Combine(stagingDir, SanitizeFileName(displayName));

            WpProgressDialog progress = WpProgressDialog.Show(
                host, StripExtension(displayName), "copying to local storage…", indeterminate: true);

            try
            {
                // The installer opens the package as a ZipArchive, which needs a seekable
                // stream; a content:// stream is forward-only. Staging a real file also lets
                // the manifest read and the install read the same bytes twice without
                // re-prompting the provider.
                Log.Info(LogCategory.AppInstall, $"XAP install: staging {displayName}");
                bool copied = await Task.Run(() => CopyToStaging(host, uri, stagedPath));
                if (!copied)
                {
                    progress.Dismiss();
                    WpDialogs.Error(host, WPR.Shell.Resources.InstallationFailed,
                        "Could not read the selected file. Pick it again from a location the app can open.");
                    return false;
                }

                progress.SetStage("reading manifest…");
                Log.Info(LogCategory.AppInstall, $"XAP install: reading manifest of {displayName}");

                // Off the UI thread, like the copy above. ReadPreview is not the cheap XML read
                // its name suggests: it opens the whole .xap as a ZipArchive and then hands that
                // archive to ApplicationVersionResolver, which INFLATES the game's main assembly
                // out of it to read a real AssemblyVersion (half the library ships the 1.0.0.0
                // manifest default). On a large package that is tens of megabytes of decompression
                // plus an assembly parse, and doing it inline froze the launcher hard enough for
                // Android to raise "WPR isn't responding" — reproduced on Pixel_Dev installing a
                // 136 MB package as the third install of one session.
                ApplicationPreview? preview = await Task.Run(() =>
                {
                    using FileStream previewStream = new FileStream(
                        stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return ApplicationInstaller.ReadPreview(previewStream);
                });

                if (preview == null)
                {
                    progress.Dismiss();
                    WpDialogs.Error(host, WPR.Shell.Resources.InstallationFailed,
                        LocaleUtils.GetDisplayName(ApplicationInstallError.InvalidManifestFiles));
                    return false;
                }

                progress.SetStage("installing…");
                progress.SetProgress(0);
                Log.Info(LogCategory.AppInstall,
                    $"XAP install: extracting and patching '{preview.Name}' ({preview.ProductId})");

                ApplicationInstallError error;
                using (FileStream installStream = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    error = await ApplicationInstaller.Install(
                        installStream,
                        percent => progress.SetProgress(percent),
                        existing => RxObservable.FromAsync(() => ConfirmReplaceAsync(host, existing)),
                        CancellationToken.None);
                }

                progress.Dismiss();

                if (error == ApplicationInstallError.Canceled) return false;

                if (error != ApplicationInstallError.None)
                {
                    WpDialogs.Error(host, WPR.Shell.Resources.InstallationFailed,
                        LocaleUtils.GetDisplayName(error));
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                progress.Dismiss();
                Log.Error(LogCategory.AppInstall, $"XAP install failed for {displayName}:\n{ex}");
                WpDialogs.Error(host, WPR.Shell.Resources.InstallationFailed, ex.Message);
                return false;
            }
            finally
            {
                // The staged copy can be hundreds of megabytes; the installed game lives in
                // the data store now, so there is nothing to keep.
                try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
                catch (Exception) { /* cache; Android will reclaim it */ }
            }
        }

        private static Task<bool> ConfirmReplaceAsync(Activity host, WprApplication existing) =>
            WpDialogs.ConfirmAsync(
                host,
                WPR.Shell.Resources.ApplicationAlreadyInstalled,
                string.Format(WPR.Shell.Resources.ApplicationAlreadyInstalledDescription, existing.Name));

        private static bool CopyToStaging(Context context, global::Android.Net.Uri uri, string destination)
        {
            using Stream? source = context.ContentResolver?.OpenInputStream(uri);
            if (source == null) return false;

            using FileStream target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target, 1 << 20);
            return true;
        }

        private static string? ResolveDisplayName(Context context, global::Android.Net.Uri uri)
        {
            try
            {
                using ICursor? cursor = context.ContentResolver?.Query(
                    uri, new[] { OpenableColumns.DisplayName }, null, null, null);

                if (cursor != null && cursor.MoveToFirst())
                {
                    int column = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                    if (column >= 0) return cursor.GetString(column);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"Could not resolve a display name for {uri}: {ex.Message}");
            }

            return uri.LastPathSegment;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "package.xap" : name;
        }

        private static string StripExtension(string name)
        {
            try { return Path.GetFileNameWithoutExtension(name); }
            catch (ArgumentException) { return name; }
        }
    }
}
