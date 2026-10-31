using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using WPR.Engine.Input;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// The synthetic finger (keyboard touch bindings, the mouse wheel, the test harness) lives in a
/// reserved TouchPanel slot that nothing else clears. A tap must leave GetState() the way a real
/// finger does: Pressed, then Released for one frame, then nothing.
/// </summary>
[Collection(nameof(TouchPanel))]
public class SyntheticTouchTests
{
    // TouchPanel is static: every test starts from no touches, as a new game does.
    public SyntheticTouchTests() =>
        typeof(TouchPanel).GetMethod("ResetForNewLaunch", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

    [Fact]
    public void ATapIsReleasedForOneFrameAndThenGone()
    {
        var host = DispatchProxy.Create<IKeyboardEmulationHost, ScriptedHost>();
        var scripted = (ScriptedHost)(object)host;
        scripted.Samples.Enqueue(new SyntheticTouchSample(true, new Vector2(100, 120), Vector2.Zero, true, false));
        scripted.Samples.Enqueue(new SyntheticTouchSample(true, new Vector2(100, 120), Vector2.Zero, false, true));

        XnaBackend.SetKeyboardEmulation(host);
        try
        {
            XnaBackend.SetInput(KeyboardEmulation.WrapInput(DispatchProxy.Create<IInputBackend, DefaultProxy>()));
            MethodInfo update = typeof(TouchPanel).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static)!;

            var seen = new List<string>();
            for (int frame = 0; frame < 5; frame++)
            {
                update.Invoke(null, null);
                TouchCollection state = TouchPanel.GetState();
                seen.Add(state.Count == 0 ? "none" : state[0].State.ToString());
            }

            // Before the fix the last three were "Released": the lift stayed in the slot for ever,
            // and a game firing buttons on a Released touch pressed the last button again whenever
            // a screen under it regained input (AE Fruit Slash's Settings reopened after Back).
            Assert.Equal(new[] { "Pressed", "Released", "none", "none", "none" }, seen);
        }
        finally
        {
            XnaBackend.Clear();
            typeof(TouchPanel).GetField("ReservedFingerSlots", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0);
        }
    }

    [Fact]
    public void APressTheFrameAfterAReleaseIsPressed()
    {
        var host = DispatchProxy.Create<IKeyboardEmulationHost, ScriptedHost>();
        var scripted = (ScriptedHost)(object)host;
        scripted.Samples.Enqueue(new SyntheticTouchSample(true, new Vector2(40, 120), Vector2.Zero, true, false));
        scripted.Samples.Enqueue(new SyntheticTouchSample(true, new Vector2(760, 90), Vector2.Zero, false, true));
        scripted.Samples.Enqueue(new SyntheticTouchSample(true, new Vector2(760, 200), Vector2.Zero, true, false));
        scripted.Samples.Enqueue(new SyntheticTouchSample(true, new Vector2(700, 210), Vector2.Zero, false, false));

        XnaBackend.SetKeyboardEmulation(host);
        try
        {
            XnaBackend.SetInput(KeyboardEmulation.WrapInput(DispatchProxy.Create<IInputBackend, DefaultProxy>()));
            MethodInfo update = typeof(TouchPanel).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static)!;

            var seen = new List<string>();
            var ids = new List<int>();
            for (int frame = 0; frame < 4; frame++)
            {
                update.Invoke(null, null);
                TouchCollection state = TouchPanel.GetState();
                seen.Add(state.Count == 0 ? "none" : state[0].State.ToString());
                ids.Add(state.Count == 0 ? 0 : state[0].Id);
            }

            // A new touch is Pressed even when the slot's last state was Released (it was Moved).
            Assert.Equal(new[] { "Pressed", "Released", "Pressed", "Moved" }, seen);

            // And it has a new id, as every new touch did on the phone, although the synthetic
            // finger's own id never changes. Within one touch the id holds.
            Assert.Equal(ids[0], ids[1]);
            Assert.Equal(ids[2], ids[3]);
            Assert.NotEqual(ids[0], ids[2]);
        }
        finally
        {
            XnaBackend.Clear();
            typeof(TouchPanel).GetField("ReservedFingerSlots", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0);
        }
    }

    [Fact]
    public void AHeldFingerThatShiftsSlotKeepsItsId()
    {
        // The SDL drain writes fingers by index: when the first of two lifts, the second moves
        // down to slot 0. It is the same touch and must keep its id, and not be released.
        MethodInfo setFinger = typeof(TouchPanel).GetMethod("SetFinger", BindingFlags.NonPublic | BindingFlags.Static)!;
        var frames = new Queue<Action>();
        frames.Enqueue(() => { setFinger.Invoke(null, new object[] { 0, 10, new Vector2(10, 10) }); setFinger.Invoke(null, new object[] { 1, 11, new Vector2(50, 50) }); });
        frames.Enqueue(() => { setFinger.Invoke(null, new object[] { 0, 11, new Vector2(55, 55) }); setFinger.Invoke(null, new object[] { 1, (int)typeof(TouchPanel).GetField("NO_FINGER", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!, Vector2.Zero }); });

        var input = DispatchProxy.Create<IInputBackend, DrainProxy>();
        ((DrainProxy)(object)input).Frames = frames;
        XnaBackend.SetInput(input);
        try
        {
            MethodInfo update = typeof(TouchPanel).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static)!;
            update.Invoke(null, null);
            TouchCollection first = TouchPanel.GetState();
            int secondId = first[1].Id;
            Assert.NotEqual(first[0].Id, secondId);

            update.Invoke(null, null);
            TouchCollection second = TouchPanel.GetState();
            Assert.Equal(TouchLocationState.Moved, second[0].State);
            Assert.Equal(secondId, second[0].Id);
            Assert.DoesNotContain(second, t => t.Id == secondId && t.State == TouchLocationState.Released);
        }
        finally
        {
            XnaBackend.Clear();
        }
    }

    [Fact]
    public void TwoFingersLiftedInEitherOrderAreBothReleasedAndThenGone()
    {
        // A virtual pad held in slot 0 and a button in slot 1. Lifting the pad first makes the
        // drain shift the button down into slot 0, so the pad's id was in no slot and was never
        // Released: the game held the pad for ever (Final Fantasy III, 0.2.08 on Android).
        foreach (bool padFirst in new[] { true, false })
        {
            int none = (int)typeof(TouchPanel).GetField("NO_FINGER", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            MethodInfo setFinger = typeof(TouchPanel).GetMethod("SetFinger", BindingFlags.NonPublic | BindingFlags.Static)!;
            void Slot(int slot, int finger, float x) => setFinger.Invoke(null, new object[] { slot, finger, new Vector2(x, 400) });

            var frames = new Queue<Action>();
            frames.Enqueue(() => { Slot(0, 1, 60); Slot(1, none, 0); });              // pad down
            frames.Enqueue(() => { Slot(0, 1, 62); Slot(1, 2, 700); });               // button down
            frames.Enqueue(() => { Slot(0, 1, 64); Slot(1, 2, 700); });               // both held
            if (padFirst)
            {
                frames.Enqueue(() => { Slot(0, 2, 700); Slot(1, none, 0); });         // pad up: button shifts
                frames.Enqueue(() => { Slot(0, 2, 700); Slot(1, none, 0); });
            }
            else
            {
                frames.Enqueue(() => { Slot(0, 1, 64); Slot(1, none, 0); });          // button up
                frames.Enqueue(() => { Slot(0, 1, 64); Slot(1, none, 0); });
            }
            frames.Enqueue(() => { Slot(0, none, 0); Slot(1, none, 0); });           // last finger up
            frames.Enqueue(() => { Slot(0, none, 0); Slot(1, none, 0); });

            var input = DispatchProxy.Create<IInputBackend, DrainProxy>();
            ((DrainProxy)(object)input).Frames = frames;
            XnaBackend.SetInput(input);
            try
            {
                MethodInfo update = typeof(TouchPanel).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static)!;
                var released = new Dictionary<int, int>();
                var lastState = new Dictionary<int, TouchLocationState>();
                int padId = 0, buttonId = 0;
                for (int frame = 0; frame < 7; frame++)
                {
                    update.Invoke(null, null);
                    foreach (TouchLocation t in TouchPanel.GetState())
                    {
                        if (frame == 0) padId = t.Id;
                        if (frame == 1 && t.State == TouchLocationState.Pressed) buttonId = t.Id;
                        lastState[t.Id] = t.State;
                        if (t.State == TouchLocationState.Released)
                        {
                            released[t.Id] = released.GetValueOrDefault(t.Id) + 1;
                        }
                    }
                }

                Assert.NotEqual(0, padId);
                Assert.NotEqual(0, buttonId);
                Assert.NotEqual(padId, buttonId);
                // Each touch is Released exactly once, and nothing is left afterwards.
                Assert.Equal(1, released.GetValueOrDefault(padId));
                Assert.Equal(1, released.GetValueOrDefault(buttonId));
                Assert.Equal(TouchLocationState.Released, lastState[padId]);
                Assert.Equal(TouchLocationState.Released, lastState[buttonId]);
                Assert.Empty(TouchPanel.GetState());
            }
            finally
            {
                XnaBackend.Clear();
            }
        }
    }

    private static readonly MethodInfo NotePressed = typeof(TouchPanel).GetMethod("NoteSourcePressed", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo NoteLifted = typeof(TouchPanel).GetMethod("NoteSourceLifted", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo UpdateTouch = typeof(TouchPanel).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly int NoFinger = (int)typeof(TouchPanel).GetField("NO_FINGER", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static void Slot(int slot, int finger, float x, float y = 400) =>
        typeof(TouchPanel).GetMethod("SetFinger", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { slot, finger, new Vector2(x, y) });

    private static void Drain(Queue<Action> frames)
    {
        var input = DispatchProxy.Create<IInputBackend, DrainProxy>();
        ((DrainProxy)(object)input).Frames = frames;
        XnaBackend.SetInput(input);
    }

    [Fact]
    public void ATapThatGoesDownAndUpBetweenTwoFramesIsPressedThenReleased()
    {
        // The drain polls which fingers are down when it runs; a finger that went down and up in
        // between was never in GetState(), on any source. The phone reported every touch.
        var frames = new Queue<Action>();
        for (int i = 0; i < 4; i++) frames.Enqueue(() => { Slot(0, NoFinger, 0); Slot(1, NoFinger, 0); });
        Drain(frames);
        try
        {
            UpdateTouch.Invoke(null, null);
            Assert.Empty(TouchPanel.GetState());

            NotePressed.Invoke(null, new object[] { 3, new Vector2(120, 340), false });
            NoteLifted.Invoke(null, new object[] { 3 });

            var seen = new List<string>();
            int id = 0;
            for (int frame = 0; frame < 3; frame++)
            {
                UpdateTouch.Invoke(null, null);
                TouchCollection state = TouchPanel.GetState();
                seen.Add(state.Count == 0 ? "none" : state[0].State.ToString());
                if (frame == 0)
                {
                    id = state[0].Id;
                    Assert.Equal(new Vector2(120, 340), state[0].Position);
                }
                else if (state.Count > 0)
                {
                    Assert.Equal(id, state[0].Id);
                }
            }
            Assert.Equal(new[] { "Pressed", "Released", "none" }, seen);
        }
        finally
        {
            XnaBackend.Clear();
        }
    }

    [Fact]
    public void ASourceLiftedAndPressedAgainBetweenFramesIsANewTouch()
    {
        // Android pointer ids restart at 0, and a quick second tap of one finger reuses its id:
        // both used to read as the old touch Moved, with no lift between.
        var frames = new Queue<Action>();
        frames.Enqueue(() => { Slot(0, 5, 100); Slot(1, NoFinger, 0); });
        frames.Enqueue(() => { Slot(0, 5, 300); Slot(1, NoFinger, 0); });
        frames.Enqueue(() => { Slot(0, 5, 300); Slot(1, NoFinger, 0); });
        Drain(frames);
        try
        {
            UpdateTouch.Invoke(null, null);
            int first = TouchPanel.GetState()[0].Id;

            NoteLifted.Invoke(null, new object[] { 5 });
            NotePressed.Invoke(null, new object[] { 5, new Vector2(300, 400), false });
            UpdateTouch.Invoke(null, null);
            TouchCollection state = TouchPanel.GetState();
            Assert.Contains(state, t => t.Id == first && t.State == TouchLocationState.Released);
            Assert.Contains(state, t => t.Id != first && t.State == TouchLocationState.Pressed);

            UpdateTouch.Invoke(null, null);
            TouchCollection after = TouchPanel.GetState();
            Assert.Single(after);
            Assert.Equal(TouchLocationState.Moved, after[0].State);
            Assert.NotEqual(first, after[0].Id);
        }
        finally
        {
            XnaBackend.Clear();
        }
    }

    [Fact]
    public void TheMouseIsThePrimaryTouch()
    {
        // WP7's Mouse.GetState() was the first finger. On Android SDL makes no mouse from touch,
        // so titles reading only the mouse took no touch at all.
        TouchPanel.DisplayWidth = 800;
        TouchPanel.DisplayHeight = 480;
        foreach ((string name, int value) in new[] { ("INTERNAL_BackBufferWidth", 800), ("INTERNAL_BackBufferHeight", 480) })
        {
            typeof(Microsoft.Xna.Framework.Input.Mouse).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
        }
        var frames = new Queue<Action>();
        frames.Enqueue(() => { Slot(0, 1, 100, 200); Slot(1, NoFinger, 0); });  // first finger down
        frames.Enqueue(() => { Slot(0, 1, 110, 210); Slot(1, 2, 600, 50); });  // second finger joins
        frames.Enqueue(() => { Slot(0, 2, 600, 50); Slot(1, NoFinger, 0); });  // first lifts
        frames.Enqueue(() => { Slot(0, 2, 610, 60); Slot(1, NoFinger, 0); });  // second still down
        frames.Enqueue(() => { Slot(0, NoFinger, 0); Slot(1, NoFinger, 0); }); // all up
        frames.Enqueue(() => { Slot(0, 2, 700, 300); Slot(1, NoFinger, 0); }); // a new touch
        Drain(frames);
        try
        {
            var seen = new List<(ButtonState, int, int)>();
            for (int frame = 0; frame < 6; frame++)
            {
                UpdateTouch.Invoke(null, null);
                MouseState mouse = Microsoft.Xna.Framework.Input.Mouse.GetState();
                seen.Add((mouse.LeftButton, mouse.X, mouse.Y));
            }

            Assert.Equal((ButtonState.Pressed, 100, 200), seen[0]);
            Assert.Equal((ButtonState.Pressed, 110, 210), seen[1]);
            // The primary lifted: released where it was, and the other finger does not take over.
            Assert.Equal(ButtonState.Released, seen[2].Item1);
            Assert.Equal(ButtonState.Released, seen[3].Item1);
            Assert.Equal(ButtonState.Released, seen[4].Item1);
            Assert.Equal((ButtonState.Pressed, 700, 300), seen[5]);
        }
        finally
        {
            XnaBackend.Clear();
        }
    }

    public class DrainProxy : DefaultProxy
    {
        public Queue<Action> Frames = new Queue<Action>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IInputBackend.GetMouseState))
            {
                // No hardware mouse: at the origin, no buttons.
                for (int i = 1; i < args!.Length; i++) args[i] = i < 3 ? 0 : ButtonState.Released;
                return null;
            }
            if (targetMethod!.Name == nameof(IInputBackend.UpdateTouchPanelState))
            {
                // Like the real drain: every slot not written this frame is cleared first.
                MethodInfo setFinger = typeof(TouchPanel).GetMethod("SetFinger", BindingFlags.NonPublic | BindingFlags.Static)!;
                for (int i = 2; i < 8; i++) setFinger.Invoke(null, new object[] { i, -1, Vector2.Zero });
                if (Frames.Count > 0) Frames.Dequeue()();
                return null;
            }
            return base.Invoke(targetMethod, args);
        }
    }

    /// <summary>Answers every member with its default; the drain under the injector has nothing.</summary>
    public class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Type t = targetMethod!.ReturnType;
            return t == typeof(void) || !t.IsValueType ? null : Activator.CreateInstance(t);
        }
    }

    public class ScriptedHost : DefaultProxy
    {
        public readonly Queue<SyntheticTouchSample> Samples = new Queue<SyntheticTouchSample>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IKeyboardEmulationHost.AdvanceSyntheticTouch))
            {
                return Samples.Count > 0 ? Samples.Dequeue() : SyntheticTouchSample.Inactive;
            }
            return base.Invoke(targetMethod, args);
        }
    }
}

/// <summary>TouchPanel is static: its tests must not run beside each other.</summary>
[CollectionDefinition(nameof(TouchPanel), DisableParallelization = true)]
public class TouchPanelCollection
{
}
