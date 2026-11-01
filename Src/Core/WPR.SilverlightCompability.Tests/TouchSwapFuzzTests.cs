using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;
using WPR.Xna.Rhi;
using Xunit;
using Xunit.Abstractions;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Fingers going on and off fast (Halo's fire thumb while the other thumb holds the stick),
/// modelled the way Android and SDL deliver them: pointer ids are the lowest free, SDL removes a
/// finger by swapping the last one into its place, and the Java thread changes SDL's finger list
/// at any moment, so a press or lift can reach the polled list a frame before its event is noted.
/// Whatever the timing, once the fingers stop moving every touch a consumer saw go down must have
/// gone up again.
/// </summary>
[Collection(nameof(TouchPanel))]
public class TouchSwapFuzzTests
{
    private readonly ITestOutputHelper _out;

    public TouchSwapFuzzTests(ITestOutputHelper output)
    {
        _out = output;
        typeof(TouchPanel).GetMethod("ResetForNewLaunch", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    }

    private static readonly MethodInfo SetFinger = typeof(TouchPanel).GetMethod("SetFinger", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo Update = typeof(TouchPanel).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo NotePressed = typeof(TouchPanel).GetMethod("NoteSourcePressed", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo NoteLifted = typeof(TouchPanel).GetMethod("NoteSourceLifted", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>SDL's finger list for one touch device, and its event queue.</summary>
    public sealed class Sdl
    {
        public readonly List<(int Source, Vector2 Pos)> Fingers = new();
        public readonly Queue<(bool Down, int Source, Vector2 Pos)> Events = new();

        public void Down(Vector2 pos)
        {
            // Android: the lowest pointer id not in use. WprSourceFinger adds one.
            int id = 0;
            while (Fingers.Any(f => f.Source == id + 1)) id++;
            Fingers.Add((id + 1, pos));
            Events.Enqueue((true, id + 1, pos));
        }

        public void Up(int index)
        {
            int source = Fingers[index].Source;
            // SDL_DelFinger: the last finger takes the removed one's place.
            Fingers[index] = Fingers[^1];
            Fingers.RemoveAt(Fingers.Count - 1);
            Events.Enqueue((false, source, Vector2.Zero));
        }

        public void Poll()
        {
            while (Events.TryDequeue(out var e))
            {
                if (e.Down) NotePressed.Invoke(null, new object[] { e.Source, e.Pos, false });
                else NoteLifted.Invoke(null, new object[] { e.Source });
            }
        }
    }

    public class SdlDrain : SyntheticTouchTests.DefaultProxy
    {
        public Sdl Sdl = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IInputBackend.UpdateTouchPanelState))
            {
                for (int i = 0; i < Sdl.Fingers.Count; i++)
                {
                    SetFinger.Invoke(null, new object[] { i, Sdl.Fingers[i].Source, Sdl.Fingers[i].Pos });
                }
                for (int i = Sdl.Fingers.Count; i < 8; i++)
                {
                    SetFinger.Invoke(null, new object[] { i, -1, Vector2.Zero });
                }
                return null;
            }
            return base.Invoke(targetMethod, args);
        }
    }

    /// <summary>
    /// A consumer that turns GetState() into press/move/lift events, with the same rules as the
    /// WP8 pointer feed (Wp8NativeSession.PumpTouch, which this project cannot reference), and
    /// the game as the set of pointers it was told are down.
    /// </summary>
    private sealed class PointerFeed
    {
        private readonly Dictionary<int, Vector2> _fingers = new();
        private int _newest;
        private readonly List<int> _releaseNext = new();
        public readonly HashSet<int> GuestDown = new();
        public readonly List<string> Log = new();

        private void Inject(string kind, int id)
        {
            Log.Add($"{kind} {id}");
            if (kind == "Pressed") GuestDown.Add(id);
            else if (kind == "Released") GuestDown.Remove(id);
        }

        public void Pump()
        {
            foreach (int id in _releaseNext) Inject("Released", id);
            _releaseNext.Clear();
            var seen = new HashSet<int>();
            foreach (TouchLocation touch in TouchPanel.GetState())
            {
                if (touch.State == TouchLocationState.Invalid) continue;
                seen.Add(touch.Id);
                bool down = _fingers.TryGetValue(touch.Id, out Vector2 last);
                bool unseen = touch.Id > _newest;
                _newest = Math.Max(_newest, touch.Id);
                switch (touch.State)
                {
                    case TouchLocationState.Pressed:
                    case TouchLocationState.Moved when !down:
                        _fingers[touch.Id] = touch.Position; Inject("Pressed", touch.Id); break;
                    case TouchLocationState.Moved when touch.Position != last:
                        _fingers[touch.Id] = touch.Position; Inject("Moved", touch.Id); break;
                    case TouchLocationState.Released when down:
                        _fingers.Remove(touch.Id); Inject("Released", touch.Id); break;
                    case TouchLocationState.Released when unseen:
                        Inject("Pressed", touch.Id); _releaseNext.Add(touch.Id); break;
                }
            }
            foreach (int gone in _fingers.Keys.Where(k => !seen.Contains(k)).ToArray())
            {
                _fingers.Remove(gone); Inject("Released", gone);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryTouchThatWentDownComesUp(int seed)
    {
        var rng = new Random(seed);
        var sdl = new Sdl();
        var drain = DispatchProxy.Create<IInputBackend, SdlDrain>();
        ((SdlDrain)(object)drain).Sdl = sdl;
        XnaBackend.SetInput(drain);
        var feed = new PointerFeed();
        try
        {
            var stick = new Vector2(150, 600);
            var fire = new Vector2(1100, 600);
            for (int frame = 0; frame < 4000; frame++)
            {
                sdl.Poll();

                // The Java thread, between the poll and the drain: a few events a frame at most.
                int events = rng.Next(0, 4);
                for (int e = 0; e < events; e++)
                {
                    int r = rng.Next(10);
                    if (sdl.Fingers.Count < 2 && r < 6)
                    {
                        sdl.Down(sdl.Fingers.Count == 0 && r < 2 ? stick : fire + new Vector2(rng.Next(-30, 30), 0));
                    }
                    else if (sdl.Fingers.Count > 0)
                    {
                        sdl.Up(rng.Next(sdl.Fingers.Count));
                    }
                }

                Update.Invoke(null, null);
                feed.Pump();

                // Some events land after the drain too, before the next poll.
                if (rng.Next(4) == 0 && sdl.Fingers.Count > 0)
                {
                    sdl.Up(rng.Next(sdl.Fingers.Count));
                }
                else if (rng.Next(4) == 0 && sdl.Fingers.Count < 2)
                {
                    sdl.Down(fire);
                }

                // Hold still now and then: once nothing moves for three frames, the guest's
                // pointers must be exactly the fingers on the glass.
                if (frame % 50 == 49)
                {
                    for (int still = 0; still < 3; still++)
                    {
                        sdl.Poll();
                        Update.Invoke(null, null);
                        feed.Pump();
                    }
                    if (feed.GuestDown.Count != sdl.Fingers.Count)
                    {
                        foreach (string line in feed.Log.TakeLast(40)) _out.WriteLine(line);
                    }
                    Assert.Equal(sdl.Fingers.Count, feed.GuestDown.Count);
                }
            }
        }
        finally
        {
            XnaBackend.Clear();
        }
    }
}
