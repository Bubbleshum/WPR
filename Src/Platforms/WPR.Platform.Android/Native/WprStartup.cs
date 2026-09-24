using System;
using System.Collections.Generic;
using System.IO;

using Android.Content;

using WPR.Common;


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
        /// Copy the assemblies in <see cref="CopyAssemblyList"/> out of the APK's assets onto
        /// disk. Cecil reads the assembly being patched from a stream, but its
        /// <c>BaseAssemblyResolver</c> only finds real FILES in its search directories, and
        /// inside an APK the runtime's assemblies are not files. Also sets the process current
        /// directory to the staging folder, which is what puts it on Cecil's search path.
        ///
        /// <para><b>Reads an ASSET, not the APK's assembly packaging.</b> This used to open the
        /// APK and pull "assemblies/FNA.dll" out of it (Debug) or walk the assembly store with
        /// <c>AssemblyStoreExplorer</c> (Release). Both are hostage to how the .NET Android SDK
        /// happens to package managed code, and that changed under .NET 10: assemblies now live
        /// in <c>lib/&lt;abi&gt;/libassembly-store.so</c>, and <c>_AndroidUseAssemblyStore</c> is
        /// force-set true whenever <c>EmbedAssembliesIntoApk</c> is, so it cannot be turned off.
        /// The store reader found nothing, logged "entry not found", and returned — leaving Cecil
        /// unable to resolve FNA for every install and repatch performed ON THE DEVICE, which is
        /// the quiet kind of failure: the game still launches, it just binds the wrong types
        /// later. The csproj now ships FNA.dll as a plain asset, so nothing about future assembly
        /// packaging can break this again.</para>
        ///
        /// <para>Deliberately ONE code path for Debug and Release. The previous split meant the
        /// configuration most testing happens in exercised different code from the one that
        /// ships — see the interpreter-vs-JIT note in CLAUDE.md for what that costs.</para>
        /// </summary>
        public static void SetupDllPatchForCecil(Context context)
        {
            string basePath = PatchAssembliesDirectory(context);
            Directory.CreateDirectory(basePath);

            foreach (string dll in CopyAssemblyList)
            {
                // Must keep the ".dll" extension: Cecil's BaseAssemblyResolver looks for
                // "<name>.dll" in its search directories, so a file written as bare "FNA" is
                // invisible to it and every patch that needs FNA fails.
                string destination = Path.Combine(basePath, $"{dll}.dll");
                try
                {
                    // Copied unconditionally rather than only when absent. The staging folder is
                    // in external files and outlives the install, so a skip-if-present would
                    // leave the PREVIOUS APK's FNA.dll in place after an update and patch games
                    // against the wrong build. The file is ~1 MB and this runs once per process.
                    Filesystem.CopyFileFromAssets(context.Assets!, $"PatchAssemblies/{dll}.dll", destination);
                }
                catch (Exception ex)
                {
                    // Non-fatal by design: the launcher must still start. What is lost is the
                    // ability to patch, so say so in those terms rather than naming the file.
                    Log.Warn(LogCategory.Android,
                        $"Could not stage {dll}.dll for patching ({ex.GetType().Name}: {ex.Message}). " +
                        "Installing or repatching a game will fail on this device.");
                }
            }

            Directory.SetCurrentDirectory(basePath);
        }
    }
}
