using System;
using System.Collections.Generic;
using System.IO;

using Android.Content;

using WPR.Common;

#if !DEBUG
using Xamarin.Android.AssemblyStore;
#else
using System.IO.Compression;
#endif

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Process-wide start-up that every launcher activity depends on: the
    /// <see cref="Configuration"/> singleton, the seeded SQLite databases, the bundled
    /// achievement catalogues, and the on-disk copy of FNA that Cecil needs in order to
    /// resolve references while patching a game.
    ///
    /// <para>This used to live as instance methods on <c>MainActivity</c>. The native shell
    /// has several entry activities (Start, games, achievements, settings) and Android is
    /// free to recreate the process directly into any of them after a low-memory kill, so
    /// the work has to be callable from all of them and has to be idempotent.</para>
    ///
    /// <para><b>Not shared with the game process.</b> <c>GameActivity</c> runs under
    /// <c>Process=":game"</c> and redoes its own subset of this in its
    /// <c>OnCreate</c>.</para>
    /// </summary>
    internal static class WprStartup
    {
        private static readonly object Gate = new object();
        private static bool _Initialized;

        /// <summary>Assemblies Cecil must be able to resolve from disk while patching.</summary>
        private static readonly List<string> CopyAssemblyList = new List<string> { "FNA" };

        /// <summary>
        /// Where <see cref="SetupDllPatchForCecil"/> stages those assemblies. Also the
        /// process's current directory while patching, because Cecil's
        /// <c>BaseAssemblyResolver</c> searches it.
        /// </summary>
        public static string PatchAssembliesDirectory(Context context) =>
            Path.Combine(context.GetExternalFilesDir(null)!.AbsolutePath, "PatchAssemblies");

        /// <summary>
        /// Idempotent. Safe to call from every activity's <c>OnCreate</c>; only the first
        /// call in a process does the work.
        /// </summary>
        public static void EnsureInitialized(Context context)
        {
            lock (Gate)
            {
                if (_Initialized) return;
                _Initialized = true;
            }

            SetupConfigurationAndDatabase(context);
            SetupDllPatchForCecil(context);
        }

        private static void SetupConfigurationAndDatabase(Context context)
        {
            Configuration.Current = new Configuration(context.GetExternalFilesDir(null)!.AbsolutePath);

            var databaseDir = Configuration.Current.DataPath("Database");
            Directory.CreateDirectory(databaseDir);

            var dbPath = Path.Combine(databaseDir, "applications.db");
            if (!File.Exists(dbPath))
            {
                Filesystem.CopyFileFromAssets(context.Assets!, "Database/applications.db", dbPath);
            }

            var achievementsPath = Path.Combine(databaseDir, "achievements.db");
            if (!File.Exists(achievementsPath))
            {
                Filesystem.CopyFileFromAssets(context.Assets!, "Database/achievements.db", achievementsPath);
            }

            // Hardcoded achievement catalogues (manifest + icon PNGs), one folder per
            // product. Recursive copy handles the per-product subfolders.
            Filesystem.CopyFolderFromAssets(context.Assets!, "Database/Achievements",
                Path.Combine(databaseDir, "Achievements"));

            // Reconcile installed games against their catalogues (non-destructive; never
            // resets unlock progress). Non-fatal.
            try { WPR.XnaAchievementSeeder.ReconcileCatalogueGamesAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Startup, $"Startup achievement reconcile failed (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Extract the assemblies in <see cref="CopyAssemblyList"/> out of the APK onto
        /// disk. Cecil reads assemblies from streams, but its resolver only looks at real
        /// files in its search directories, and inside an APK the runtime's assemblies are
        /// not files. Also sets the process current directory to the staging folder, which
        /// is what puts it on Cecil's search path.
        /// </summary>
        public static void SetupDllPatchForCecil(Context context)
        {
            string basePath = PatchAssembliesDirectory(context);
            Directory.CreateDirectory(basePath);

            string? apkPath = global::Android.App.Application.Context.ApplicationInfo?.PublicSourceDir;
            if (apkPath == null)
            {
                Log.Warn(LogCategory.Android, "Unable to copy DLLs needed for patching! Some games may fail to patch!");
                return;
            }

#if DEBUG
            using (ZipArchive archive = ZipFile.Open(apkPath, ZipArchiveMode.Read))
            {
                foreach (var dll in CopyAssemblyList)
                {
                    ZipArchiveEntry? entry = archive.GetEntry($"assemblies/{dll}.dll");
                    if (entry == null)
                    {
                        Log.Warn(LogCategory.Android, $"Fail to copy DLL ${dll} to patch assembly folder!");
                    }
                    else
                    {
                        // Must keep the ".dll" extension. Cecil's BaseAssemblyResolver looks
                        // for "<name>.dll" in its search directories (this folder is the CWD,
                        // set at the end of this method), so a file written as bare "FNA" is
                        // invisible to it and every patch that needs FNA fails.
                        entry.ExtractToFile(Path.Combine(basePath, $"{dll}.dll"), true);
                    }
                }
            }
#else
            AssemblyStoreExplorer explorer = new AssemblyStoreExplorer(apkPath, keepStoreInMemory: true);
            foreach (var dll in CopyAssemblyList)
            {
                string filename = $"{dll}.dll.comp";
                string filenameAuth = $"{dll}.dll";

                if (explorer.AssembliesByName.ContainsKey(dll))
                {
                    explorer.AssembliesByName[dll].ExtractImage(basePath, filename);
                }
                else
                {
                    Log.Warn(LogCategory.Android, $"Fail to copy DLL ${dll} to patch assembly folder (entry not found)!");
                    continue;
                }

                bool fileShouldMove = false;

                using (FileStream stream = new FileStream(Path.Combine(basePath, filename), FileMode.Open, FileAccess.Read))
                {
                    if (AssemblyDecompressor.IsCompressed(stream))
                    {
                        stream.Seek(0, SeekOrigin.Begin);

                        using (FileStream streamAuth = new FileStream(Path.Combine(basePath, filenameAuth), FileMode.OpenOrCreate, FileAccess.Write))
                        {
                            if (!AssemblyDecompressor.Work(stream, streamAuth))
                            {
                                Log.Warn(LogCategory.Android, $"Fail to decompress DLL ${dll} to patch assembly folder (entry not found)!");
                            }
                        }
                    }
                    else
                    {
                        fileShouldMove = true;
                    }
                }

                if (fileShouldMove)
                {
                    // overwrite: true is load-bearing. PatchAssemblies lives in the app's
                    // external files dir, so FNA.dll survives the process — every launch
                    // after the first found it already there and the 2-arg File.Move threw
                    // IOException("...FNA.dll already exists") straight out of OnCreate.
                    File.Move(Path.Combine(basePath, filename), Path.Combine(basePath, filenameAuth), true);
                }
                else
                {
                    File.Delete(Path.Combine(basePath, filename));
                }
            }
#endif

            Directory.SetCurrentDirectory(basePath);
        }
    }
}
