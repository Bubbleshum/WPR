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
            public const string SilverlightAvalonia = "silverlight-avalonia";
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
