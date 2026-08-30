using System.IO;
using System.IO.IsolatedStorage;

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
    /// </summary>
    public sealed class SharedIsolatedStorageFileStream : IsolatedStorageFileStream
    {
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
    }
}
