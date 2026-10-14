using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WPR.Engine.Online
{
    public static partial class CompatibilityProblems
    {
        /// <summary>At most this many per-problem reports leave one run; the rest are summarised in the last.</summary>
        public const int MaxReportsPerRun = 10;

        /// <summary>The synthetic frame every per-problem report's stack starts with.</summary>
        public const string FramePrefix = "at WPR.Compatibility.";

        /// <summary>
        /// One crash-kind report per distinct problem, so the hub fingerprints each one and groups
        /// the same problem across every title that meets it.
        /// </summary>
        /// <remarks>
        /// <para><b>Shaped for the hub's fingerprint</b> (<c>CrashFingerprinter</c>: the exception
        /// type plus the top five <c>at ...</c> frames). The type is the real exception's
        /// (<c>System.TypeLoadException</c>) or <c>WPR.Compatibility.&lt;category&gt;</c> for a
        /// problem WPR noticed itself, and the stack has exactly ONE <c>at</c> line, a synthetic
        /// frame naming the problem: <c>at WPR.Compatibility.missing-type: System.Windows.Controls.RichTextBox [System.Windows]</c>.
        /// Where it was first seen follows as <c>from ...</c> lines, which the fingerprinter does not
        /// read: kept as <c>at</c> frames they would split "RichTextBox is missing" into one group
        /// per game, which is exactly what this exists to avoid.</para>
        ///
        /// <para><paramref name="excludeKey"/> is the crash's own problem (<see cref="KeyOf"/>), when
        /// the run crashed: the crash report already counts it, and a second report for it would
        /// count one failure twice.</para>
        /// </remarks>
        public static List<CrashReport> BuildProblemReports(IReadOnlyList<CompatibilityProblem> problems, int dropped,
            string outcome, TimeSpan ran, string? excludeKey = null)
        {
            List<CrashReport> reports = new List<CrashReport>(problems.Count);
            foreach (CompatibilityProblem p in problems)
            {
                if (excludeKey != null && KeyOf(p) == excludeKey) continue;

                string key = OneLine(p.Key);
                CrashReport r = new CrashReport
                {
                    Source = CrashReport.Sources.Compatibility,
                    ExceptionType = string.IsNullOrEmpty(p.ExceptionType) ? "WPR.Compatibility." + p.Category : p.ExceptionType,
                    ExceptionMessage = p.Category + ": " + key + (p.Detail != p.Key && !string.IsNullOrEmpty(p.Detail) ? "\n" + p.Detail : ""),
                    StackTrace = ProblemStack(p, key),
                };
                r.Extra["problem_category"] = p.Category;
                r.Extra["problem_key"] = Truncate(key, 500)!;
                r.Extra["problem_count"] = p.Count.ToString(CultureInfo.InvariantCulture);
                r.Extra["first_seen_s"] = p.FirstSeenSeconds.ToString("0.0", CultureInfo.InvariantCulture);
                r.Extra["outcome"] = outcome;
                r.Extra["run_seconds"] = ((int)ran.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                r.Extra["run_problems"] = (problems.Count + dropped).ToString(CultureInfo.InvariantCulture);
                reports.Add(r);
            }
            return reports;
        }

        /// <summary>
        /// Builds the per-problem reports, fills their title with <paramref name="fill"/> and hands
        /// them to the reporter, which drops the ones already sent and caps the rest
        /// (<see cref="ICrashReporter.ReportCompatibility"/>). Never throws.
        /// </summary>
        public static int ReportProblems(ICrashReporter reporter, IReadOnlyList<CompatibilityProblem> problems, int dropped,
            string outcome, TimeSpan ran, Action<CrashReport> fill, string? excludeKey)
        {
            try
            {
                List<CrashReport> reports = BuildProblemReports(problems, dropped, outcome, ran, excludeKey);
                if (reports.Count == 0) return 0;
                foreach (CrashReport r in reports) fill(r);
                return reporter.ReportCompatibility(reports);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>The identity a problem is recorded under: <c>category|key</c>.</summary>
        public static string KeyOf(CompatibilityProblem p) => p.Category + "|" + p.Key;

        /// <summary>
        /// The key <paramref name="ex"/> would be recorded under by the first-chance observer, or
        /// null when it is not a compatibility problem. For excluding a crash's own exception from
        /// the per-problem reports of the run it ended.
        /// </summary>
        public static string? KeyOf(Exception ex)
        {
            // The same unwrapping CrashReport.FromException does, so the key is the crash's root.
            while (ex.InnerException != null
                   && (ex is AggregateException { InnerExceptions.Count: 1 } or System.Reflection.TargetInvocationException or TypeInitializationException))
            {
                ex = ex.InnerException;
            }
            return Classify(ex) is { } c ? c.Category + "|" + Truncate(c.Key, MaxDetailChars) : null;
        }

        /// <summary>
        /// Picks what to send from one run's candidates: the ones <paramref name="alreadySent"/>
        /// does not know, at most <paramref name="cap"/>, and when more were left the last one
        /// sent carries a summary of them (<c>more_problems</c> plus one <c>mNN</c> line each, up
        /// to twenty), so nothing a run met goes unmentioned.
        /// </summary>
        public static List<CrashReport> SelectForSending(IReadOnlyList<CrashReport> candidates, Func<CrashReport, bool> alreadySent,
            int cap = MaxReportsPerRun)
        {
            List<CrashReport> fresh = candidates.Where(r => !alreadySent(r)).ToList();
            if (fresh.Count <= cap) return fresh;

            List<CrashReport> sent = fresh.Take(cap).ToList();
            List<CrashReport> rest = fresh.Skip(cap).ToList();
            CrashReport last = sent[sent.Count - 1];
            last.Extra["more_problems"] = rest.Count.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < rest.Count && i < 20 && last.Extra.Count < 44; i++)
            {
                rest[i].Extra.TryGetValue("problem_category", out string? cat);
                rest[i].Extra.TryGetValue("problem_key", out string? key);
                rest[i].Extra.TryGetValue("problem_count", out string? count);
                last.Extra[$"m{i + 1:00}"] = Truncate($"[{cat}] x{count} {key}", 300)!;
            }
            return sent;
        }

        /// <summary>
        /// What makes a per-problem report "the same" for local deduplication: the title, the
        /// problem and the WPR version. A short SHA-256 so the sent log stays small and holds no
        /// names. Null for a report that is not a per-problem compatibility report.
        /// </summary>
        public static string? SentId(CrashReport report, string wprVersion)
        {
            if (report.Source != CrashReport.Sources.Compatibility
                || !report.Extra.TryGetValue("problem_category", out string? cat)
                || !report.Extra.TryGetValue("problem_key", out string? key)) return null;
            string material = (report.TitleId ?? "").Trim('{', '}').ToUpperInvariant() + "\n" + cat + "\n" + key + "\n" + wprVersion;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(hash, 0, 10);
        }

        private static string ProblemStack(CompatibilityProblem p, string key)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(FramePrefix).Append(p.Category).Append(": ").Append(key).Append('\n');
            sb.Append("--- first seen ").Append(p.FirstSeenSeconds.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" s into the run, x").Append(p.Count).Append(" (not part of the fingerprint) ---\n");
            AppendFromLines(sb, p.Stack);
            return sb.ToString();
        }

        /// <summary>
        /// A stack as <c>    from X</c> lines. The hub's fingerprinter reads every line starting
        /// <c>at </c> as a frame, so a stack that only says where a problem was first seen must
        /// not use that word: in a per-problem report it would split one problem into a group per
        /// game, and appended to a crash report it would become that crash's fingerprint.
        /// </summary>
        private static void AppendFromLines(StringBuilder sb, string? stack)
        {
            if (string.IsNullOrEmpty(stack)) return;
            foreach (string raw in stack!.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("at ", StringComparison.Ordinal)) line = line.Substring(3);
                sb.Append("    from ").Append(line).Append('\n');
            }
        }

        private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>
    /// The per-problem reports already sent from this install, so the same (title, problem, WPR
    /// version) is not sent again every run. A small line file of <see cref="CompatibilityProblems.SentId"/>
    /// values, newest last, capped at <see cref="Capacity"/> lines (the oldest are forgotten, which
    /// only means one may be sent again). Unreadable means empty.
    /// </summary>
    public sealed class CompatibilitySentLog
    {
        public const int Capacity = 2000;

        private readonly string _path;
        private readonly object _gate = new object();

        public CompatibilitySentLog(string path) => _path = path;

        public bool Contains(string id)
        {
            lock (_gate) return Read().Contains(id);
        }

        public void Add(string id)
        {
            lock (_gate)
            {
                try
                {
                    List<string> lines = ReadLines();
                    if (lines.Contains(id)) return;
                    lines.Add(id);
                    if (lines.Count > Capacity) lines.RemoveRange(0, lines.Count - Capacity);
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    string tmp = _path + ".tmp";
                    File.WriteAllLines(tmp, lines);
                    File.Move(tmp, _path, overwrite: true);
                }
                catch (Exception)
                {
                    // Losing the record only means sending one again.
                }
            }
        }

        private HashSet<string> Read() => new HashSet<string>(ReadLines(), StringComparer.Ordinal);

        private List<string> ReadLines()
        {
            try
            {
                if (!File.Exists(_path)) return new List<string>();
                return File.ReadAllLines(_path).Select(l => l.Trim()).Where(l => l.Length == 20 && l.All(Uri.IsHexDigit)).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }
    }
}
