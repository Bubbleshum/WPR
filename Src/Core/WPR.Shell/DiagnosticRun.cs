using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using WPR.Common;

namespace WPR.Shell
{
    /// <summary>
    /// "Play with logging": one game run with the verbose log switched on, ended by the player's
    /// "stop and send report" button (or by the game ending), after which the launcher sends that
    /// run's log to WPR Hub as a report. It replaces the settings pages' "send a report" box, whose
    /// fixed 20-minute window caught everything except, usually, the moment that mattered.
    /// </summary>
    /// <remarks>
    /// <para><b>The run is a file, not a field</b>, at <c>&lt;DataStore&gt;/Online/diagnostic-run.txt</c>.
    /// On Android the game runs in the <c>:game</c> process, which the stop button kills, and the
    /// launcher may itself have been recreated by then; a file is what both processes and both
    /// lifetimes can see. The desktop runs games in-process and uses the same file for symmetry.</para>
    ///
    /// <para><b>The report is always sent from the launcher</b>, the process that outlives the game —
    /// the same rule as crash reports. The stop button only records that the player asked, and ends
    /// the game.</para>
    ///
    /// <para>What is sent is <see cref="SessionLog"/> from the moment the run began (both processes,
    /// merged by time, which in a logging run includes every first-chance exception with its stack),
    /// plus whatever the head adds (Android: its own logcat). It is scrubbed of user names, paths,
    /// emails and addresses by the hub module before it leaves the device.</para>
    /// </remarks>
    public static class DiagnosticRun
    {
        public sealed record Run(string ProductId, string Title, DateTimeOffset StartedAt, bool StopRequested)
        {
            /// <summary>How far back to read the logs: from the start of the run, plus a minute of lead-in.</summary>
            public TimeSpan Window => DateTimeOffset.UtcNow - StartedAt + TimeSpan.FromMinutes(1);
        }

        private static string? FilePath() =>
            Configuration.Current == null ? null : Path.Combine(Configuration.Current.DataPath("Online"), "diagnostic-run.txt");

        /// <summary>Record that a logging run of this game is starting. Replaces any earlier record.</summary>
        public static void Begin(string productId, string title)
        {
            Write(new Run(productId, title, DateTimeOffset.UtcNow, StopRequested: false));
            Log.Info(LogCategory.AppList, $"[wpr-diag] logging run started: {title} ({productId})");
        }

        /// <summary>The run in progress, or null. Never throws; a torn file reads as no run.</summary>
        public static Run? Current()
        {
            try
            {
                string? path = FilePath();
                if (path == null || !File.Exists(path)) return null;
                string[] parts = File.ReadAllText(path).Split('\n');
                if (parts.Length < 4) return null;
                return new Run(parts[0].Trim(), parts[1].Trim(),
                    new DateTimeOffset(long.Parse(parts[2].Trim(), CultureInfo.InvariantCulture), TimeSpan.Zero),
                    parts[3].Trim() == "stop");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The player pressed "stop and send report". Called from the game's side, just before it ends the game.</summary>
        public static void MarkStopRequested()
        {
            Run? run = Current();
            if (run == null) return;
            Write(run with { StopRequested = true });
            Log.Info(LogCategory.AppList, $"[wpr-diag] stop and send requested for {run.Title}");
            SessionLog.Flush();
        }

        /// <summary>The run for this game, removed so it is reported once. Null when there was none.</summary>
        public static Run? Take(string productId)
        {
            Run? run = Current();
            if (run == null || !string.Equals(run.ProductId, productId, StringComparison.OrdinalIgnoreCase)) return null;
            try { File.Delete(FilePath()!); } catch (Exception) { /* reported once is best effort */ }
            return run;
        }

        /// <summary>
        /// Send the run's log as a WPR Hub report and return its code. <paramref name="outcome"/> says
        /// how the run ended ("stopped by the player", "the game crashed", …); <paramref name="headLog"/>
        /// is what the head adds, e.g. Android's logcat for the same window. Throws when the hub cannot
        /// be reached, for the head to show.
        /// </summary>
        public static async Task<string> SendAsync(Run run, string outcome, string? headLog, IReadOnlyDictionary<string, string>? extra = null,
            string? runtimePath = null, string? graphicsBackend = null)
        {
            var hub = HubSetup.Current ?? throw new InvalidOperationException("WPR Hub is not available.");

            string log = await Task.Run(() =>
            {
                string text = $"===== logging run: {run.Title} ({run.ProductId}) =====\n" +
                              $"started {run.StartedAt:u}, ended {DateTimeOffset.UtcNow:u}, {outcome}\n\n" +
                              SessionLog.ReadRecent(run.Window);
                if (!string.IsNullOrEmpty(headLog)) text += "\n\n" + headLog;
                return text;
            }).ConfigureAwait(false);

            var fields = new Dictionary<string, string>
            {
                ["diagnostic_run"] = "1",
                ["title_id"] = run.ProductId,
                ["title_name"] = run.Title,
                ["outcome"] = outcome,
                ["run_seconds"] = ((long)(DateTimeOffset.UtcNow - run.StartedAt).TotalSeconds).ToString(CultureInfo.InvariantCulture),
            };
            if (extra != null) foreach (var kv in extra) fields[kv.Key] = kv.Value;

            string comment = $"Logging run of {run.Title}: {outcome}.";
            // The game goes in the report's own title fields, not only in extra: those are what the hub
            // names the report by, and the install's package record ties it to the exact build.
            string installFolder = Path.Combine(Configuration.Current!.DataPath(Models.Application.DataStoreFolder), run.ProductId);
            var game = new WPR.Online.Hub.ManualReportGame(run.ProductId, run.Title, installFolder, runtimePath, graphicsBackend);
            string code = await hub.Crashes.SendManualAsync(comment, log, fields, game).ConfigureAwait(false);
            Log.Info(LogCategory.AppList, $"[wpr-diag] report {code} sent for {run.Title}");
            return code;
        }

        private static void Write(Run run)
        {
            try
            {
                string? path = FilePath();
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Join("\n",
                    run.ProductId, run.Title.Replace('\n', ' '),
                    run.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
                    run.StopRequested ? "stop" : "running"));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, "[wpr-diag] could not record the logging run: " + ex.Message);
            }
        }
    }
}
