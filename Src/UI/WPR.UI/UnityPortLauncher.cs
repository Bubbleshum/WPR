using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using WPR.Common;
using WPR.Models;

using WPRModel = WPR.Models.Application;

namespace WPR.UI
{
    /// <summary>
    /// Desktop launcher for externally-built native ports (see <see cref="WPR.UnityPortManifest"/>).
    /// Unlike <see cref="SilverlightLauncher"/> / <see cref="XnaLauncher"/> — which host the game
    /// in-process in an Avalonia / FNA window — a port is its own standalone executable, so this
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
        /// Throws if the title <em>is</em> a port but has no usable Windows build — the desktop
        /// host surfaces that as an error dialog.
        /// </summary>
        public static async Task<bool> TryLaunchAsync(WPRModel app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            string installFolder = Path.Combine(
                Configuration.Current!.DataPath(WPRModel.DataStoreFolder),
                app.ProductId!);

            var manifest = WPR.UnityPortManifest.TryLoad(installFolder);
            if (manifest == null) return false; // not a port — let normal dispatch handle it

            app.ApplicationType = ApplicationType.UnityPort;

            string? exe = manifest.ResolveWindowsExe(installFolder);
            if (exe == null)
                throw new InvalidOperationException(
                    $"'{app.Name}' is a Unity port but has no Windows build. Expected the executable named " +
                    $"by \"windows\" in {WPR.UnityPortManifest.FileName} to exist under '{installFolder}'. " +
                    "Rebuild the Unity project for the Windows Standalone target and place the binary in the " +
                    "install folder (see Docs/Unity_WP8_Feasibility.md).");

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
