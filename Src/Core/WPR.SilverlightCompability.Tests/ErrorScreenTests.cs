using System;
using System.Threading;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// The error screen's frame watch: it must trip only on frames that BOTH threw and drew nothing,
/// for long enough. Titles that throw every frame but still paint (Feed Me Oil, Chickens Can't
/// Fly, Brain Challenge) must never see it.
/// </summary>
[CollectionDefinition(nameof(ErrorScreenTests), DisableParallelization = true)]
public class ErrorScreenCollection { }

[Collection(nameof(ErrorScreenTests))]
public class ErrorScreenTests : IDisposable
{
    public ErrorScreenTests()
    {
        ErrorScreen.Reset();
        ErrorScreen.Factory = (_, _) => null;
    }

    public void Dispose() => ErrorScreen.Reset();

    private static void Frames(int count, bool throws, int draws, int sleepMs = 0)
    {
        for (int i = 0; i < count; i++)
        {
            if (throws) ErrorScreen.NoteFrameException(new InvalidOperationException("frame " + i));
            ErrorScreen.EndFrame(draws);
            if (sleepMs > 0) Thread.Sleep(sleepMs);
        }
    }

    [Fact]
    public void A_game_that_throws_but_still_draws_never_trips_it()
    {
        Frames(200, throws: true, draws: 3, sleepMs: 12);
        Assert.Null(ErrorScreen.TakePending());
    }

    [Fact]
    public void A_game_that_draws_nothing_but_throws_nothing_never_trips_it()
    {
        Frames(200, throws: false, draws: 0, sleepMs: 12);
        Assert.Null(ErrorScreen.TakePending());
    }

    [Fact]
    public void Many_failing_frames_in_a_short_time_do_not_trip_it()
    {
        Frames(500, throws: true, draws: 0);
        Assert.Null(ErrorScreen.TakePending());
    }

    [Fact]
    public void One_drawn_frame_restarts_the_count()
    {
        Frames(40, throws: true, draws: 0, sleepMs: 30);
        Frames(1, throws: true, draws: 1);
        Frames(40, throws: true, draws: 0, sleepMs: 30);
        Assert.Null(ErrorScreen.TakePending());
    }

    [Fact]
    public void Two_seconds_of_failing_undrawn_frames_trip_it_with_the_first_exception()
    {
        Frames(70, throws: true, draws: 0, sleepMs: 32);
        GameErrorReport? report = ErrorScreen.TakePending();
        Assert.NotNull(report);
        Assert.Contains("frame 0", report!.Headline);
        Assert.False(report.Stopped);
        Assert.Same(report, ErrorScreen.Shown);

        // Once shown, nothing else is queued.
        Frames(70, throws: true, draws: 0);
        Assert.Null(ErrorScreen.TakePending());
    }

    [Fact]
    public void Without_a_screen_a_request_is_declined()
    {
        ErrorScreen.Factory = null;
        Assert.False(ErrorScreen.Request(new GameErrorReport { Headline = "x", Stopped = true }));
    }

    [Theory]
    [InlineData(@"Foo.Bar() in C:\a\b\Game.cs:line 1097", "Foo.Bar() (Game.cs:1097)")]
    [InlineData("Foo.Bar() in /src/x/File.cs:line 3", "Foo.Bar() (File.cs:3)")]
    [InlineData("Foo.Bar()", "Foo.Bar()")]
    public void Frames_lose_their_paths(string frame, string expected)
    {
        Assert.Equal(expected, ErrorScreen.ShortenFrame(frame));
    }
}
