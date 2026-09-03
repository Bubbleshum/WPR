using System.IO;
using System.IO.IsolatedStorage;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Static stand-ins for <see cref="IsolatedStorageFile"/>'s file-opening methods, installed by
    /// <c>ApplicationPatcher.RedirectIsolatedStorageOpens</c>, which rewrites every
    /// <c>store.OpenFile(…)</c> / <c>store.CreateFile(…)</c> call site in a game to the matching
    /// method here. The instance becomes the first argument, so the IL stack is unchanged — only
    /// the callee moves.
    ///
    /// <para><b>Why the call sites have to move at all.</b>
    /// <see cref="SharedIsolatedStorageFileStream"/> already fixes this for games that
    /// <c>new</c> a stream directly, but <see cref="IsolatedStorageFile.OpenFile(string, FileMode)"/>
    /// constructs its <see cref="IsolatedStorageFileStream"/> *inside the BCL*, with
    /// <see cref="FileShare"/>.None — IL the patcher can never reach. A game that reaches its
    /// isolated storage through <c>OpenFile</c> therefore got none of that fix. Retargeting the
    /// member reference is not an option either: <see cref="IsolatedStorageFile"/> is sealed, so no
    /// stand-in type can stand where the instance is on the stack. Hence a static shim plus a
    /// call-site rewrite.</para>
    ///
    /// <para><b>What it changes.</b> Exactly one thing: the share mode is widened to
    /// <see cref="FileShare.ReadWrite"/>. Everything else — path, mode, the implicit access for
    /// each mode, the isolated-storage location, the exceptions thrown for a missing file or a
    /// bad mode — is whatever <see cref="IsolatedStorageFileStream"/> already does.</para>
    ///
    /// <para><b>Why widening is safe, and necessary.</b> On real WP7 each app was its own
    /// short-lived process, so a game could leak an isolated-storage handle for the rest of its
    /// life and never notice. WPR hosts games in one long-lived process behind collectible
    /// <c>AssemblyLoadContext</c>s, so a leaked handle outlives the read that opened it and blocks
    /// the next open of the same file. Angry Birds is the reference case: its reader
    /// (<c>al::b</c>) returns the file's bytes without ever closing the stream when the file is
    /// non-empty — only the zero-length branch calls <c>Close()</c>. Its writer (<c>al::a</c>)
    /// then opens the same path with <see cref="FileMode.Create"/>, collides with that leaked
    /// handle, swallows the <see cref="IsolatedStorageException"/> in its own <c>catch</c>, and
    /// falls through to <c>Write</c> on a null stream. The resulting
    /// <see cref="System.NullReferenceException"/> is eaten by the caller, so the game looks fine
    /// and simply never saves — measured on both <c>settings.lua</c> and <c>highscores.lua</c>,
    /// every launch. Sharing both ends makes the second open succeed.</para>
    ///
    /// <para>The four-argument overload's explicit <see cref="FileShare"/> is deliberately ignored
    /// for the same reason: a WP7 title asking for exclusive access was describing a world where
    /// it was the only process that could possibly hold the file. Under WPR it is describing a
    /// deadlock with itself.</para>
    /// </summary>
    public static class SharedIsolatedStorage
    {
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
    }
}
