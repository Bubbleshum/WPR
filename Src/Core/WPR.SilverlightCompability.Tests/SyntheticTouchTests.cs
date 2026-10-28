using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
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
public class SyntheticTouchTests
{
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

    public class DrainProxy : DefaultProxy
    {
        public Queue<Action> Frames = new Queue<Action>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
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
