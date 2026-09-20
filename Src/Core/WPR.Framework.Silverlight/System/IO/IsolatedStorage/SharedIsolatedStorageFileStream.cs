using System.IO;
using System.IO.IsolatedStorage;
using System.Threading;
using System.Threading.Tasks;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Drop-in substitute for <see cref="IsolatedStorageFileStream"/>, installed by
    /// <c>ApplicationPatcher.MemberPatches</c> for the two constructors WP7 games actually use
    /// (<c>(string,FileMode,IsolatedStorageFile)</c> and
    /// <c>(string,FileMode,FileAccess,IsolatedStorageFile)</c>). It behaves identically — same
    /// isolated-storage location, same access — except the underlying file is opened with
    /// <see cref="FileShare.ReadWrite"/> instead of the BCL default of no sharing.
    ///
    /// <para>Why WP7 games need this under WPR: WPR hosts every game in ONE long-lived process
    /// using collectible <c>AssemblyLoadContext</c>s. A game that keeps a <c>static</c>
    /// <see cref="IsolatedStorageFileStream"/> open then either</para>
    /// <list type="number">
    ///   <item>races an unsynchronised open across threads — Battleship's <c>Profiler.openLogFile</c>
    ///   has no lock around its <c>if (logStream != null) return;</c> guard, so a background
    ///   <c>RESTRequest</c> thread and the main thread can both <c>newobj</c> a stream over
    ///   <c>debug.log</c> at once; or</item>
    ///   <item>leaks the handle into the NEXT launch when its ALC hasn't finalised yet (the static
    ///   field keeps the stream — and its OS handle — alive).</item>
    /// </list>
    /// <para>On real WP7 each app was its own process that died on exit, so neither bit. Both
    /// surface here as <c>IsolatedStorageException -&gt; IOException "The process cannot access
    /// the file ... because it is being used by another process."</c> Sharing the handle makes
    /// the second open succeed instead of throwing.</para>
    ///
    /// <para><b>A <see cref="Flush()"/> after the stream is closed is a no-op here, not a throw.</b>
    /// That is the second WP7-ism this type absorbs, and it comes from the same place as the first:
    /// WP7 code could be careless with a stream's lifetime and never pay for it. The idiom is
    /// <c>using (var w = new StreamWriter(stream)) { …; stream.Close(); }</c> — closing the stream
    /// by hand inside the <c>using</c>, so the writer's own <c>Dispose</c> then flushes into an
    /// already-closed file. Contre Jour's <c>UserData.SaveUserData</c> does exactly this, and its
    /// <c>OnDeactivated</c> is a bare <c>SaveUserData(); ret;</c> with no <c>catch</c>, so the
    /// <see cref="System.ObjectDisposedException"/> unwound through <c>Game.Tick</c> and killed the
    /// run loop every time the game lost focus — which on Android is every time the player is
    /// interrupted.</para>
    ///
    /// <para><b>Why that shipped on WP7 and cannot here:</b> there, <c>Deactivated</c> meant the app
    /// was about to be tombstoned, so a handler that threw cost nothing — the process was going away
    /// regardless and the player saw a normal app switch. Under WPR the game is expected to keep
    /// running afterwards, so the identical throw is fatal. Relaxing the flush is exact rather than
    /// a fudge: <see cref="Stream.Close"/> has already flushed the buffers to disk and released the
    /// handle, so a later flush genuinely has nothing left to do. Note the relaxation stops at
    /// flushing — a <b>write</b> to a closed stream still throws, because that one really would lose
    /// data.</para>
    ///
    /// <para><b>A creating open makes its parent directory first.</b> That is the third WP7-ism,
    /// and like the other two it exists because WP7 gave a game an environment WPR does not.
    /// A WP7 app's isolated store was provisioned by the phone shell, so some folders were
    /// simply always there — <c>Shared/ShellContent</c>, where the OS required a secondary
    /// tile's background image to be written, is the documented one. Under WPR the store starts
    /// empty and nothing else ever creates them, so a game writing a live-tile image gets
    /// <c>DirectoryNotFoundException</c>, which the BCL rewraps as
    /// <c>IsolatedStorageException "Operation not permitted on IsolatedStorageFileStream"</c> —
    /// a message that says nothing about the missing folder.</para>
    ///
    /// <para>Chickens Can't Fly is the reference case, and the damage lands nowhere near the
    /// tile: <c>TilesHelper.UpdateBackgroundImage</c> writes <c>/Shared/ShellContent/main.png</c>
    /// from <c>RefreshMainTile()</c>, which the loading screen calls in <c>DoAfterLoading()</c>
    /// — so the throw skipped the <c>_loadingDone = true</c> on the next line and the game sat
    /// on its title screen for ever, still ticking, with the doors that reveal the menu never
    /// sliding. A live tile is cosmetic on WP7 and has no meaning at all here (<c>ShellTile</c>
    /// is an inert shim); failing a game's boot over it is not.</para>
    ///
    /// <para>Scoped to modes that create the file, so an open-for-read of a missing path still
    /// throws exactly what it threw before. It cannot lose data or paper over a game bug: the
    /// caller was about to create that file, and the only thing WP7 did differently was have
    /// the folder there already.</para>
    ///
    /// <para><b><see cref="IsolatedStorageFile.CreateDirectory"/> does NOT sandbox its argument</b>
    /// — measured, not assumed: <c>isf.CreateDirectory(@"..\..\..\..\X")</c> happily creates
    /// <c>X</c> outside the store. So the escaping shapes are screened out here instead, and a
    /// path carrying one is simply left to the BCL to reject exactly as it does today. Do not
    /// remove that screen on the belief that the store checks for you.</para>
    /// </summary>
    public sealed class SharedIsolatedStorageFileStream : IsolatedStorageFileStream
    {
        private volatile bool _closed;

        public SharedIsolatedStorageFileStream(string path, FileMode mode, IsolatedStorageFile isf)
            : base(EnsureParentDirectory(path, mode, isf), mode, DefaultAccess(mode), FileShare.ReadWrite, isf)
        {
        }

        public SharedIsolatedStorageFileStream(string path, FileMode mode, FileAccess access, IsolatedStorageFile isf)
            : base(EnsureParentDirectory(path, mode, isf), mode, access, FileShare.ReadWrite, isf)
        {
        }

        // Mirror the BCL's implicit access for the (path, mode, isf) ctor: Append is write-only,
        // everything else is read/write. Keeps behaviour identical apart from the share flag.
        private static FileAccess DefaultAccess(FileMode mode)
            => mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite;

        /// <summary>
        /// Best-effort <c>mkdir -p</c> of <paramref name="path"/>'s parent for the modes that can
        /// create a file, returning <paramref name="path"/> untouched so it can be used directly
        /// as the base constructor's first argument. See the note on the type.
        /// </summary>
        /// <remarks>
        /// Deliberately silent on failure: the very next thing that happens is the real open, so
        /// a genuine problem surfaces there as the exception the caller already expects, with the
        /// path it already names. Throwing a different one from here would only obscure it.
        /// <para><see cref="FileMode.Truncate"/> is excluded with the read modes — it requires the
        /// file to exist, so a missing directory is a real error there, not a missing folder WP7
        /// would have had.</para>
        /// </remarks>
        private static string EnsureParentDirectory(string path, FileMode mode, IsolatedStorageFile isf)
        {
            if (mode != FileMode.Create && mode != FileMode.CreateNew &&
                mode != FileMode.OpenOrCreate && mode != FileMode.Append)
            {
                return path;
            }

            try
            {
                // Path.GetDirectoryName over the store-relative path: it never touches the disk,
                // and both separators are handled because WP7 paths use either. Empty means the
                // file sits at the store root, which always exists.
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && IsContainedInStore(directory))
                {
                    isf.CreateDirectory(directory);
                }
            }
            catch
            {
                // See remarks.
            }

            return path;
        }

        /// <summary>
        /// True when <paramref name="directory"/> is a plain store-relative path that cannot
        /// resolve outside the store — the only shape this type will create. See the note on the
        /// type for why the store does not answer this itself.
        /// </summary>
        private static bool IsContainedInStore(string directory)
        {
            // A leading separator is not an escape: IsolatedStorageFile strips leading separators
            // before combining, which is why "/Shared/ShellContent/main.png" — the WP7 spelling —
            // lands inside the store. Anything still rooted after that (a drive letter, a UNC
            // share) would win the Path.Combine and escape.
            string relative = directory.TrimStart('/', '\\');
            if (relative.Length == 0 || Path.IsPathRooted(relative))
            {
                return false;
            }

            foreach (string segment in relative.Split('/', '\\'))
            {
                if (segment == "..")
                {
                    return false;
                }
            }

            return true;
        }

        // The flag is set in a finally so a throwing base.Dispose still leaves the stream marked
        // closed — the handle is gone either way, and a later flush must not resurrect the throw.
        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                _closed = true;
            }
        }

        /// <summary>No-op once closed — see the note on the type.</summary>
        public override void Flush()
        {
            if (_closed) return;
            base.Flush();
        }

        /// <summary>No-op once closed — see the note on the type.</summary>
        public override void Flush(bool flushToDisk)
        {
            if (_closed) return;
            base.Flush(flushToDisk);
        }

        /// <summary>No-op once closed — see the note on the type.</summary>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_closed) return Task.CompletedTask;
            return base.FlushAsync(cancellationToken);
        }
    }
}
