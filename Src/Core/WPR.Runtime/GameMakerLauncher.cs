using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Detects whether an installed app is a GameMaker Studio export and, if so, runs it via
    /// YoYo's official <c>Runner.exe</c> instead of through the Silverlight host.
    ///
    /// Heuristic for "is this a GMS app":
    /// - Has an <c>Assets/game.win</c> file (the GameMaker compiled game data).
    /// - Has a native WP component DLL — typically <c>WinPhoneRunnerAppComponent.dll.native_original</c>
    ///   after WPR's installer stubs it. This is the WP-specific GMS runtime, distinct from
    ///   pure-Silverlight WP apps.
    ///
    /// Runtime sourcing:
    ///   <see cref="LocateRunnerExe"/> looks under <c>%LOCALAPPDATA%\WPR\GMRuntime</c> first
    ///   (the user's pinned cache), then under the source-tree <c>Tools/GameMaker/runtimes/</c>
    ///   (dev convenience). If neither has a Runner, returns null and the caller should fall
    ///   back to the normal Silverlight host.
    /// </summary>
    public static class GameMakerLauncher
    {
        /// <summary>True if the install folder looks like a GMS Studio export.</summary>
        public static bool LooksLikeGameMakerApp(string installFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(installFolder) || !Directory.Exists(installFolder))
                    return false;

                string winPath = Path.Combine(installFolder, "Assets", "game.win");
                return File.Exists(winPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Path to the .win the launcher should hand to Runner.exe. Prefers
        /// <c>game.win.patched</c> (sibling produced by <see cref="GameMakerWinPatcher"/>
        /// at install time) when it exists, falls back to <c>game.win</c>.
        /// Returns null if neither is present.
        /// </summary>
        public static string? FindGameWin(string installFolder)
        {
            string original = Path.Combine(installFolder, "Assets", "game.win");
            if (!File.Exists(original)) return null;

            string patched = original + GameMakerWinPatcher.PatchedSuffix;
            if (File.Exists(patched))
            {
                Log.Info(LogCategory.AppLaunch,
                    $"GameMakerLauncher: using patched sibling {Path.GetFileName(patched)}");
                return patched;
            }
            return original;
        }

        /// <summary>
        /// Locate <c>Runner.exe</c> from the GameMaker runtime cache. Searches common paths
        /// in priority order. Returns null if no installed runtime is found — the caller should
        /// either fall back to Silverlight or trigger a runtime install (not implemented yet).
        /// </summary>
        public static string? LocateRunnerExe()
        {
            foreach (var root in EnumerateRuntimeRoots())
            {
                if (root == null || !Directory.Exists(root)) continue;
                string? runner = FindRunnerUnder(root);
                if (runner != null) return runner;
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<string?> EnumerateRuntimeRoots()
        {
            // 1. User cache (preferred, will be the install location after Phase 1).
            string? appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (appData != null)
                yield return Path.Combine(appData, "WPR", "GMRuntime");

            // 2. Source-tree dev cache: Tools/GameMaker/runtimes/runtime-X.Y.Z.W
            //    Walks up from the current AppDomain base looking for "Tools/GameMaker".
            //    Lets the dev-build pick up the binaries without an extra install step.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Tools", "GameMaker");
                if (Directory.Exists(candidate))
                {
                    yield return candidate;
                    break;
                }
            }
        }

        private static string? FindRunnerUnder(string root)
        {
            // Two layouts to try:
            //   <root>/runtimes/runtime-*/windows/Runner.exe   (Igor install layout)
            //   <root>/runtime-*/windows/Runner.exe            (alternate)
            //   <root>/windows/Runner.exe                      (direct)
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(root, "Runner.exe", SearchOption.AllDirectories);
            }
            catch
            {
                return null;
            }

            // Prefer the highest-versioned runtime when multiple are present.
            return candidates
                .OrderByDescending(p => p)
                .FirstOrDefault(p => p.IndexOf("\\windows\\", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Spawn Runner.exe pointing at the app's <c>game.win</c>. Returns the launched process,
        /// or null if no runtime / game data is available. The caller owns the lifetime — kill
        /// it when the WPR window closes.
        ///
        /// Stdout is captured so <see cref="GameMakerAchievementBridge"/> can pick up the
        /// <c>WPR_ACH:*</c> lines emitted by patched GML scripts and persist them into the
        /// shared <see cref="Microsoft.Xna.Framework.GamerServices.AchievementContext"/>.
        /// </summary>
        public static Process? Launch(string installFolder, string productId, string appName)
        {
            string? gameWin = FindGameWin(installFolder);
            if (gameWin == null)
            {
                Log.Warn(LogCategory.AppLaunch, $"GameMakerLauncher: no game.win in '{installFolder}'");
                return null;
            }
            string? runner = LocateRunnerExe();
            if (runner == null)
            {
                Log.Warn(LogCategory.AppLaunch,
                    "GameMakerLauncher: no Runner.exe available — install a GMS runtime under " +
                    "%LOCALAPPDATA%\\WPR\\GMRuntime or Tools/GameMaker/runtimes");
                return null;
            }

            Log.Info(LogCategory.AppLaunch, $"GameMakerLauncher: spawning {runner} -game {gameWin}");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = runner,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(gameWin) ?? installFolder,
                };
                psi.ArgumentList.Add("-game");
                psi.ArgumentList.Add(gameWin);

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    GameMakerAchievementBridge.HandleStdoutLine(e.Data, productId, appName);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    Log.Info(LogCategory.AppLaunch, $"Runner stderr: {e.Data}");
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                return proc;
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppLaunch, $"GameMakerLauncher: failed to start Runner: {ex}");
                return null;
            }
        }
    }
}
