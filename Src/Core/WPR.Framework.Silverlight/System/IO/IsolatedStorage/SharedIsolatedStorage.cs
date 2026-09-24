using System;
using System.IO;
using System.IO.IsolatedStorage;

using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Static stand-ins for <see cref="IsolatedStorageFile"/>'s path-taking methods, installed by
    /// <c>ApplicationPatcher.RedirectIsolatedStorageCalls</c>, which rewrites every
    /// <c>store.Member(…)</c> call site in a game to the matching method here. The instance becomes
    /// the first argument, so the IL stack is unchanged — only the callee moves.
    ///
    /// <para><b>Why the call sites have to move at all.</b> <see cref="IsolatedStorageFile"/> is
    /// SEALED, so <c>MemberPatches</c> — which installs a replacement by retargeting a member
    /// reference's declaring type — cannot be used: no stand-in type can stand where the instance
    /// is on the stack. That is the same wall <c>FileInfo</c> and <c>DirectoryInfo</c> sit behind.
    /// A static shim plus a call-site rewrite is the only mechanism available for this type.</para>
    ///
    /// <para><b>This type absorbs TWO unrelated WP7-isms. Keep them straight.</b></para>
    ///
    /// <list type="number">
    ///   <item><b>Share mode</b> (patcher v20), which applies to the opening members only. On real
    ///   WP7 each app was its own short-lived process, so a game could leak an isolated-storage
    ///   handle for the rest of its life and never notice. WPR hosts games in one long-lived
    ///   process behind collectible <c>AssemblyLoadContext</c>s, so a leaked handle outlives the
    ///   read that opened it and blocks the next open of the same file. Angry Birds is the
    ///   reference case: its reader (<c>al::b</c>) returns the file's bytes without ever closing
    ///   the stream when the file is non-empty — only the zero-length branch calls <c>Close()</c>.
    ///   Its writer (<c>al::a</c>) then opens the same path with <see cref="FileMode.Create"/>,
    ///   collides with that leaked handle, swallows the <see cref="IsolatedStorageException"/> in
    ///   its own <c>catch</c>, and falls through to <c>Write</c> on a null stream. The resulting
    ///   <see cref="NullReferenceException"/> is eaten by the caller, so the game looks fine and
    ///   simply never saves. Sharing both ends makes the second open succeed.</item>
    ///
    ///   <item><b>Path separators</b> (patcher v31), which applies to EVERY member here. WP7 titles
    ///   were built on Windows, where <c>\</c> and <c>/</c> are interchangeable, so hardcoded
    ///   Windows paths are everywhere in game IL. On Android <c>\</c> is an ordinary filename
    ///   character, so the call silently addresses something that does not exist. Every path and
    ///   search pattern therefore goes through <see cref="ContentPaths.Normalize"/> first — a
    ///   no-op on Windows, so the desktop head is unchanged.</item>
    /// </list>
    ///
    /// <para>The two reasons are independent: v20 needed only the opening members and was never
    /// about paths, which is why the rest of this surface went uncovered until a game was found
    /// that depended on it.</para>
    ///
    /// <para><b>The reference case for the separator half is Funny Bounce</b> (AE Mobile,
    /// <c>f84a19d8-2820-41a6-b972-1f0c7da88196</c>), reported as "blue screen after game over" and
    /// Android-only. <c>AEMobile.GameFramework.Helper.GetGameData</c> enumerates the high-score
    /// directory with <c>GetFileNames(string.Format("{0}\\*", dir))</c>. On Android that pattern
    /// matches nothing, so the method returns an EMPTY list — its own <c>catch { }</c> would have
    /// hidden a failure anyway — and <c>GameOverScreen.LoadContent</c> then runs
    /// <c>.OrderByDescending(d =&gt; d.Score).First()</c> over it and throws
    /// <see cref="InvalidOperationException"/> "Sequence contains no elements". LoadContent aborts
    /// before building its fonts, its menu entries and <c>base.LoadContent()</c>, so every
    /// subsequent <c>GameScreen.Draw</c> dereferences a half-built screen — once per frame, caught
    /// and swallowed by <c>Game.Tick</c>, for ever. What the player sees is the game's own
    /// <c>Clear(CornflowerBlue)</c> and nothing else.</para>
    ///
    /// <para><b>The recogniser worth remembering: a game that renders one flat colour, ignores all
    /// input and never crashes, on Android only, is a Windows path separator above the seam</b> —
    /// not a renderer bug. It is the same defect class as patcher v23, which fixed it for
    /// <c>FileStream</c> / <c>StreamReader</c> / <c>StreamWriter</c> / <c>XmlReader.Create</c> by
    /// subclassing; those types are not sealed, so they could be reached that way and this one
    /// could not.</para>
    ///
    /// <para><b><see cref="ContentPaths.Normalize"/>, never <c>Resolve</c>.</b> Resolve may redirect
    /// a read to a file that already exists in the install folder. These paths are store-relative
    /// and mean a location inside the isolated store, so probing the install folder would be
    /// meaningless at best and could silently land a create on an unrelated file. Separator
    /// translation is the whole of what is wanted here.</para>
    ///
    /// <para><b>Normalize is correct for a SEARCH PATTERN too, not just a path.</b>
    /// <c>"GameData_Normal\*"</c> becomes <c>"GameData_Normal/*"</c>, which is exactly what the BCL
    /// wants on Unix: it splits a pattern into its directory and file parts using the platform's
    /// separators, so a backslash there is otherwise just part of the filename being matched.</para>
    ///
    /// <para><b>Parameterless overloads are deliberately absent.</b> <c>GetFileNames()</c> and
    /// <c>GetDirectoryNames()</c> take no path, so there is nothing to normalise and nothing to
    /// gain by routing them through here. The rewriter logs "no shim" and leaves them alone, which
    /// is the intended outcome — do not add them for symmetry.</para>
    /// </summary>
    public static class SharedIsolatedStorage
    {
        // ---- opening: share mode widened (v20), and the path normalised by the stream shim -----

        public static IsolatedStorageFileStream OpenFile(
            IsolatedStorageFile store, string path, FileMode mode)
            => new SharedIsolatedStorageFileStream(path, mode, store);

        public static IsolatedStorageFileStream OpenFile(
            IsolatedStorageFile store, string path, FileMode mode, FileAccess access)
            => new SharedIsolatedStorageFileStream(path, mode, access, store);

        /// <param name="share">Ignored — see the type remarks.</param>
        public static IsolatedStorageFileStream OpenFile(
            IsolatedStorageFile store, string path, FileMode mode, FileAccess access, FileShare share)
            => new SharedIsolatedStorageFileStream(path, mode, access, store);

        /// <summary>
        /// Mirrors <see cref="IsolatedStorageFile.CreateFile(string)"/>, which is
        /// <c>OpenFile(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None)</c>.
        /// </summary>
        public static IsolatedStorageFileStream CreateFile(IsolatedStorageFile store, string path)
            => new SharedIsolatedStorageFileStream(path, FileMode.Create, FileAccess.ReadWrite, store);

        // ---- everything else: separators only (v31) -------------------------------------------
        //
        // Each one does exactly one thing before delegating. No behaviour changes: same store,
        // same semantics, same exceptions for a missing file or a bad path. A null argument is
        // passed straight through, so the BCL throws the ArgumentNullException the caller expects
        // rather than this shim throwing a different one from a different place.

        public static string[] GetFileNames(IsolatedStorageFile store, string searchPattern)
            => store.GetFileNames(ContentPaths.Normalize(searchPattern)!);

        public static string[] GetDirectoryNames(IsolatedStorageFile store, string searchPattern)
            => store.GetDirectoryNames(ContentPaths.Normalize(searchPattern)!);

        public static bool DirectoryExists(IsolatedStorageFile store, string path)
            => store.DirectoryExists(ContentPaths.Normalize(path)!);

        public static bool FileExists(IsolatedStorageFile store, string path)
            => store.FileExists(ContentPaths.Normalize(path)!);

        public static void CreateDirectory(IsolatedStorageFile store, string dir)
            => store.CreateDirectory(ContentPaths.Normalize(dir)!);

        public static void DeleteDirectory(IsolatedStorageFile store, string dir)
            => store.DeleteDirectory(ContentPaths.Normalize(dir)!);

        public static void DeleteFile(IsolatedStorageFile store, string file)
            => store.DeleteFile(ContentPaths.Normalize(file)!);

        public static void MoveFile(
            IsolatedStorageFile store, string sourceFileName, string destinationFileName)
            => store.MoveFile(
                ContentPaths.Normalize(sourceFileName)!,
                ContentPaths.Normalize(destinationFileName)!);

        public static void MoveDirectory(
            IsolatedStorageFile store, string sourceDirectoryName, string destinationDirectoryName)
            => store.MoveDirectory(
                ContentPaths.Normalize(sourceDirectoryName)!,
                ContentPaths.Normalize(destinationDirectoryName)!);

        public static void CopyFile(
            IsolatedStorageFile store, string sourceFileName, string destinationFileName)
            => store.CopyFile(
                ContentPaths.Normalize(sourceFileName)!,
                ContentPaths.Normalize(destinationFileName)!);

        public static void CopyFile(
            IsolatedStorageFile store, string sourceFileName, string destinationFileName, bool overwrite)
            => store.CopyFile(
                ContentPaths.Normalize(sourceFileName)!,
                ContentPaths.Normalize(destinationFileName)!,
                overwrite);

        public static DateTimeOffset GetCreationTime(IsolatedStorageFile store, string path)
            => store.GetCreationTime(ContentPaths.Normalize(path)!);

        public static DateTimeOffset GetLastAccessTime(IsolatedStorageFile store, string path)
            => store.GetLastAccessTime(ContentPaths.Normalize(path)!);

        public static DateTimeOffset GetLastWriteTime(IsolatedStorageFile store, string path)
            => store.GetLastWriteTime(ContentPaths.Normalize(path)!);
    }
}
