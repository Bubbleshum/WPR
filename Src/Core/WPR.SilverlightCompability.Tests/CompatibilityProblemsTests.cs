using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using WPR.Engine.Online;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// The end-of-run compatibility report's collector: what it records, what it ignores, and that
/// it stays bounded. One class, so the process-wide static is never shared between parallel tests.
/// </summary>
[CollectionDefinition(nameof(CompatibilityProblemsTests), DisableParallelization = true)]
public class CompatibilityProblemsCollection { }

[Collection(nameof(CompatibilityProblemsTests))]
public class CompatibilityProblemsTests
{
    private static TypeLoadException MissingType(string type) =>
        new TypeLoadException($"Could not load type '{type}' from assembly 'FNA, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.");

    [Fact]
    public void Nothing_is_recorded_outside_a_run()
    {
        CompatibilityProblems.End();
        CompatibilityProblems.Observe(MissingType("A"));
        CompatibilityProblems.Note(CompatibilityProblems.Categories.UiRender, "Popup", "Popup: not drawn");
        CompatibilityProblems.Begin();
        Assert.Empty(CompatibilityProblems.End());
    }

    [Fact]
    public void The_same_problem_is_one_line_with_a_count()
    {
        CompatibilityProblems.Begin();
        for (int i = 0; i < 5; i++) CompatibilityProblems.Observe(MissingType("Microsoft.Xna.Framework.Media.PlaylistCollection"));
        CompatibilityProblems.Observe(MissingType("Microsoft.Xna.Framework.Media.Playlist"));
        var problems = CompatibilityProblems.End();

        Assert.Equal(2, problems.Count);
        Assert.Equal(CompatibilityProblems.Categories.MissingType, problems[0].Category);
        Assert.Equal(5, problems[0].Count);
        Assert.Contains("PlaylistCollection", problems[0].Detail);
        // Keyed without the version, which the hub's scrubber would read as an IP address.
        Assert.Equal("Microsoft.Xna.Framework.Media.PlaylistCollection [FNA]", problems[0].Key);
        Assert.Equal(1, problems[1].Count);
    }

    [Fact]
    public void Distinct_problems_are_capped_and_the_rest_counted()
    {
        CompatibilityProblems.Begin();
        for (int i = 0; i < CompatibilityProblems.MaxProblems + 10; i++)
        {
            CompatibilityProblems.Observe(new MissingMethodException($"Method not found: 'Void Game.M{i}()'."));
        }
        int dropped = CompatibilityProblems.Dropped;
        var problems = CompatibilityProblems.End();

        Assert.Equal(CompatibilityProblems.MaxProblems, problems.Count);
        Assert.Equal(10, dropped);
    }

    [Fact]
    public void Known_noise_is_ignored()
    {
        CompatibilityProblems.Begin();
        // Dead online services.
        CompatibilityProblems.Observe(new HttpRequestException("No such host is known."));
        CompatibilityProblems.Observe(new SocketException(11001));
        CompatibilityProblems.Observe(new System.Net.WebException("The remote name could not be resolved"));
        // A game probing for a save that does not exist yet.
        CompatibilityProblems.Observe(new FileNotFoundException("Could not find file 'settings.lua'.", "settings.lua"));
        CompatibilityProblems.Observe(new DirectoryNotFoundException("Could not find a part of the path 'Saves'."));
        CompatibilityProblems.Observe(new System.IO.IsolatedStorage.IsolatedStorageException("Operation not permitted"));
        // The runtime's own probes.
        CompatibilityProblems.Observe(new FileNotFoundException(
            "Could not load file or assembly 'Game.resources, Version=1.0.0.0, Culture=en-GB, PublicKeyToken=null'.",
            "Game.resources, Version=1.0.0.0, Culture=en-GB, PublicKeyToken=null"));
        CompatibilityProblems.Observe(new FileNotFoundException(
            "Could not load file or assembly 'Game.XmlSerializers, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.",
            "Game.XmlSerializers, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
        // Ordinary game exceptions.
        CompatibilityProblems.Observe(new NullReferenceException());
        CompatibilityProblems.Observe(new ArgumentOutOfRangeException("index"));
        CompatibilityProblems.Observe(new InvalidOperationException("Collection was modified"));

        Assert.Empty(CompatibilityProblems.End());
    }

    [Fact]
    public void A_missing_assembly_is_recorded()
    {
        CompatibilityProblems.Begin();
        CompatibilityProblems.Observe(new FileNotFoundException(
            "Could not load file or assembly 'System.ServiceModel.Http, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'. The system cannot find the file specified.",
            "System.ServiceModel.Http, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"));
        var problems = CompatibilityProblems.End();

        CompatibilityProblem p = Assert.Single(problems);
        Assert.Equal(CompatibilityProblems.Categories.MissingAssembly, p.Category);
        Assert.Equal("System.ServiceModel.Http", p.Key);
    }

    [Fact]
    public void A_reflection_type_load_exception_contributes_its_loader_exceptions()
    {
        CompatibilityProblems.Begin();
        CompatibilityProblems.Observe(new ReflectionTypeLoadException(
            new Type?[] { null, null },
            new Exception?[] { MissingType("A.B"), new MissingFieldException("Field not found: 'X.Y'.") }));
        var problems = CompatibilityProblems.End();

        Assert.Equal(new[] { CompatibilityProblems.Categories.MissingType, CompatibilityProblems.Categories.MissingMember },
            problems.Select(p => p.Category).ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromDraw() => throw new NullReferenceException();

    [Fact]
    public void Frame_exceptions_collapse_by_type_and_thrower()
    {
        CompatibilityProblems.Begin();
        for (int i = 0; i < 100; i++)
        {
            try { ThrowFromDraw(); }
            catch (Exception ex) { CompatibilityProblems.NoteFrameException(CompatibilityProblems.Categories.DrawThrew, ex); }
        }
        var problems = CompatibilityProblems.End();

        CompatibilityProblem p = Assert.Single(problems);
        Assert.Equal(100, p.Count);
        Assert.Contains(nameof(ThrowFromDraw), p.Key);
        Assert.NotNull(p.Stack);
    }

    [Fact]
    public void The_first_chance_observer_sees_what_a_game_catches()
    {
        CompatibilityProblems.InstallFirstChanceObserver();
        CompatibilityProblems.Begin();
        try { throw MissingType("Microsoft.Phone.Swallowed"); }
        catch (TypeLoadException) { /* the game carries on */ }
        try { throw new HttpRequestException("offline"); }
        catch (HttpRequestException) { }
        var problems = CompatibilityProblems.End();

        CompatibilityProblem p = Assert.Single(problems);
        Assert.Contains("Microsoft.Phone.Swallowed", p.Detail);
        Assert.Contains(nameof(The_first_chance_observer_sees_what_a_game_catches), p.Stack);
    }

    [Fact]
    public void Each_problem_is_its_own_crash_kind_report_shaped_to_group_across_titles()
    {
        Assert.Empty(CompatibilityProblems.BuildProblemReports(Array.Empty<CompatibilityProblem>(), 0, "ended normally", TimeSpan.FromSeconds(5)));

        CompatibilityProblems.Begin();
        CompatibilityProblems.Observe(MissingType("System.Windows.Controls.RichTextBox"), captureStack: true);
        CompatibilityProblems.Observe(MissingType("System.Windows.Controls.RichTextBox"));
        CompatibilityProblems.Note(CompatibilityProblems.Categories.UiRender, "Popup", "Popup: popups are not drawn");
        var problems = CompatibilityProblems.End();

        var reports = CompatibilityProblems.BuildProblemReports(problems, 0, "ended normally", TimeSpan.FromSeconds(83));
        Assert.Equal(2, reports.Count);

        CrashReport missing = reports[0];
        Assert.Equal(CrashReport.Sources.Compatibility, missing.Source);
        Assert.True(CrashReport.Sources.IsNotACrash(missing.Source));
        Assert.False(CrashReport.Sources.SentAsManual(missing.Source)); // goes to the hub as kind "crash"
        Assert.Equal("System.TypeLoadException", missing.ExceptionType);
        Assert.StartsWith("missing-type: System.Windows.Controls.RichTextBox [FNA]", missing.ExceptionMessage);
        Assert.Equal("2", missing.Extra["problem_count"]);
        Assert.Equal("83", missing.Extra["run_seconds"]);
        Assert.Equal("2", missing.Extra["run_problems"]);

        // Exactly one "at" line - the synthetic frame - so the hub fingerprint is the same for every
        // title; where it was first seen follows as "from" lines the fingerprinter ignores.
        string[] lines = missing.StackTrace!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("at WPR.Compatibility.missing-type: System.Windows.Controls.RichTextBox [FNA]", lines[0]);
        Assert.Single(lines, l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("from ") && l.Contains(nameof(Each_problem_is_its_own_crash_kind_report_shaped_to_group_across_titles)));

        CrashReport ui = reports[1];
        Assert.Equal("WPR.Compatibility.uirender", ui.ExceptionType);
        Assert.StartsWith("at WPR.Compatibility.uirender: Popup", ui.StackTrace);
        Assert.All(reports, r => Assert.True(r.Extra.Count < 46));
    }

    [Fact]
    public void The_crash_own_problem_is_not_also_a_compat_report()
    {
        CompatibilityProblems.Begin();
        var crash = new MissingMethodException("Method not found: 'Void Game.Boom()'.");
        CompatibilityProblems.Observe(crash);
        CompatibilityProblems.Observe(MissingType("Other"));
        var problems = CompatibilityProblems.End();

        string? key = CompatibilityProblems.KeyOf(new System.Reflection.TargetInvocationException(crash));
        var reports = CompatibilityProblems.BuildProblemReports(problems, 0, "crashed", TimeSpan.Zero, key);
        Assert.Single(reports);
        Assert.Contains("Other", reports[0].ExceptionMessage);
    }

    private static CrashReport Candidate(string title, string key)
    {
        var r = new CrashReport { Source = CrashReport.Sources.Compatibility, TitleId = title };
        r.Extra["problem_category"] = "missing-type";
        r.Extra["problem_key"] = key;
        r.Extra["problem_count"] = "1";
        return r;
    }

    [Fact]
    public void A_run_sends_at_most_ten_and_the_last_names_the_rest()
    {
        var candidates = Enumerable.Range(1, 14).Select(i => Candidate("T", "Type" + i)).ToList();
        var sent = CompatibilityProblems.SelectForSending(candidates, r => r.Extra["problem_key"] == "Type2");

        Assert.Equal(CompatibilityProblems.MaxReportsPerRun, sent.Count);
        Assert.DoesNotContain(sent, r => r.Extra["problem_key"] == "Type2");
        Assert.Equal("3", sent[^1].Extra["more_problems"]);       // 13 new, 10 sent
        Assert.Equal("[missing-type] x1 Type12", sent[^1].Extra["m01"]);
        Assert.False(sent[0].Extra.ContainsKey("more_problems"));
    }

    [Fact]
    public void The_same_problem_is_sent_once_per_title_and_version()
    {
        string a = CompatibilityProblems.SentId(Candidate("{abcd}", "RichTextBox"), "0.2.06")!;
        Assert.Equal(a, CompatibilityProblems.SentId(Candidate("ABCD", "RichTextBox"), "0.2.06"));
        Assert.NotEqual(a, CompatibilityProblems.SentId(Candidate("EF01", "RichTextBox"), "0.2.06"));   // another title
        Assert.NotEqual(a, CompatibilityProblems.SentId(Candidate("ABCD", "RichTextBox"), "0.2.07"));   // a new WPR
        Assert.Null(CompatibilityProblems.SentId(new CrashReport { Source = CrashReport.Sources.GameRun }, "0.2.06"));

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-compat-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new CompatibilitySentLog(System.IO.Path.Combine(dir, "compat-sent.txt"));
            Assert.False(log.Contains(a));
            log.Add(a);
            Assert.True(new CompatibilitySentLog(System.IO.Path.Combine(dir, "compat-sent.txt")).Contains(a));
            File.AppendAllText(System.IO.Path.Combine(dir, "compat-sent.txt"), "garbage\n0123");     // torn: ignored
            Assert.True(log.Contains(a));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private sealed class ListReporter : ICrashReporter
    {
        public readonly System.Collections.Generic.List<CrashReport> Reports = new();
        public void Report(CrashReport report) => Reports.Add(report);
        public System.Threading.Tasks.Task FlushAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    [Fact]
    public void A_run_persists_each_new_problem_and_survives_a_torn_write()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-compat-test-" + Guid.NewGuid().ToString("N"));
        string? before = CompatibilityProblems.RunFilePath;
        CompatibilityProblems.RunFilePath = System.IO.Path.Combine(dir, "compat-run.txt");
        try
        {
            CompatibilityProblems.Begin(new RunIdentity { ProductId = "{C0FFEE}", TitleName = "Carcassonne", InstallFolder = @"C:\x\y" });
            CompatibilityProblems.Observe(MissingType("System.Windows.Controls.RichTextBox"));
            CompatibilityProblems.Observe(MissingType("System.Windows.Controls.RichTextBox")); // a repeat writes nothing
            CompatibilityProblems.Note(CompatibilityProblems.Categories.UiRender, "Popup", "tab\there\nand a new line");

            string text = File.ReadAllText(CompatibilityProblems.RunFilePath);
            Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("p\t", StringComparison.Ordinal)));

            // The live run is never taken for an abandoned one.
            Assert.Null(CompatibilityProblems.TakeRunFile(force: false));
            Assert.True(File.Exists(CompatibilityProblems.RunFilePath));

            // What a native crash leaves: the file as it stands, with half a line at the end.
            File.AppendAllText(CompatibilityProblems.RunFilePath, "p\tmissing-type\tHalf");
            PersistedRun run = CompatibilityProblems.ParseRunFile(File.ReadAllText(CompatibilityProblems.RunFilePath))!;
            Assert.Equal("{C0FFEE}", run.Run.ProductId);
            Assert.Equal("Carcassonne", run.Run.TitleName);
            Assert.Equal(@"C:\x\y", run.Run.InstallFolder);
            Assert.Equal(Environment.ProcessId, run.Run.ProcessId);
            Assert.Equal(2, run.Problems.Count);
            Assert.Equal("System.TypeLoadException", run.Problems[0].ExceptionType);
            Assert.Equal("tab\there\nand a new line", run.Problems[1].Detail);
            Assert.Null(run.Problems[1].ExceptionType);

            // A run that ends normally drops its file.
            CompatibilityProblems.End();
            CompatibilityProblems.DiscardRunFile();
            Assert.False(File.Exists(CompatibilityProblems.RunFilePath));

            // Unparseable: no product id.
            Assert.Null(CompatibilityProblems.ParseRunFile("wpr-compat-run 1\nproduct\t\n"));
            Assert.Null(CompatibilityProblems.ParseRunFile("rubbish"));
        }
        finally
        {
            CompatibilityProblems.End();
            CompatibilityProblems.RunFilePath = before;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void A_run_that_died_is_reported_as_a_native_crash_carrying_its_problems()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-compat-test-" + Guid.NewGuid().ToString("N"));
        string? before = CompatibilityProblems.RunFilePath;
        CompatibilityProblems.RunFilePath = System.IO.Path.Combine(dir, "compat-run.txt");
        try
        {
            // A run in a process that has since died (pid 0 is never alive).
            CompatibilityProblems.Begin(new RunIdentity { ProductId = "{C0FFEE}", TitleName = "Carcassonne", ProcessId = 0 });
            CompatibilityProblems.Observe(MissingType("System.Windows.Controls.RichTextBox"), captureStack: true);
            CompatibilityProblems.Observe(MissingType("System.Windows.Data.BindingExpression"));
            CompatibilityProblems.End(); // the process "died": nothing discarded the file

            var reporter = new ListReporter();
            CompatibilityProblems.RecoverAbandonedRun(reporter);

            Assert.False(File.Exists(CompatibilityProblems.RunFilePath));
            CrashReport crash = Assert.Single(reporter.Reports, r => r.Source == CrashReport.Sources.NativeCrash);
            Assert.Equal("{C0FFEE}", crash.TitleId);
            Assert.Equal("NativeCrash", crash.ExceptionType);
            Assert.Equal("2", crash.Extra["problem_count"]);
            Assert.Equal("next-start", crash.Extra["recovered"]);
            Assert.Contains("RichTextBox", crash.StackTrace);
            // A native crash has no frames of its own: the problems' stacks must not become its
            // fingerprint, so they are "from" lines, never "at" frames.
            Assert.Contains("    from ", crash.StackTrace);
            Assert.DoesNotContain(crash.StackTrace!.Split('\n'), l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal));
            Assert.Equal(2, reporter.Reports.Count(r => r.Source == CrashReport.Sources.Compatibility && r.TitleId == "{C0FFEE}"));

            // Taken once: a second recovery finds nothing.
            CompatibilityProblems.RecoverAbandonedRun(reporter);
            Assert.Equal(3, reporter.Reports.Count);
        }
        finally
        {
            CompatibilityProblems.End();
            CompatibilityProblems.RunFilePath = before;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void A_crash_report_carries_the_problems_instead_of_a_second_report()
    {
        CompatibilityProblems.Begin();
        CompatibilityProblems.Observe(MissingType("Gone"));
        var problems = CompatibilityProblems.Snapshot();
        CompatibilityProblems.End();

        CrashReport crash = CrashReport.FromException(new InvalidOperationException("boom"), CrashReport.Sources.GameRun);
        CompatibilityProblems.AttachTo(crash, problems, 0, "crashed", TimeSpan.Zero);

        Assert.Equal(CrashReport.Sources.GameRun, crash.Source);
        Assert.Equal("1", crash.Extra["problem_count"]);
        Assert.Contains("Gone", crash.StackTrace);
    }
}
