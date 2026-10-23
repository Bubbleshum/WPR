using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Target of every patched named <c>Mutex</c> constructor (<c>new Mutex(bool, string)</c> and
    /// the <c>out bool createdNew</c> form) and of <c>Mutex.OpenExisting(string)</c>.
    ///
    /// <para>On Android a named mutex cannot be created at all. CoreCLR keeps named objects in
    /// shared memory under <c>TMPDIR</c>, the Android host leaves <c>TMPDIR</c> unset, and the
    /// runtime fixes the directory at start-up to <c>/data/local/tmp</c>, which an app may not
    /// write: <c>IOException: The system cannot open the device or file specified ...
    /// mkdtemp("/data/local/tmp/.dotnet.XXXXXX") == nullptr; errno == EACCES</c>. Nothing managed
    /// can move it, because the path is chosen before managed code runs. AE Fruit Slash's
    /// <c>AEGame</c> constructor creates <c>Mutex(false, "AEGameGraphicsDeviceControl")</c>, so
    /// the game could not start on a phone, and its save path takes another.</para>
    ///
    /// <para>So the real named mutex is used wherever the OS gives one (the desktop), and where it
    /// does not, a process-local mutex shared by name: every <c>Mutex</c> made for a name waits on
    /// the same object, so ownership and exclusion are exactly a named mutex's within the
    /// process. On WP7 the only other process that could share one was the app's own background
    /// agent, which WPR does not run. Each caller still gets its own <c>Mutex</c>; its handle does
    /// not own the shared object, so disposing it leaves the others working.</para>
    /// </summary>
    public static class NamedMutex
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Mutex> Shared = new Dictionary<string, Mutex>(StringComparer.Ordinal);

        /// <summary>True once the OS has refused a named mutex; later ones go straight to the
        /// process-local table, so one name is never split between the two.</summary>
        private static bool _osRefused;

        public static Mutex Create(bool initiallyOwned, string name) => Create(initiallyOwned, name, out _);

        public static Mutex Create(bool initiallyOwned, string name, out bool createdNew)
        {
            if (string.IsNullOrEmpty(name))
            {
                createdNew = true;
                return new Mutex(initiallyOwned);
            }

            if (!Volatile.Read(ref _osRefused))
            {
                try
                {
                    return new Mutex(initiallyOwned, name, out createdNew);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Volatile.Write(ref _osRefused, true);
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-mutex] named mutexes are process-local in this process: \"{name}\" was refused ({ex.GetType().Name}: {ex.Message})");
                }
            }

            return CreateProcessLocal(initiallyOwned, name, out createdNew);
        }

        public static Mutex OpenExisting(string name)
        {
            if (!Volatile.Read(ref _osRefused))
            {
                try
                {
                    return Mutex.OpenExisting(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Volatile.Write(ref _osRefused, true);
                }
            }

            lock (Gate)
            {
                if (!Shared.ContainsKey(name))
                {
                    throw new WaitHandleCannotBeOpenedException();
                }
            }
            return CreateProcessLocal(false, name, out _);
        }

        /// <summary>The fallback, public so it can be tested on a machine that has named mutexes.</summary>
        public static Mutex CreateProcessLocal(bool initiallyOwned, string name, out bool createdNew)
        {
            Mutex shared;
            lock (Gate)
            {
                createdNew = !Shared.TryGetValue(name, out shared!);
                if (createdNew)
                {
                    shared = new Mutex(false);
                    Shared[name] = shared;
                }
            }

            var view = new Mutex(false);
            SafeWaitHandle own = view.SafeWaitHandle;
            view.SafeWaitHandle = new SafeWaitHandle(shared.SafeWaitHandle.DangerousGetHandle(), ownsHandle: false);
            own.Dispose();

            // As with a named mutex, initiallyOwned is honoured only by the call that created it.
            if (initiallyOwned && createdNew)
            {
                view.WaitOne();
            }
            return view;
        }
    }
}
