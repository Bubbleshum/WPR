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
            Assert.Equal(1, second.Count);
            Assert.Equal(TouchLocationState.Moved, second[0].State);
            Assert.Equal(secondId, second[0].Id);
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
