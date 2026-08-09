using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Animation.Storyboard</c>. In SL a Storyboard
    /// is a Timeline that drives its <see cref="Children"/>. Begin/Stop here are
    /// near-no-ops — Begin fires <c>Completed</c> synchronously so user logic gated
    /// on "the timer ticked" doesn't deadlock.
    /// </summary>
    [ContentProperty(nameof(Children))]
    public class Storyboard : Timeline
    {
        public TimelineCollection Children { get; } = new TimelineCollection();

        // Begin/Stop/etc are deliberate no-ops. In SL, Begin starts the timeline
        // and fires Completed after Duration elapses. We don't run timelines, so
        // we can't honor that timing. Firing Completed synchronously here would
        // be catastrophic: WP7 code routinely restarts a timer from its own
        // Completed handler (PowerupsControl._tokenTimer is exactly this), and a
        // sync raise would recurse infinitely → StackOverflowException. Leaving
        // Completed unraised means logic gated on "the animation finished" never
        // ticks — accept that until a real timeline engine ships.
        public void Begin() { }
        public void Stop() { }
        public void Pause() { }
        public void Resume() { }
        public void Seek(TimeSpan offset) { }
        public void SeekAlignedToLastTick(TimeSpan offset) { }
        public void SkipToFill() { }
        public TimeSpan GetCurrentTime() => TimeSpan.Zero;
        public ClockState GetCurrentState() => ClockState.Stopped;

        // Attached properties. XAML and user code use these to bind a child animation
        // to a target FrameworkElement / property path. Real SL signatures take
        // (Timeline, …) — user IL emits exactly that, so the strong-typed first
        // parameter is critical for MissingMethodException avoidance.
        public static readonly DependencyProperty TargetNameProperty =
            DependencyProperty.RegisterAttached("TargetName", typeof(string), typeof(Storyboard),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty TargetPropertyProperty =
            DependencyProperty.RegisterAttached("TargetProperty", typeof(PropertyPath), typeof(Storyboard),
                new PropertyMetadata((object?)null));

        public static void SetTargetName(Timeline element, string name)
            => element?.SetValue(TargetNameProperty, name);
        public static string? GetTargetName(Timeline element)
            => (string?)element?.GetValue(TargetNameProperty);

        public static void SetTarget(Timeline element, DependencyObject target) { /* no-op */ }

        public static void SetTargetProperty(Timeline element, PropertyPath path)
            => element?.SetValue(TargetPropertyProperty, path);
        public static PropertyPath? GetTargetProperty(Timeline element)
            => (PropertyPath?)element?.GetValue(TargetPropertyProperty);
    }
}
