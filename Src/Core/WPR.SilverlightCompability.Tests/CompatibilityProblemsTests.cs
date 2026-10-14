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
        Assert.StartsWith("System.ServiceModel.Http", p.Key);
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
    public void The_report_is_one_compat_report_or_none()
    {
        Assert.Null(CompatibilityProblems.BuildReport(Array.Empty<CompatibilityProblem>(), 0, "ended normally", TimeSpan.FromSeconds(5)));

        CompatibilityProblems.Begin();
        CompatibilityProblems.Observe(MissingType("Microsoft.Xna.Framework.Media.PlaylistCollection"));
        CompatibilityProblems.Note(CompatibilityProblems.Categories.UiRender, "Popup", "Popup: popups are not drawn");
        var problems = CompatibilityProblems.End();

        CrashReport? report = CompatibilityProblems.BuildReport(problems, 0, "ended normally", TimeSpan.FromSeconds(83));
        Assert.NotNull(report);
        Assert.Equal(CrashReport.Sources.Compatibility, report!.Source);
        Assert.True(CrashReport.Sources.IsNotACrash(report.Source));
        Assert.Equal("WPR.CompatibilityProblems", report.ExceptionType);
        Assert.StartsWith("2 compatibility problem(s): missing-type", report.ExceptionMessage);
        Assert.Equal("2", report.Extra["problem_count"]);
        Assert.Equal("83", report.Extra["run_seconds"]);
        Assert.StartsWith("[missing-type] x1", report.Extra["p01"]);
        Assert.StartsWith("[uirender] x1", report.Extra["p02"]);
        Assert.Contains("PlaylistCollection", report.StackTrace);
        Assert.True(report.Extra.Count < 46);
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
