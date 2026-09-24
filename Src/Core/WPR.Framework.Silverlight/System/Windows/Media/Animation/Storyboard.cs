using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Animation.Storyboard</c>. In SL a Storyboard
    /// is a Timeline that drives its <see cref="Children"/>.
    /// </summary>
    /// <remarks>
    /// <para>It really runs now — see <see cref="AnimationClock"/> for the engine, why
    /// <c>Completed</c> is raised from the frame pump rather than from <see cref="Begin"/>, and
    /// which animation types are modelled. Before that it was a set of no-ops and
    /// <c>Completed</c> was never raised, which left any title gated on "the animation finished"
    /// stuck for ever.</para>
    ///
    /// <para><b>A storyboard the host never pumps behaves exactly as it used to</b> — it is
    /// registered and simply never advances. That is the pure-Silverlight (Avalonia) host today,
    /// so this is additive for those titles rather than a behaviour change.</para>
    /// </remarks>
    [ContentProperty(nameof(Children))]
    public class Storyboard : Timeline
    {
        public TimelineCollection Children { get; } = new TimelineCollection();

        /// <summary>
        /// Explicit targets set through <see cref="SetTarget"/>, keyed by the child timeline.
        /// </summary>
        /// <remarks>
        /// Static and weak-keyed because <c>SetTarget</c> is a static attached-property-shaped
        /// call that is handed only the child — it has no way to reach the storyboard holding it,
        /// and <c>TimelineCollection</c> is a plain list with no add hook to record one.
        /// </remarks>
        private static readonly ConditionalWeakTable<Timeline, object> ExplicitTargets =
            new ConditionalWeakTable<Timeline, object>();

        /// <summary>
        /// Each child's target property value when the storyboard began. Silverlight interpolates
        /// a first key frame from the property's existing value, not from zero.
        /// </summary>
        private readonly Dictionary<Timeline, object?> _baseValues = new Dictionary<Timeline, object?>();

        private TimeSpan _time;
        private bool _paused;
        private bool _filling;

        public void Begin()
        {
            _time = TimeSpan.Zero;
            _paused = false;
            _filling = false;
            _baseValues.Clear();
            CaptureBaseValues();
            AnimationClock.Start(this);
            ApplyAt(TimeSpan.Zero);
        }

        public void Stop()
        {
            AnimationClock.Remove(this);
            _time = TimeSpan.Zero;
            _paused = false;
            _filling = false;
        }

        public void Pause() => _paused = true;

        public void Resume() => _paused = false;

        public void Seek(TimeSpan offset)
        {
            _time = offset < TimeSpan.Zero ? TimeSpan.Zero : offset;
            ApplyAt(_time);
        }

        public void SeekAlignedToLastTick(TimeSpan offset) => Seek(offset);

        /// <summary>Jumps to the end, applies the final values and completes.</summary>
        public void SkipToFill()
        {
            TimeSpan duration = AnimationClock.NaturalDuration(this);
            if (duration == TimeSpan.MaxValue) duration = TimeSpan.Zero;
            _time = duration;
            ApplyAt(duration);
            Finish();
        }

        public TimeSpan GetCurrentTime() => _time;

        public ClockState GetCurrentState()
        {
            if (AnimationClock.IsRunning(this)) return ClockState.Active;
            return _filling ? ClockState.Filling : ClockState.Stopped;
        }

        /// <summary>
        /// Advances the clock by one frame, writes the current values, and completes when the
        /// timeline has run out (honouring <see cref="Timeline.RepeatBehavior"/> and
        /// <see cref="Timeline.AutoReverse"/>).
        /// </summary>
        internal void Advance(TimeSpan elapsed)
        {
            if (_paused) return;

            double speed = SpeedRatio > 0 ? SpeedRatio : 1.0;
            _time += TimeSpan.FromTicks((long)(elapsed.Ticks * speed));

            TimeSpan local = _time - (BeginTime ?? TimeSpan.Zero);
            if (local < TimeSpan.Zero) return;   // still inside BeginTime's delay

            TimeSpan duration = AnimationClock.NaturalDuration(this);
            if (duration <= TimeSpan.Zero)
            {
                // Nothing to interpolate. Apply once so a single-key-frame value still lands,
                // then complete on this frame rather than spinning for ever.
                ApplyAt(TimeSpan.Zero);
                Finish();
                return;
            }

            bool forever = duration == TimeSpan.MaxValue || RepeatBehavior == RepeatBehavior.Forever;
            TimeSpan iteration = AutoReverse ? duration + duration : duration;

            bool done = false;
            TimeSpan position = local;
            if (!forever)
            {
                double count = RepeatBehavior.HasCount ? Math.Max(1.0, RepeatBehavior.Count) : 1.0;
                double totalTicks = iteration.Ticks * count;
                if (totalTicks >= long.MaxValue) totalTicks = long.MaxValue;
                TimeSpan total = TimeSpan.FromTicks((long)totalTicks);
                if (local >= total) { done = true; position = total; }
            }

            TimeSpan inIteration;
            if (done)
            {
                // End on the value the timeline finishes at: its start again when it reverses,
                // its end otherwise.
                inIteration = AutoReverse ? TimeSpan.Zero : duration;
            }
            else
            {
                long within = iteration.Ticks <= 0 ? 0 : position.Ticks % iteration.Ticks;
                inIteration = TimeSpan.FromTicks(within);
                if (AutoReverse && inIteration > duration) inIteration = iteration - inIteration;
            }

            ApplyAt(inIteration);

            if (done) Finish();
        }

        /// <summary>
        /// Takes the storyboard off the running list BEFORE raising <c>Completed</c>.
        /// </summary>
        /// <remarks>
        /// This ordering is the whole reason a real clock is safe where a synchronous raise from
        /// <see cref="Begin"/> was not: WP7 code restarts a storyboard from its own handler, and
        /// from here that is an ordinary re-registration rather than unbounded recursion.
        /// </remarks>
        private void Finish()
        {
            AnimationClock.Remove(this);
            _filling = FillBehavior == FillBehavior.HoldEnd;
            RaiseCompleted();
        }

        private void CaptureBaseValues()
        {
            foreach (Timeline child in Children)
            {
                if (child == null) continue;
                object? target = ResolveTarget(child);
                if (target == null) continue;
                _baseValues[child] = AnimationClock.ReadValue(target, GetTargetProperty(child)?.Path);
            }
        }

        private void ApplyAt(TimeSpan time)
        {
            foreach (Timeline child in Children)
            {
                if (child == null) continue;

                TimeSpan local = time - (child.BeginTime ?? TimeSpan.Zero);
                if (local < TimeSpan.Zero) continue;

                object? target = ResolveTarget(child);
                if (target == null) continue;

                if (!_baseValues.TryGetValue(child, out object? baseValue))
                {
                    // The element may not have existed when Begin ran (a page still being laid
                    // out); capture on first sight rather than animating from nothing.
                    baseValue = AnimationClock.ReadValue(target, GetTargetProperty(child)?.Path);
                    _baseValues[child] = baseValue;
                }

                AnimationClock.Apply(child, local, target, baseValue);
            }
        }

        /// <summary>
        /// Finds the element a child animation points at: an explicit <see cref="SetTarget"/>
        /// first, then <c>Storyboard.TargetName</c> resolved against the name scope this
        /// storyboard was parsed in.
        /// </summary>
        /// <remarks>
        /// The scope matters. A storyboard almost always lives in a page's <c>Resources</c> and
        /// names elements in that page, so resolution has to start at the page — not at the
        /// storyboard, which has no children of its own. <c>XamlReader</c> records the owner when
        /// it parses one; a storyboard built in code has none and falls back to the app's root
        /// visual, which is the right answer for the single-page apps that do that.
        /// </remarks>
        private object? ResolveTarget(Timeline child)
        {
            if (ExplicitTargets.TryGetValue(child, out object? explicitTarget)) return explicitTarget;

            string? name = GetTargetName(child);
            if (string.IsNullOrEmpty(name)) return null;

            if (ScopeOwner is FrameworkElement owner)
            {
                object? found = owner.FindName(name!);
                if (found != null) return found;
            }

            try
            {
                if (WPR.WindowsCompability.Application.Current.RootVisual is FrameworkElement root)
                    return root.FindName(name!);
            }
            catch (Exception)
            {
                // Application.Current mints a singleton when there is none; never fatal here.
            }

            return null;
        }

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

        /// <summary>
        /// Points a child animation at an object directly, bypassing name resolution.
        /// </summary>
        public static void SetTarget(Timeline element, DependencyObject target)
        {
            if (element == null || target == null) return;
            ExplicitTargets.Remove(element);
            ExplicitTargets.Add(element, target);
        }

        public static void SetTargetProperty(Timeline element, PropertyPath path)
            => element?.SetValue(TargetPropertyProperty, path);
        public static PropertyPath? GetTargetProperty(Timeline element)
            => (PropertyPath?)element?.GetValue(TargetPropertyProperty);
    }
}
