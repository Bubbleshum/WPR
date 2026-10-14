using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace WPR.Engine.Online
{
    /// <summary>One distinct problem a game run met, with how often it happened.</summary>
    public sealed class CompatibilityProblem
    {
        /// <summary>One of <see cref="CompatibilityProblems.Categories"/>.</summary>
        public string Category { get; set; } = "";

        /// <summary>What makes it distinct: the missing type's name, the member, the throwing method.</summary>
        public string Key { get; set; } = "";

        /// <summary>The first occurrence's message.</summary>
        public string Detail { get; set; } = "";

        /// <summary>Where the first occurrence happened. Null when nobody could say.</summary>
        public string? Stack { get; set; }

        public int Count { get; set; }

        /// <summary>Seconds into the run of the first occurrence.</summary>
        public double FirstSeenSeconds { get; set; }

        /// <summary>One line: <c>[missing-type] x3 @1.2s Could not load type ...</c>.</summary>
        public string Summary => $"[{Category}] x{Count} @{FirstSeenSeconds:0.0}s {Detail}";
    }

    /// <summary>
    /// Collects the problems a game run carries on past, for one compatibility report to WPR Hub
    /// at the end of the run.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists.</b> The crash reports only see what escapes a run. WP7 titles
    /// routinely catch a <see cref="TypeLoadException"/> for a type WPR never defined and go on
    /// half-broken, and the visible symptom is somewhere else entirely - the Fast and the Furious
    /// case in CLAUDE.md, where one missing <c>PlaylistCollection</c> sat under 7,874 identical
    /// NREs from <c>Draw</c>. Nothing of that reached the hub. This records the root (the first
    /// <c>TypeLoadException</c>) and the symptom (Draw threw x7874) as two distinct lines.</para>
    ///
    /// <para><b>Cheap by construction.</b> The first-chance observer is installed in Release, so
    /// it does the least work possible for the exceptions games throw constantly: a null check
    /// when no game runs, then a type test, and it returns. Only an allow-listed type
    /// (<see cref="Classify"/>) is looked at further, and only a key never seen in this run pays
    /// for a stack walk. It is not the verbose <c>[wpr-fce]</c> logger, which stays Debug and
    /// logging-run only.</para>
    ///
    /// <para><b>Bounded.</b> At most <see cref="MaxProblems"/> distinct problems per run; repeats
    /// only bump a counter and past the cap only a "dropped" count grows. Every string is
    /// truncated. A game throwing 60 times a second for an hour costs one dictionary hit each.</para>
    ///
    /// <para>Process-wide static: one game runs at a time per process on both heads. Begun and
    /// ended by <c>ApplicationLaunch.Start</c>; while not begun, everything here is a no-op.</para>
    /// </remarks>
    public static class CompatibilityProblems
    {
        public const int MaxProblems = 40;
        private const int MaxDetailChars = 500;
        private const int MaxStackFrames = 16;

        /// <summary>The categories a problem can have. Sent to the hub verbatim.</summary>
        public static class Categories
        {
            public const string MissingType = "missing-type";
            public const string MissingMember = "missing-member";
            public const string MissingAssembly = "missing-assembly";
            public const string MissingNative = "missing-native";
            public const string NotImplemented = "not-implemented";
            public const string BadIl = "bad-il";
            public const string UpdateThrew = "update-threw";
            public const string DrawThrew = "draw-threw";
            public const string UiRender = "uirender";
        }

        private static readonly object Gate = new object();
        private static Dictionary<string, CompatibilityProblem>? _problems;
        private static List<CompatibilityProblem>? _order;
        private static int _dropped;
        private static readonly Stopwatch Clock = new Stopwatch();
        private static int _observerInstalled;

        [ThreadStatic] private static bool _inObserver;

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        public static bool IsActive => Volatile.Read(ref _problems) != null;

        /// <summary>Distinct problems that did not fit under <see cref="MaxProblems"/>.</summary>
        public static int Dropped { get { lock (Gate) return _dropped; } }

        /// <summary>Start collecting for a new run, forgetting the last one.</summary>
        public static void Begin()
        {
            lock (Gate)
            {
                _order = new List<CompatibilityProblem>();
                _dropped = 0;
                Clock.Restart();
                Volatile.Write(ref _problems, new Dictionary<string, CompatibilityProblem>(StringComparer.Ordinal));
            }
        }

        /// <summary>Stop collecting and hand back what the run met, in order of first sighting.</summary>
        public static IReadOnlyList<CompatibilityProblem> End()
        {
            lock (Gate)
            {
                List<CompatibilityProblem> result = _order ?? new List<CompatibilityProblem>();
                Volatile.Write(ref _problems, null);
                _order = null;
                Clock.Stop();
                return result;
            }
        }

        /// <summary>What has been collected so far, without stopping. A copy.</summary>
        public static IReadOnlyList<CompatibilityProblem> Snapshot()
        {
            lock (Gate)
            {
                if (_order == null) return Array.Empty<CompatibilityProblem>();
                List<CompatibilityProblem> copy = new List<CompatibilityProblem>(_order.Count);
                foreach (CompatibilityProblem p in _order)
                {
                    copy.Add(new CompatibilityProblem
                    {
                        Category = p.Category, Key = p.Key, Detail = p.Detail, Stack = p.Stack,
                        Count = p.Count, FirstSeenSeconds = p.FirstSeenSeconds,
                    });
                }
                return copy;
            }
        }

        /// <summary>
        /// Hooks <see cref="AppDomain.FirstChanceException"/> once per process. Safe to call on
        /// every launch.
        /// </summary>
        public static void InstallFirstChanceObserver()
        {
            if (Interlocked.Exchange(ref _observerInstalled, 1) != 0) return;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        }

        private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            // The two checks every exception in the process pays for.
            if (Volatile.Read(ref _problems) == null || _inObserver) return;
            _inObserver = true;
            try
            {
                Observe(e.Exception, captureStack: true);
            }
            catch
            {
                // Never let observation mask the original throw.
            }
            finally
            {
                _inObserver = false;
            }
        }

        /// <summary>
        /// Records <paramref name="ex"/> if it is a compatibility problem (see <see cref="Classify"/>).
        /// Public so tests can drive it without throwing.
        /// </summary>
        public static void Observe(Exception ex, bool captureStack = false)
        {
            if (ex is ReflectionTypeLoadException rtle)
            {
                // GetTypes() over an assembly naming something WPR does not define: the useful
                // part is the loader exceptions, one per missing type.
                int seen = 0;
                foreach (Exception? inner in rtle.LoaderExceptions)
                {
                    if (inner == null || ++seen > 10) break;
                    Observe(inner, captureStack);
                }
                return;
            }

            if (Classify(ex) is not { } c) return;
            Record(c.Category, c.Key, c.Detail, captureStack ? CaptureStack : null);
        }

        /// <summary>
        /// The allow-list. Null for anything that is not a compatibility problem, which is almost
        /// every exception a game throws - including the known noise: dead online services,
        /// probes for saves that do not exist yet, and the runtime's own satellite-assembly and
        /// serializer-assembly probes.
        /// </summary>
        public static (string Category, string Key, string Detail)? Classify(Exception ex)
        {
            switch (ex)
            {
                case TypeLoadException tle when ex is not EntryPointNotFoundException and not DllNotFoundException:
                    return (Categories.MissingType, tle.TypeName is { Length: > 0 } name && !tle.Message.Contains(name, StringComparison.Ordinal)
                        ? name + " | " + tle.Message : tle.Message, tle.Message);
                case MissingMemberException mme: // MissingMethodException and MissingFieldException derive from it
                    return (Categories.MissingMember, mme.Message, mme.Message);
                case EntryPointNotFoundException or DllNotFoundException:
                    return (Categories.MissingNative, ex.Message, ex.Message);
                case FileNotFoundException fnf:
                    return IsAssemblyLoad(fnf.Message, fnf.FileName) && !IsNoiseAssembly(fnf.FileName ?? fnf.Message)
                        ? (Categories.MissingAssembly, fnf.FileName ?? fnf.Message, fnf.Message)
                        : null;
                case FileLoadException fle:
                    return IsNoiseAssembly(fle.FileName ?? fle.Message)
                        ? null
                        : (Categories.MissingAssembly, fle.FileName ?? fle.Message, fle.Message);
                case BadImageFormatException or InvalidProgramException:
                    return (Categories.BadIl, ex.GetType().Name + ": " + Thrower(ex) + " " + ex.Message, ex.GetType().Name + " in " + Thrower(ex) + ": " + ex.Message);
                case NotImplementedException or PlatformNotSupportedException:
                    string where = Thrower(ex);
                    return (Categories.NotImplemented, ex.GetType().Name + " @ " + where, ex.GetType().Name + " in " + where + ": " + ex.Message);
                default:
                    return null;
            }
        }

        /// <summary>
        /// A <see cref="FileNotFoundException"/> is an assembly the game references and nobody
        /// provides only when it says so; the far more common one is a game probing for its own
        /// save file, which is not a problem at all.
        /// </summary>
        private static bool IsAssemblyLoad(string message, string? fileName) =>
            message.StartsWith("Could not load file or assembly", StringComparison.Ordinal)
            || (fileName != null && fileName.Contains(", Version=", StringComparison.Ordinal));

        /// <summary>
        /// The runtime probes for these on its own and always expects to miss: satellite resource
        /// assemblies for the current culture, and pre-generated XmlSerializer assemblies.
        /// </summary>
        public static bool IsNoiseAssembly(string nameOrMessage) =>
            nameOrMessage.Contains(".resources", StringComparison.OrdinalIgnoreCase)
            || nameOrMessage.Contains(".XmlSerializers", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// An exception thrown out of a game's Update or Draw (or a mixed-mode page's GameTimer).
        /// Keyed by exception type and the method that threw it, so a frame-rate stream of the same
        /// NRE is one line with a large count, and its message and stack are kept from the first.
        /// </summary>
        public static void NoteFrameException(string category, Exception ex)
        {
            if (Volatile.Read(ref _problems) == null) return;
            try
            {
                string where = Thrower(ex);
                Record(category, ex.GetType().FullName + " @ " + where,
                    ex.GetType().FullName + " in " + where + ": " + ex.Message,
                    () => Truncate(ex.StackTrace, 2000));
            }
            catch
            {
                // Observation only.
            }
        }

        /// <summary>A problem WPR noticed itself (an unsupported Silverlight feature, ...).</summary>
        public static void Note(string category, string key, string detail)
        {
            if (Volatile.Read(ref _problems) == null) return;
            try { Record(category, key, detail, null); }
            catch { /* observation only */ }
        }

        private static void Record(string category, string key, string detail, Func<string?>? stack)
        {
            string full = category + "|" + key;
            lock (Gate)
            {
                if (_problems == null) return;
                if (_problems.TryGetValue(full, out CompatibilityProblem? known)) { known.Count++; return; }
                if (_problems.Count >= MaxProblems) { _dropped++; return; }
            }

            // New: worth a stack, taken outside the lock.
            string? trace = null;
            try { trace = stack?.Invoke(); } catch { /* none */ }

            lock (Gate)
            {
                if (_problems == null) return;
                if (_problems.TryGetValue(full, out CompatibilityProblem? raced)) { raced.Count++; return; }
                if (_problems.Count >= MaxProblems) { _dropped++; return; }
                CompatibilityProblem p = new CompatibilityProblem
                {
                    Category = category,
                    Key = Truncate(key, MaxDetailChars)!,
                    Detail = Truncate(detail, MaxDetailChars)!,
                    Stack = trace,
                    Count = 1,
                    FirstSeenSeconds = Clock.Elapsed.TotalSeconds,
                };
                _problems.Add(full, p);
                _order!.Add(p);
            }
        }

        /// <summary>
        /// The calling thread's stack, from inside a first-chance handler, which still has the
        /// whole call chain on it (the exception's own trace has at most the throwing frame yet).
        /// </summary>
        private static string? CaptureStack()
        {
            StackFrame[] frames = new StackTrace(1, false).GetFrames();
            StringBuilder sb = new StringBuilder();
            int written = 0;
            foreach (StackFrame frame in frames)
            {
                MethodBase? m = frame.GetMethod();
                Type? t = m?.DeclaringType;
                if (m == null) continue;
                if (t == typeof(CompatibilityProblems)) continue;
                if (t?.Namespace == "System.Runtime.ExceptionServices" || t?.FullName == "System.AppDomain") continue;
                sb.Append("   at ").Append(t?.FullName).Append('.').Append(m.Name).Append('\n');
                if (++written >= MaxStackFrames) break;
            }
            return written == 0 ? null : sb.ToString();
        }

        /// <summary>The method that threw, as far as the exception knows by now.</summary>
        private static string Thrower(Exception ex)
        {
            try
            {
                MethodBase? site = ex.TargetSite;
                if (site != null) return (site.DeclaringType?.FullName ?? "?") + "." + site.Name;
            }
            catch
            {
                // TargetSite can throw for dynamic methods; fall through.
            }
            return "?";
        }

        // ------------------------------------------------------------------------------------
        // The report
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// The run's compatibility report, or null when it met nothing. One per run; the caller
        /// fills in the title fields.
        /// </summary>
        public static CrashReport? BuildReport(IReadOnlyList<CompatibilityProblem> problems, int dropped, string outcome, TimeSpan ran)
        {
            if (problems.Count == 0) return null;

            CrashReport report = new CrashReport
            {
                Source = CrashReport.Sources.Compatibility,
                ExceptionType = "WPR.CompatibilityProblems",
                ExceptionMessage = Headline(problems, dropped),
            };
            AttachTo(report, problems, dropped, outcome, ran);
            report.StackTrace = Describe(problems, dropped, outcome, ran);
            return report;
        }

        /// <summary>
        /// Adds the problems to a report that is about something else (a crash), so a run that
        /// crashes still sends exactly one report and the hub sees the problems that led up to it.
        /// </summary>
        public static void AttachTo(CrashReport report, IReadOnlyList<CompatibilityProblem> problems, int dropped, string outcome, TimeSpan ran)
        {
            if (problems.Count == 0) return;
            report.Extra["problem_count"] = problems.Count.ToString();
            if (dropped > 0) report.Extra["problems_dropped"] = dropped.ToString();
            report.Extra["outcome"] = outcome;
            report.Extra["run_seconds"] = ((int)ran.TotalSeconds).ToString();
            for (int i = 0; i < problems.Count && i < MaxProblems; i++)
            {
                report.Extra[$"p{i + 1:00}"] = Truncate(problems[i].Summary, 300)!;
            }
            if (report.Source != CrashReport.Sources.Compatibility)
            {
                // A crash report's stack is the crash's; append the problems after it.
                report.StackTrace = (report.StackTrace ?? "") + "\n\n" + Describe(problems, dropped, outcome, ran);
            }
        }

        /// <summary>A one-line title: the count and the first few problems.</summary>
        public static string Headline(IReadOnlyList<CompatibilityProblem> problems, int dropped)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(problems.Count + dropped).Append(" compatibility problem(s): ");
            for (int i = 0; i < problems.Count && i < 3; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(problems[i].Category).Append(' ').Append(Truncate(problems[i].Key, 160));
                if (problems[i].Count > 1) sb.Append(" x").Append(problems[i].Count);
            }
            if (problems.Count > 3) sb.Append("; ...");
            return sb.ToString();
        }

        /// <summary>The structured body: one block per problem, its message and first stack.</summary>
        public static string Describe(IReadOnlyList<CompatibilityProblem> problems, int dropped, string outcome, TimeSpan ran)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("WPR compatibility problems: ").Append(problems.Count).Append(" distinct")
              .Append(dropped > 0 ? $" (+{dropped} more not kept)" : "")
              .Append(", run ").Append((int)ran.TotalSeconds).Append(" s, ").Append(outcome).Append('\n');
            foreach (CompatibilityProblem p in problems)
            {
                sb.Append('\n').Append(p.Summary).Append('\n');
                if (!string.IsNullOrEmpty(p.Stack)) sb.Append(p.Stack.TrimEnd()).Append('\n');
            }
            return sb.ToString();
        }

        private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
