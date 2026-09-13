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
    /// </summary>
    public sealed class SharedIsolatedStorageFileStream : IsolatedStorageFileStream
    {
        private volatile bool _closed;

        public SharedIsolatedStorageFileStream(string path, FileMode mode, IsolatedStorageFile isf)
            : base(path, mode, DefaultAccess(mode), FileShare.ReadWrite, isf)
        {
        }

        public SharedIsolatedStorageFileStream(string path, FileMode mode, FileAccess access, IsolatedStorageFile isf)
            : base(path, mode, access, FileShare.ReadWrite, isf)
        {
        }

        // Mirror the BCL's implicit access for the (path, mode, isf) ctor: Append is write-only,
        // everything else is read/write. Keeps behaviour identical apart from the share flag.
        private static FileAccess DefaultAccess(FileMode mode)
            => mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite;

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
