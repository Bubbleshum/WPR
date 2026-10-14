using System;
using System.Collections.Generic;
using System.Text;

namespace WPR.Engine.Online
{
    /// <summary>
    /// A crash, as the engine saw it. Plain data on purpose, so it round-trips through a JSON file
    /// between the process that crashed and the one that sends it.
    /// </summary>
    public sealed class CrashReport
    {
        /// <summary>How WPR ran the game. Must match the hub's <c>DiagnosticReport::RUNTIME_PATHS</c>.</summary>
        public static class RuntimePaths
        {
            public const string XnaFna = "xna-fna";
            public const string UnitySwap = "unity-swap";
            public const string Other = "other";
        }

        /// <summary>Where a crash was caught. Sent to the hub in <c>extra.source</c>.</summary>
        public static class Sources
        {
            /// <summary>An exception escaped the game's run (init, the loop, or its construction).</summary>
            public const string GameRun = "game-run";

            /// <summary>An exception reached the process-wide unhandled handler; the process died.</summary>
            public const string Unhandled = "unhandled";

            /// <summary>The game process died without a managed exception (Android: SIGSEGV, abort).</summary>
            public const string NativeCrash = "native";

            /// <summary>
            /// Not a crash: a game was just installed. Sent as a hub "manual" report carrying only
            /// the package fingerprint, because a diagnostics receipt is the hub's one anonymous
            /// route to an upload offer. If the hub has never seen that build it asks for it, and
            /// the package goes up exactly as it would after a crash - so the hub collects every
            /// build anyone installs, not only the ones that fail.
            /// </summary>
            public const string Install = "install";

            /// <summary>
            /// Not a crash: the problems a game run met and carried on past - unknown types and
            /// members it caught, exceptions thrown out of its Update/Draw every frame, Silverlight
            /// features the rasteriser cannot draw. One per run, sent only when there were any and
            /// the run did not also crash (a crash report carries them itself). See
            /// <see cref="CompatibilityProblems"/>.
            /// </summary>
            public const string Compatibility = "compat";

            /// <summary>
            /// Not a crash: WPR declined to attempt a package or a launch at all (no manifest, a
            /// package type it has no runtime for). Tells the hub which titles people try.
            /// </summary>
            public const string Refused = "refused";

            /// <summary>True for the sources that are not crashes and must never be grouped as one.</summary>
            public static bool IsNotACrash(string? source) => source is Install or Compatibility or Refused;

            /// <summary>True for the sources that carry a managed exception from inside a game run.</summary>
            public static bool IsManagedCrash(string? source) => source is GameRun or Unhandled;
        }

        /// <summary>
        /// A package or launch WPR refused before running anything. <paramref name="reason"/> is a
        /// short code (an <c>ApplicationInstallError</c> name, <c>unity-port-without-build</c>, ...)
        /// and <paramref name="detail"/> the human sentence. Any identity field may be null: a
        /// package with no manifest has no title to name.
        /// </summary>
        public static CrashReport ForRefusal(string stage, string reason, string? detail,
            string? titleId, string? titleName, GamePackageRecord? package, string? installFolder = null)
        {
            CrashReport report = new CrashReport
            {
                Source = Sources.Refused,
                TitleId = titleId,
                TitleName = titleName,
                InstallFolder = installFolder,
                Package = package,
                RuntimePath = RuntimePaths.Other,
                ExceptionType = "WPR.Refused." + reason,
                ExceptionMessage = detail ?? reason,
            };
            report.Extra["stage"] = stage;
            report.Extra["reason"] = reason;
            if (package?.PackageFileName is { } file) report.Extra["package_file"] = file;
            return report;
        }

        /// <summary>An install report for a game just installed. See <see cref="Sources.Install"/>.</summary>
        public static CrashReport ForInstall(string? titleId, string? titleName, string installFolder, string runtimePath) =>
            new CrashReport
            {
                Source = Sources.Install,
                TitleId = titleId,
                TitleName = titleName,
                InstallFolder = installFolder,
                RuntimePath = runtimePath,
                LogTail = "", // nothing has run yet; "" rather than null so the reporter does not read a stale log
            };

        /// <summary>The game's ProductId (braces and case do not matter).</summary>
        public string? TitleId { get; set; }

        public string? TitleName { get; set; }

        /// <summary>
        /// The game's install folder. The reporter takes the per-game log's tail from here when
        /// the crash is queued, and the package fingerprint (<see cref="GamePackageRecord"/>)
        /// when it is sent.
        /// </summary>
        public string? InstallFolder { get; set; }

        public string RuntimePath { get; set; } = RuntimePaths.XnaFna;

        /// <summary>One of <see cref="Sources"/>.</summary>
        public string Source { get; set; } = Sources.GameRun;

        /// <summary>The FNA3D driver that was running, when known (<c>Vulkan</c>, <c>OpenGL</c>, <c>D3D11</c>).</summary>
        public string? GraphicsBackend { get; set; }

        public string? ExceptionType { get; set; }

        public string? ExceptionMessage { get; set; }

        /// <summary>Innermost exception first, which is what the hub fingerprints on.</summary>
        public string? StackTrace { get; set; }

        public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// The package fingerprint when there is no install folder to read it from (a refused
        /// package never got one). <see cref="InstallFolder"/>'s record wins when both exist.
        /// </summary>
        public GamePackageRecord? Package { get; set; }

        /// <summary>
        /// Extra key/value pairs for the hub's <c>extra</c>. Keep it under ~45 entries: the hub
        /// accepts 50 and the reporter adds its own. Problems and refusal reasons go here.
        /// </summary>
        public Dictionary<string, string> Extra { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// The end of the per-game log, captured by the reporter when the crash is queued. It
        /// cannot wait until send time: the next run of the same game overwrites the log.
        /// </summary>
        public string? LogTail { get; set; }

        public static CrashReport FromException(Exception ex, string source)
        {
            Exception root = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerException! : ex;
            root = Unwrap(root);

            return new CrashReport
            {
                Source = source,
                ExceptionType = root.GetType().FullName,
                ExceptionMessage = root.Message,
                StackTrace = FullStackTrace(root),
            };
        }

        /// <summary>
        /// A TargetInvocationException or TypeInitializationException says where the reflection
        /// call was, not what failed. The hub groups crashes by their top frames, so leaving the
        /// wrapper on top would file every crash in a game's constructor under one group.
        /// </summary>
        private static Exception Unwrap(Exception ex)
        {
            while (ex is System.Reflection.TargetInvocationException or TypeInitializationException
                   && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex;
        }

        private static string FullStackTrace(Exception ex)
        {
            List<Exception> chain = new List<Exception>();
            for (Exception? e = ex; e != null; e = e.InnerException) chain.Add(e);
            chain.Reverse();

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < chain.Count; i++)
            {
                if (i > 0) sb.AppendLine($"--- wrapped by {chain[i].GetType().FullName}: {chain[i].Message}");
                sb.AppendLine(chain[i].StackTrace);
            }
            return sb.ToString();
        }
    }
}
