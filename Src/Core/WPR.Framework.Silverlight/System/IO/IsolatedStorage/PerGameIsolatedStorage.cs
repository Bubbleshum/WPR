using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Reflection;

using WPR.Common;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Gives each game its own isolated store, as WP7 did. Reached through
    /// <see cref="SharedIsolatedStorage.GetUserStoreForApplication"/>, which the patcher (v38)
    /// substitutes for every <c>IsolatedStorageFile.GetUserStoreForApplication()</c> call site.
    ///
    /// <para><b>Why.</b> The BCL keys a store on the host's ENTRY ASSEMBLY, so every game WPR ever
    /// hosted shared one store — and two titles that happen to use the same filename overwrite
    /// each other's data. Gravity Guy (<c>4f930d12-…</c>) is the reference case: it, Fragger,
    /// Monster Island and iStunt 2 all bundle Miniclip's <c>DLCFramework</c>, which persists to
    /// <c>$_StatesAfterExitData_$\DLCManager</c>. In the other three that copy is obfuscated, so
    /// the file is written with a root of <c>&lt;d&gt;</c>; Gravity Guy's is not, so its
    /// <c>XmlSerializer</c> throws on it inside <c>DLCManager.Initialize</c>, which aborts
    /// <c>Game.Initialize</c> before <c>LoadContent</c>, and the game draws nothing for ever.
    /// Playing Fragger once was enough to break Gravity Guy permanently, because the framework
    /// only deletes that file after reading it successfully.</para>
    ///
    /// <para><b>The store is a genuine <see cref="IsolatedStorageFile"/> with its root moved</b>,
    /// not a wrapper. Every BCL member resolves paths through the private <c>_rootDirectory</c>
    /// (<c>GetFullPath</c>, the enumerations, <c>Remove</c>, and <c>IsolatedStorageFileStream</c>
    /// via <c>isf.GetFullPath</c>), so swapping that one field isolates the whole surface —
    /// including the members <see cref="SharedIsolatedStorage"/> deliberately does not shim.
    /// Verified identical on .NET 8 (desktop) and .NET 10 (Android). If the field ever
    /// disappears the store is handed back unchanged and a warning is traced: that is the old
    /// shared behaviour, never an exception.</para>
    ///
    /// <para><b>Layout mirrors the BCL's own</b> — <c>&lt;DataStore&gt;\IsolatedStore\&lt;ProductId&gt;\AppFiles\</c>
    /// — and that is load-bearing. <see cref="IsolatedStorageFile.Remove()"/> deletes the root and
    /// then its PARENT when the parent holds nothing else; with the per-game folder directly under
    /// <c>IsolatedStore</c>, a game calling <c>Remove()</c> would take every other game's saves
    /// with it. Under the BCL layout the parent is the game's own folder. It is also why the
    /// migration manifest sits beside the game folders rather than inside one.</para>
    ///
    /// <para><b>Migration.</b> Existing saves are all in the old shared store and nothing records
    /// which file belongs to which game, so the first time the new layout is used a manifest is
    /// written listing every game installed at that moment. Each listed game, on its first store
    /// open, receives a copy of the whole old pool (never overwriting a file it already has) and
    /// is struck off. So each game starts exactly where it was — it sees the same files it saw
    /// yesterday — and from then on nothing another game writes can reach it. Games installed
    /// after the cut-over are not listed and start with an empty store, as a fresh WP7 install
    /// did. The old store is left untouched, so this is recoverable. Foreign files inherited in
    /// the copy are inert unless the game already collided with them, which it did before this
    /// change too.</para>
    ///
    /// <para>The old pool is whatever the RUNNING host's BCL store is, which on Windows depends
    /// on the exe's path (a harness has a different one from the app). The per-game stores do not:
    /// they follow <see cref="Configuration.DataStorePath"/>, so the app and any harness now see
    /// the same saves.</para>
    /// </summary>
    public static class PerGameIsolatedStorage
    {
        private const string StoresFolderName = "IsolatedStore";
        private const string ScopeFolderName = "AppFiles";
        private const string ManifestFileName = "shared-store-migration.txt";

        private static readonly object Gate = new object();
        private static FieldInfo? s_rootField;
        private static bool s_rootFieldResolved;

        /// <summary>
        /// The current game's store, or the shared BCL store when no game is identified (the
        /// launcher, a unit test) or the root could not be moved.
        /// </summary>
        [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(IsolatedStorageFile))]
        public static IsolatedStorageFile GetUserStoreForApplication()
        {
            IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication();

            string? productId = WprHostEnvironment.CurrentProductId;
            string? storesRoot = Configuration.Current?.DataPath(StoresFolderName);
            if (string.IsNullOrEmpty(productId) || storesRoot == null || !IsPlainFolderName(productId!))
            {
                return store;
            }

            FieldInfo? rootField = ResolveRootField();
            if (rootField == null || rootField.GetValue(store) is not string sharedRoot)
            {
                return store;
            }

            string gameRoot = GameRoot(storesRoot, productId!);
            try
            {
                lock (Gate)
                {
                    MigrateIfPending(storesRoot, productId!, sharedRoot, gameRoot);
                    Directory.CreateDirectory(gameRoot);
                }

                rootField.SetValue(store, gameRoot);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-isostore] could not open the per-game store for {productId} "
                    + $"({ex.GetType().Name}: {ex.Message}); falling back to the shared store.");
            }

            return store;
        }

        /// <summary>Where a game's store lives, with the trailing separator the BCL uses.</summary>
        public static string GameRoot(string storesRoot, string productId)
            => Path.Combine(storesRoot, productId, ScopeFolderName) + Path.DirectorySeparatorChar;

        private static FieldInfo? ResolveRootField()
        {
            if (!s_rootFieldResolved)
            {
                s_rootField = typeof(IsolatedStorageFile).GetField(
                    "_rootDirectory", BindingFlags.Instance | BindingFlags.NonPublic);
                if (s_rootField == null || s_rootField.FieldType != typeof(string))
                {
                    s_rootField = null;
                    Trace.WriteLine("[wpr-isostore] IsolatedStorageFile._rootDirectory not found on this "
                        + "runtime; every game shares one store.");
                }
                s_rootFieldResolved = true;
            }
            return s_rootField;
        }

        /// <summary>
        /// Copies the old shared pool into <paramref name="gameRoot"/> if this game is still listed
        /// in the manifest, writing the manifest first if this is the first launch after the
        /// cut-over. See the note on the type.
        /// </summary>
        private static void MigrateIfPending(string storesRoot, string productId, string sharedRoot, string gameRoot)
        {
            string manifest = Path.Combine(storesRoot, ManifestFileName);
            List<string> pending = LoadOrCreateManifest(storesRoot,
                Path.GetDirectoryName(WprHostEnvironment.CurrentInstallFolder?.TrimEnd('/', '\\')), productId);

            int index = pending.FindIndex(p => string.Equals(p, productId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            int copied = Directory.Exists(sharedRoot) ? CopyTree(sharedRoot, gameRoot) : 0;

            // Struck off only after the copy, so a copy that throws is retried next launch.
            pending.RemoveAt(index);
            WriteManifest(manifest, pending);
            Trace.WriteLine($"[wpr-isostore] {productId}: inherited {copied} file(s) from the shared store");
        }

        /// <summary>
        /// Deletes every file a game has saved — its whole per-game store — so it starts as if
        /// freshly installed. For the player whose game no longer starts because of something it
        /// saved, which is exactly what the shared-store era could leave behind. The game should
        /// not be running.
        ///
        /// <para>The game is struck off the migration manifest FIRST, and that is the half that
        /// matters most: a game still waiting to inherit the old shared pool would otherwise copy
        /// the very file that broke it straight back in on its next launch. When no manifest
        /// exists yet (nothing has launched since the cut-over) one is written now, listing every
        /// other installed game, so the others still inherit their saves. The old shared store
        /// itself is never touched.</para>
        /// </summary>
        /// <param name="installsRoot">The folder holding every game's install folder
        /// (<c>DataPath("AppData")</c>); a launcher has no running game to derive it from.</param>
        /// <exception cref="IOException">A file is still in use, typically by a running game.</exception>
        public static void ClearGameData(string productId, string installsRoot)
        {
            string? storesRoot = Configuration.Current?.DataPath(StoresFolderName);
            if (storesRoot == null || string.IsNullOrEmpty(productId) || !IsPlainFolderName(productId))
            {
                throw new InvalidOperationException($"No per-game store can exist for '{productId}'.");
            }

            lock (Gate)
            {
                List<string> pending = LoadOrCreateManifest(storesRoot, installsRoot, productId);
                if (pending.RemoveAll(p => string.Equals(p, productId, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    WriteManifest(Path.Combine(storesRoot, ManifestFileName), pending);
                }

                string gameFolder = Path.Combine(storesRoot, productId);
                if (Directory.Exists(gameFolder))
                {
                    Directory.Delete(gameFolder, recursive: true);
                }
            }

            Trace.WriteLine($"[wpr-isostore] {productId}: game data cleared by the player");
        }

        /// <summary>
        /// The ids still waiting to inherit the old shared pool, writing the manifest first if
        /// this is the first use of the per-game layout. See the note on the type.
        /// </summary>
        private static List<string> LoadOrCreateManifest(string storesRoot, string? installsRoot, string productId)
        {
            string manifest = Path.Combine(storesRoot, ManifestFileName);
            if (File.Exists(manifest))
            {
                return File.ReadAllLines(manifest)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                    .ToList();
            }

            List<string> pending = InstalledProductIds(installsRoot, productId);
            Directory.CreateDirectory(storesRoot);
            WriteManifest(manifest, pending);
            Trace.WriteLine($"[wpr-isostore] per-game stores introduced; {pending.Count} installed "
                + "game(s) will inherit the shared store");
            return pending;
        }

        /// <summary>
        /// Every folder under <paramref name="installsRoot"/>, i.e. every installed game. The
        /// given game is always included, so it migrates even if that folder cannot be read.
        /// </summary>
        private static List<string> InstalledProductIds(string? installsRoot, string productId)
        {
            List<string> ids = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(installsRoot) && Directory.Exists(installsRoot))
                {
                    ids.AddRange(Directory.GetDirectories(installsRoot!).Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n))!);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-isostore] could not list installed games: {ex.Message}");
            }

            if (!ids.Contains(productId, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(productId);
            }
            return ids;
        }

        private static void WriteManifest(string manifest, List<string> pending)
        {
            string temp = manifest + ".tmp";
            File.WriteAllLines(temp, new[]
            {
                "# Games that still have to inherit the pre-2026-09-25 shared isolated store.",
                "# Each is removed once its copy is made. Delete a line to make that game start empty.",
            }.Concat(pending));
            File.Move(temp, manifest, overwrite: true);
        }

        private static int CopyTree(string source, string destination)
        {
            int copied = 0;
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                    copied += 1;
                }
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                if (NotInherited.Contains(Path.GetFileName(dir)))
                {
                    continue;
                }
                copied += CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
            }
            return copied;
        }

        /// <summary>
        /// Folders deliberately left out of the migration copy. Both are Miniclip
        /// <c>DLCFramework</c>'s transient hand-off state — pending marketplace purchases carried
        /// across an exit or a tombstone, rewritten on every exit and meaningless here, where
        /// there is no marketplace. They are also exactly the files four games collide on, in two
        /// incompatible shapes, so inheriting them would hand whichever side lost the last write
        /// the very failure this type exists to end: measured, Fragger inherited Gravity Guy's
        /// copy and drew nothing on its first launch after the cut-over.
        /// </summary>
        private static readonly HashSet<string> NotInherited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$_StatesAfterExitData_$",
            "$_TombstoneData_$",
        };

        // A product id becomes a folder name, so refuse anything that could leave IsolatedStore.
        private static bool IsPlainFolderName(string name)
            => name != "." && name != ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
               && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;
    }
}
