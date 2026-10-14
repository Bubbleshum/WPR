using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WPR.Engine.Online
{
    /// <summary>Which run a persisted problem list belongs to.</summary>
    public sealed class RunIdentity
    {
        public string? ProductId { get; set; }
        public string? TitleName { get; set; }
        public string? InstallFolder { get; set; }
        public string? GraphicsBackend { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>The process the run is in, so a live run is never taken for an abandoned one.</summary>
        public int ProcessId { get; set; } = Environment.ProcessId;
    }

    /// <summary>A run's problem list read back from <see cref="CompatibilityProblems.RunFilePath"/>.</summary>
    public sealed class PersistedRun
    {
        public RunIdentity Run { get; set; } = new RunIdentity();
        public List<CompatibilityProblem> Problems { get; } = new List<CompatibilityProblem>();

        /// <summary>How long ago the run started, as a stand-in for how long it ran.</summary>
        public TimeSpan Age => DateTimeOffset.UtcNow - Run.StartedAt;
    }

    public static partial class CompatibilityProblems
    {
        /// <summary>
        /// Where a run's problems are persisted as they are met (<c>&lt;DataStore&gt;/Online/compat-run.txt</c>,
        /// set by the head's hub setup). Null: nothing is persisted, which is what tests and a
        /// platform without WPR Hub get.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a file.</b> The list lives in the game process's memory, and a native crash
        /// (Android's SIGSEGV/SIGABRT in the <c>:game</c> process, the desktop's
        /// <c>Fatal error. 0xC0000005</c>, which takes all of WPR down) kills that process before
        /// any report can be written. Whoever lives on reads the file back:
        /// <c>GameLauncher.HandleGameResult</c> on Android, and the next
        /// <see cref="OnlineBackend.FlushAsync"/> anywhere (<see cref="RecoverAbandonedRun"/>).</para>
        ///
        /// <para><b>Cheap.</b> Only a new key is written (at most <see cref="MaxProblems"/> lines a
        /// run), appended and flushed to the OS, which survives the process dying. Repeats only
        /// bump the in-memory count, so a persisted count is 1.</para>
        ///
        /// <para><b>Line-based so a torn write costs one line.</b> A header of <c>name\tvalue</c>
        /// lines, then one <c>p\t...\t.</c> line per problem; a line without its closing <c>.</c>
        /// field, or a file without a product id, is ignored. Fields are escaped
        /// (<c>\\ \t \n \r</c>).</para>
        /// </remarks>
        public static string? RunFilePath { get; set; }

        private const string RunFileMagic = "wpr-compat-run 1";
        private static readonly object FileGate = new object();
        private static string? _activeRunFile;

        /// <summary>
        /// Stop persisting this run and delete its file: the run's problems have been handed to the
        /// reporter (or there was nothing to hand). Called when a run ends normally and when a crash
        /// report carrying the list is queued.
        /// </summary>
        public static void DiscardRunFile()
        {
            lock (FileGate)
            {
                string? path = _activeRunFile ?? RunFilePath;
                _activeRunFile = null;
                if (path != null) TryDelete(path);
            }
        }

        private static void StartRunFile(RunIdentity? run)
        {
            lock (FileGate)
            {
                _activeRunFile = null;
                string? path = RunFilePath;
                if (run == null || path == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    StringBuilder sb = new StringBuilder();
                    sb.Append(RunFileMagic).Append('\n');
                    Header(sb, "product", run.ProductId);
                    Header(sb, "title", run.TitleName);
                    Header(sb, "folder", run.InstallFolder);
                    Header(sb, "graphics", run.GraphicsBackend);
                    Header(sb, "started", run.StartedAt.ToString("O", CultureInfo.InvariantCulture));
                    Header(sb, "pid", run.ProcessId.ToString(CultureInfo.InvariantCulture));
                    // Overwrites a leftover from an earlier run on purpose: if nothing recovered it
                    // by now (the launcher flushes at start-up), it never will be.
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                    _activeRunFile = path;
                }
                catch (Exception)
                {
                    // Persistence is a safety net; the in-memory list still works.
                }
            }
        }

        private static void AppendToRunFile(CompatibilityProblem p)
        {
            lock (FileGate)
            {
                if (_activeRunFile is not { } path) return;
                try
                {
                    string line = string.Join("\t",
                        "p", Escape(p.Category), Escape(p.Key), Escape(p.Detail), Escape(p.Stack ?? ""),
                        p.FirstSeenSeconds.ToString("0.0", CultureInfo.InvariantCulture), Escape(p.ExceptionType ?? ""), ".") + "\n";
                    using FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] bytes = Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                }
                catch (Exception)
                {
                    // As above.
                }
            }
        }

        /// <summary>
        /// Reads and deletes the persisted run, or returns null when there is none or it is
        /// unreadable (an unreadable file is deleted too: it would never become readable).
        /// </summary>
        /// <param name="force">Take it even if the process that wrote it still looks alive. The
        /// Android launcher passes true from <c>HandleGameResult</c>, where it knows the <c>:game</c>
        /// process has gone; the start-up recovery passes false, so a game running in another
        /// process (or this one) keeps its file.</param>
        public static PersistedRun? TakeRunFile(bool force)
        {
            string? path = RunFilePath;
            if (path == null) return null;
            lock (FileGate)
            {
                if (!File.Exists(path)) return null;
                if (_activeRunFile != null && IsActive) return null; // the live run in this process

                string text;
                try { text = ReadShared(path); }
                catch (Exception) { return null; }

                PersistedRun? run = ParseRunFile(text);
                if (run != null && !force && IsProcessAlive(run.Run.ProcessId)) return null;

                // Taken: whatever happens next, it is not reported twice.
                TryDelete(path);
                return run;
            }
        }

        /// <summary>Parses a run file. Null when it has no product id (torn or not ours).</summary>
        public static PersistedRun? ParseRunFile(string text)
        {
            string[] lines = text.Split('\n');
            if (lines.Length == 0 || lines[0].TrimEnd('\r') != RunFileMagic) return null;

            PersistedRun run = new PersistedRun();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                if (f.Length == 2)
                {
                    string v = Unescape(f[1]);
                    switch (f[0])
                    {
                        case "product": run.Run.ProductId = v.Length == 0 ? null : v; break;
                        case "title": run.Run.TitleName = v.Length == 0 ? null : v; break;
                        case "folder": run.Run.InstallFolder = v.Length == 0 ? null : v; break;
                        case "graphics": run.Run.GraphicsBackend = v.Length == 0 ? null : v; break;
                        case "started":
                            if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)) run.Run.StartedAt = at;
                            break;
                        case "pid":
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) run.Run.ProcessId = pid;
                            break;
                    }
                }
                else if (f.Length == 8 && f[0] == "p" && f[7] == "." && run.Problems.Count < MaxProblems)
                {
                    run.Problems.Add(new CompatibilityProblem
                    {
                        Category = Unescape(f[1]),
                        Key = Unescape(f[2]),
                        Detail = Unescape(f[3]),
                        Stack = f[4].Length == 0 ? null : Unescape(f[4]),
                        Count = 1,
                        FirstSeenSeconds = double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 0,
                        ExceptionType = f[6].Length == 0 ? null : Unescape(f[6]),
                    });
                }
            }
            return string.IsNullOrEmpty(run.Run.ProductId) ? null : run;
        }

        /// <summary>
        /// A run whose process died without ending it (a native crash, or the process killed):
        /// queue a native-crash report carrying its problems, plus the per-problem reports. Run at
        /// every <see cref="OnlineBackend.FlushAsync"/>, which the launchers call at start-up, so a
        /// desktop WPR killed by an access violation reports on its next start. Never throws.
        /// </summary>
        public static void RecoverAbandonedRun(ICrashReporter? reporter)
        {
            try
            {
                if (reporter == null || RunFilePath == null || !File.Exists(RunFilePath)) return;
                if (TakeRunFile(force: false) is not { } run) return;
                ReportNativeCrash(reporter, run, "the run ended without WPR seeing it end (native crash, or the process was killed); recovered at the next start",
                    recovered: true);
            }
            catch (Exception)
            {
                // Observation only.
            }
        }

        /// <summary>
        /// Queues the native-crash report for a run that died (<paramref name="run"/>'s title, its
        /// problems attached) and the per-problem compatibility reports. The Android launcher calls
        /// this from <c>HandleGameResult</c> with the file it took.
        /// </summary>
        public static void ReportNativeCrash(ICrashReporter reporter, PersistedRun run, string outcome, bool recovered)
        {
            CrashReport crash = NativeCrashReport(run.Run.ProductId, run.Run.TitleName, run.Run.InstallFolder, run.Run.GraphicsBackend);
            if (recovered) crash.Extra["recovered"] = "next-start";
            AttachTo(crash, run.Problems, 0, outcome, run.Age);
            reporter.Report(crash);
            ReportProblems(reporter, run.Problems, 0, outcome, run.Age, r => FillFrom(r, run.Run), excludeKey: null);
        }

        /// <summary>The report for a game process that died with no managed exception.</summary>
        public static CrashReport NativeCrashReport(string? productId, string? titleName, string? installFolder, string? graphicsBackend) =>
            new CrashReport
            {
                Source = CrashReport.Sources.NativeCrash,
                TitleId = productId,
                TitleName = titleName,
                InstallFolder = installFolder,
                GraphicsBackend = graphicsBackend,
                // No managed exception to name. A fixed type is what lets the hub group these by
                // game and driver rather than filing each one as unique.
                ExceptionType = "NativeCrash",
                ExceptionMessage = "The game process exited without a managed exception (native crash, abort, or killed by the system).",
            };

        /// <summary>Copies a persisted run's title fields onto a report.</summary>
        public static void FillFrom(CrashReport report, RunIdentity run)
        {
            report.TitleId = run.ProductId;
            report.TitleName = run.TitleName;
            report.InstallFolder = run.InstallFolder;
            report.GraphicsBackend = run.GraphicsBackend;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            if (pid == Environment.ProcessId) return IsActive;
            try
            {
                // Android (and any Unix): no Process API needed, and none that may be unsupported.
                // A reused pid only defers recovery; the next run overwrites the file anyway.
                if (!OperatingSystem.IsWindows()) return Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture));

                using Process p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ReadShared(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception) { /* next run overwrites it */ }
        }

        private static void Header(StringBuilder sb, string name, string? value) =>
            sb.Append(name).Append('\t').Append(Escape(value ?? "")).Append('\n');

        private static string Escape(string s)
        {
            if (s.IndexOfAny(new[] { '\\', '\t', '\n', '\r' }) < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                sb.Append(n switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => n });
            }
            return sb.ToString();
        }
    }
}
