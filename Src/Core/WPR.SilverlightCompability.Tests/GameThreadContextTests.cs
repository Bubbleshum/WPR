using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// An XNA title's game thread has a <see cref="SynchronizationContext"/>, as WP7's did: a post
/// runs on the game thread at the next tick. OpenXLive (iRunner) refuses to start without one.
/// </summary>
[CollectionDefinition(nameof(GameThreadContextTests), DisableParallelization = true)]
[Collection(nameof(GameThreadContextTests))]
public class GameThreadContextTests
{
    private static void Drain() =>
        typeof(WprGameThread).GetMethod("DrainPending", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

    [Fact]
    public void A_post_waits_for_the_next_tick()
    {
        Drain();
        var context = new GameThreadSynchronizationContext();
        int runs = 0;
        context.Post(_ => runs++, null);
        Assert.Equal(0, runs);
        Drain();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_send_from_the_game_thread_runs_at_once()
    {
        var context = new GameThreadSynchronizationContext();
        int runs = 0;
        context.Send(_ => runs++, null);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_send_from_another_thread_waits_for_the_game_thread()
    {
        Drain();
        var context = new GameThreadSynchronizationContext();
        int ranOn = -1;
        Task send = Task.Run(() => context.Send(_ => ranOn = Environment.CurrentManagedThreadId, null));
        Assert.False(send.Wait(100));
        while (!send.IsCompleted)
        {
            Drain();
            Thread.Sleep(5);
        }

        Assert.Equal(Environment.CurrentManagedThreadId, ranOn);
    }
}
