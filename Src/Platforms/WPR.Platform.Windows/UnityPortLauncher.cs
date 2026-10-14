using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using WPR.Common;
using WPR.Models;

using WPRModel = WPR.Models.Application;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// Desktop launcher for externally-built native ports (see <see cref="WPR.UnityPortManifest"/>).
    /// Unlike <see cref="XnaLauncher"/> — which hosts the game in-process in an FNA window — a port is its own standalone executable, so this
    /// just spawns it and waits for it to exit, mirroring the GameMaker <c>Runner.exe</c> fast-path.
    /// </summary>
    public static class UnityPortLauncher
    {
        private static Process? _current;

        /// <summary>
        /// Kills a running port process (if any) so it doesn't outlive the WPR window. Best-effort;
        /// safe to call from the UI thread. Returns true if one was running.
        /// </summary>
        public static bool RequestExit()
        {
            Process? p = _current;
            if (p == null) return false;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* already gone / access race — best-effort */ }
            return true;
        }

        /// <summary>
        /// If <paramref name="app"/> is a port (its install folder has a <c>wpr-port.json</c>),
        /// launch its Windows binary and await exit, returning true. Returns false when the title
        /// is not a port, so the caller falls through to the normal Silverlight / XNA hosts.
        /// A port with no usable Windows build also returns false, so the original package is
        /// attempted in-process rather than refused.
        /// </summary>
        public static async Task<bool> TryLaunchAsync(WPRModel app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            string installFolder = Path.Combine(
                Configuration.Current!.DataPath(WPRModel.DataStoreFolder),
                app.ProductId!);

            var manifest = WPR.UnityPortManifest.TryLoad(installFolder);
            if (manifest == null) return false; // not a port — let normal dispatch handle it

            string? exe = manifest.ResolveWindowsExe(installFolder);
            if (exe == null)
            {
                // Not a reason to refuse the game: fall through and attempt the original package
                // in-process, as for any title. If that fails, the run reports it to WPR Hub.
                // (This used to throw "a Unity port but has no Windows build", which nobody heard
                // about but the player.)
                Log.Warn(LogCategory.AppLaunch,
                    $"UnityPortLauncher: '{app.Name}' has a {WPR.UnityPortManifest.FileName} but no Windows build " +
                    $"under '{installFolder}'; attempting the original package in-process instead.");
                return false;
            }

            app.ApplicationType = ApplicationType.UnityPort;
            Log.Info(LogCategory.AppLaunch, $"UnityPortLauncher: spawning port '{exe}' for {app.Name}");

            await LaunchAndAwaitAsync(exe);
            return true;
        }

        private static async Task LaunchAndAwaitAsync(string exe)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();
            _current = proc;
            try
            {
                await Task.Run(() => proc.WaitForExit());
            }
            finally
            {
                _current = null;
            }
        }
    }
}
