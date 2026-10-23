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

        /// <summary>The process the run is in. Only consulted for a file without a run lock
        /// (written before the lock existed); see <see cref="HoldsLock"/>.</summary>
        public int ProcessId { get; set; } = Environment.ProcessId;

        /// <summary>
        /// The writer held <see cref="CompatibilityProblems.RunLockPath"/> for the run, so the run
        /// is alive exactly while that lock is held. Read back from the file's <c>lock</c> header.
        /// </summary>
        public bool HoldsLock { get; set; }
    }

    /// <summary>The platform's record of why a process ended (<see cref="CompatibilityProblems.ProcessExitLookup"/>).</summary>
    public sealed class ProcessExit
    {
        /// <summary>A short stable name: <c>crash-native</c>, <c>package-updated</c>, <c>user-requested</c>...</summary>
        public string Reason { get; init; } = "unknown";

        /// <summary>The platform's own words, when it gave any.</summary>
        public string? Description { get; init; }

        /// <summary>
        /// False when the platform ended the process on purpose (an app update, a force stop, a
        /// permission change), which is never the game's fault. Everything else, low-memory kills
        /// included, still counts.
        /// </summary>
        public bool IsCrash { get; init; } = true;
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
        /// The run lock: <see cref="RunFilePath"/> + <c>.lock</c>, held open with
        /// <see cref="FileShare.None"/> by the process whose run the file is, from
        /// <see cref="Begin(RunIdentity)"/> until the file is discarded.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a lock as well as the pid.</b> A pid only says some process of that number
        /// exists; the lock says this run's process is still running it. The kernel releases it
        /// when its holder dies, however it dies, and only then. It is purely additive (see
        /// <see cref="IsRunLive"/>): it keeps a live run's file from the launcher's forced take,
        /// which could otherwise take a different game's file, and from the window between
        /// <see cref="End"/> and <see cref="DiscardRunFile"/>. .NET takes it with
        /// <c>flock(LOCK_EX)</c> on Unix and a share-mode open on Windows, both of which conflict
        /// between processes and between two opens in one process. See the CLAUDE.md note "A game
        /// Android killed on purpose is not a native crash" (2026-10-23).</para>
        ///
        /// <para>The lock file is never deleted: unlinking a lock file another process may be
        /// about to open is the classic way to have two holders.</para>
        /// </remarks>
        public static string? RunLockPath => RunFilePath == null ? null : RunFilePath + ".lock";

        private static FileStream? _runLock;

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
                // After the delete: while the file exists, the lock must say the run is live.
                ReleaseRunLock();
            }
        }

        /// <summary>Lets go of the run lock without touching the file: what a process dying does.</summary>
        internal static void ReleaseRunLock()
        {
            lock (FileGate)
            {
                try { _runLock?.Dispose(); } catch (Exception) { /* closing cannot usefully fail */ }
                _runLock = null;
            }
        }

        private static FileStream? AcquireRunLock(string lockPath)
        {
            try
            {
                FileStream fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    // For a person reading the folder; nothing parses it.
                    byte[] who = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
                    fs.SetLength(0);
                    fs.Write(who, 0, who.Length);
                    fs.Flush();
                }
                catch (Exception)
                {
                    // The lock is what matters.
                }
                return fs;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void StartRunFile(RunIdentity? run)
        {
            lock (FileGate)
            {
                _activeRunFile = null;
                ReleaseRunLock();
                string? path = RunFilePath;
                if (run == null || path == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Taken BEFORE the file is written, so no reader ever sees this run's file
                    // without its lock held. If it cannot be taken (the file system refuses), the
                    // file has no lock line and readers fall back to the pid, as before.
                    _runLock = AcquireRunLock(RunLockPath!);
                    StringBuilder sb = new StringBuilder();
                    sb.Append(RunFileMagic).Append('\n');
                    Header(sb, "product", run.ProductId);
                    Header(sb, "title", run.TitleName);
                    Header(sb, "folder", run.InstallFolder);
                    Header(sb, "graphics", run.GraphicsBackend);
                    Header(sb, "started", run.StartedAt.ToString("O", CultureInfo.InvariantCulture));
                    Header(sb, "pid", run.ProcessId.ToString(CultureInfo.InvariantCulture));
                    if (_runLock != null) Header(sb, "lock", "1");
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
        /// <param name="force">For a file without a run lock (written by a WPR older than the
        /// lock), take it even if its pid still looks alive. The Android launcher passes true from
        /// <c>HandleGameResult</c>, where it knows the <c>:game</c> process it started has gone. A
        /// file whose lock is held is never taken, force or not: some live run owns it, and on
        /// Android that can be a different game started since.</param>
        /// <param name="lockWait">How long to wait for a held lock to be let go. The launcher
        /// hears that <c>:game</c> died when its binder closes, which can be a moment before the
        /// kernel has closed the rest of its files, the lock among them.</param>
        public static PersistedRun? TakeRunFile(bool force, TimeSpan lockWait = default)
        {
            string? path = RunFilePath;
            if (path == null) return null;
            DateTime giveUp = DateTime.UtcNow + lockWait;
            while (true)
            {
                lock (FileGate)
                {
                    if (!File.Exists(path)) return null;
                    if (_activeRunFile != null && IsActive) return null; // the live run in this process

                    string text;
                    try { text = ReadShared(path); }
                    catch (Exception) { return null; }

                    PersistedRun? run = ParseRunFile(text);
                    if (run == null)
                    {
                        // Unreadable: it would never become readable.
                        TryDelete(path);
                        return null;
                    }

                    FileStream? probe = null;
                    try
                    {
                        if (!IsRunLive(run, force, out probe))
                        {
                            // Deleted while the lock is ours, so a run starting this instant
                            // cannot have its new file deleted from under it.
                            TryDelete(path);
                            return run;
                        }
                        if (force)
                        {
                            // Only the lock keeps a forced take back: some other live run (on
                            // Android, a game started since the one that died) owns this file.
                            Trace.WriteLine($"[wpr-compat] run file of {run.Run.TitleName ?? run.Run.ProductId} (pid {run.Run.ProcessId}) is locked by a live run; left alone.");
                        }
                    }
                    finally
                    {
                        probe?.Dispose();
                    }
                }
                if (DateTime.UtcNow >= giveUp) return null;
                System.Threading.Thread.Sleep(50);
            }
        }

        /// <summary>
        /// Whether the run that wrote <paramref name="run"/> is still going: its run lock is held,
        /// or (unless <paramref name="force"/>) its pid is alive. The lock only ever ADDS "alive":
        /// a file system whose <c>flock</c> silently does nothing must not make a live run look
        /// dead, so the pid check stands exactly as it did before the lock. When the lock is free,
        /// <paramref name="probe"/> holds it for the caller until the file is deleted.
        /// </summary>
        private static bool IsRunLive(PersistedRun run, bool force, out FileStream? probe)
        {
            probe = null;
            string? lockPath = RunLockPath;
            if (run.Run.HoldsLock && lockPath != null)
            {
                try
                {
                    probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (FileNotFoundException) { }     // nothing can be holding it
                catch (DirectoryNotFoundException) { }
                catch (IOException) { return true; }  // held: the run is live
                catch (Exception) { }                 // not allowed to open it: the pid must answer
            }
            if (force) return false;
            return IsProcessAlive(run.Run.ProcessId);
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
                        case "lock": run.Run.HoldsLock = v == "1"; break;
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
        /// <returns>False when the platform says the process was ended on purpose (see
        /// <see cref="ProcessExitLookup"/>): then only the per-problem reports are queued.</returns>
        public static bool ReportNativeCrash(ICrashReporter reporter, PersistedRun run, string outcome, bool recovered)
        {
            ProcessExit? exit = ExitOf(run.Run.ProcessId, run.Run.StartedAt);
            if (exit is { IsCrash: false })
            {
                string why = "ended by the system (" + exit.Reason + ")";
                Trace.WriteLine($"[wpr-compat] {run.Run.TitleName ?? run.Run.ProductId} (pid {run.Run.ProcessId}) was {why}"
                    + (exit.Description is { } d ? ": " + d : "") + "; not a crash, no native-crash report.");
                ReportProblems(reporter, run.Problems, 0, why, run.Age, r => FillFrom(r, run.Run), excludeKey: null);
                return false;
            }

            CrashReport crash = NativeCrashReport(run.Run.ProductId, run.Run.TitleName, run.Run.InstallFolder, run.Run.GraphicsBackend);
            if (recovered) crash.Extra["recovered"] = "next-start";
            AttachExit(crash, exit);
            AttachTo(crash, run.Problems, 0, outcome, run.Age);
            reporter.Report(crash);
            ReportProblems(reporter, run.Problems, 0, outcome, run.Age, r => FillFrom(r, run.Run), excludeKey: null);
            return true;
        }

        /// <summary>
        /// Why a process of this app ended, from the platform's own record: Android's
        /// <c>ApplicationExitInfo</c> (API 30+), filled by the Android head. Arguments: the pid,
        /// or 0 for the most recent game process; and the earliest time the death may have been
        /// recorded (a reused pid's older record must not answer). Null: the platform keeps no
        /// record, or none matched, and the death is treated as a crash, as before.
        /// </summary>
        /// <remarks>
        /// <para><b>Why.</b> A run file left behind only says the process died without ending the
        /// run. On a phone that is very often not a crash: installing a new WPR build kills every
        /// WPR process (<c>stop com.wpr.android due to installPackageLI</c>), and so does a force
        /// stop from Settings or <c>am force-stop</c>. All of those used to arrive at WPR Hub as
        /// <c>NativeCrash</c> (WPR-44SQYC8G, Angry Birds Space, was an APK install).</para>
        /// </remarks>
        public static Func<int, DateTimeOffset, ProcessExit?>? ProcessExitLookup { get; set; }

        /// <summary><see cref="ProcessExitLookup"/>, never throwing.</summary>
        public static ProcessExit? ExitOf(int pid, DateTimeOffset after)
        {
            try { return ProcessExitLookup?.Invoke(pid, after); }
            catch (Exception) { return null; }
        }

        /// <summary>Records the platform's exit reason on a native-crash report, when there is one.</summary>
        public static void AttachExit(CrashReport crash, ProcessExit? exit)
        {
            if (exit == null) return;
            crash.Extra["exit_reason"] = exit.Reason;
            if (exit.Description is { Length: > 0 } d) crash.Extra["exit_description"] = Truncate(d, 300)!;
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
