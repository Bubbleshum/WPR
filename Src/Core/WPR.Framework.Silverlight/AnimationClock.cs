using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Runs Silverlight storyboards: advances their time once per host frame, writes the
    /// animated values onto their targets, and raises <c>Completed</c> when they finish.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists at all.</b> <see cref="Storyboard.Begin"/> used to be a no-op and
    /// <c>Completed</c> was never raised, on the reasoning that a fake synchronous raise would be
    /// worse than none — WP7 code routinely restarts a storyboard from its own handler, so a
    /// synchronous raise recurses until the stack runs out. That reasoning is right and this does
    /// not change it: <c>Completed</c> is raised from the frame pump, <b>after</b> the storyboard
    /// has been taken off the running list, so a handler that calls <c>Begin</c> again simply
    /// re-registers it.</para>
    ///
    /// <para><b>The cost of not having it was whole games.</b> Sid Meier's Pirates! navigates off
    /// its logo page only from <c>FiraxisLogoAnimation.Completed</c>, which is chained off
    /// <c>LogoAnimation.Completed</c> — there is no timer and no tap fallback, so with no clock it
    /// sat on the 2K logo for ever with a completely clean log. "Renders correctly, zero
    /// exceptions, never advances" is what a missing timeline engine looks like.</para>
    ///
    /// <para><b>Pumped from the mixed-mode host</b> (<c>MixedModeGame.Update</c>), which is the
    /// only frame loop a mixed-mode title has. The Avalonia Silverlight host has its own render
    /// loop and does <b>not</b> call this yet, so storyboards still do nothing in pure-Silverlight
    /// titles; wiring that up is a separate change and wants its own testing.</para>
    ///
    /// <para><b>What it animates:</b> <see cref="DoubleAnimation"/>,
    /// <see cref="DoubleAnimationUsingKeyFrames"/> (linear and eased) and
    /// <see cref="ObjectAnimationUsingKeyFrames"/> (discrete). Colour, point and transform-group
    /// animations are not modelled — a storyboard containing one still runs and still completes,
    /// it just does not move that property, which keeps the game's logic progressing.</para>
    /// </remarks>
    internal static class AnimationClock
    {
        private static readonly object Gate = new object();
        private static readonly List<Storyboard> Running = new List<Storyboard>();

        internal static void Start(Storyboard storyboard)
        {
            if (storyboard == null) return;
            lock (Gate)
            {
                if (!Running.Contains(storyboard)) Running.Add(storyboard);
            }
        }

        internal static void Remove(Storyboard storyboard)
        {
            if (storyboard == null) return;
            lock (Gate) Running.Remove(storyboard);
        }

        internal static bool IsRunning(Storyboard storyboard)
        {
            lock (Gate) return Running.Contains(storyboard);
        }

        /// <summary>
        /// Advances every running storyboard by <paramref name="elapsed"/>. Called once per host
        /// frame.
        /// </summary>
        /// <remarks>
        /// Iterates a snapshot because a <c>Completed</c> handler routinely starts or stops other
        /// storyboards — and, in the chained case this was built for, the very next one.
        /// </remarks>
        internal static void Tick(TimeSpan elapsed)
        {
            Storyboard[] snapshot;
            lock (Gate)
            {
                if (Running.Count == 0) return;
                snapshot = Running.ToArray();
            }

            foreach (Storyboard sb in snapshot)
            {
                try
                {
                    sb.Advance(elapsed);
                }
                catch (Exception ex)
                {
                    // One bad storyboard must not stop the others, and must not take the frame
                    // down: this runs inside the game's Update.
                    System.Diagnostics.Trace.WriteLine(
                        "[wpr-anim] advancing a storyboard threw " + ex.GetType().Name + ": " + ex.Message);
                    Remove(sb);
                }
            }
        }

        /// <summary>Drops every running storyboard. Called at game teardown.</summary>
        /// <remarks>
        /// The list holds game objects, and the host outlives a game on the desktop — leaving
        /// them registered would pin the game's collectible load context and keep ticking a dead
        /// page's animations into the next launch.
        /// </remarks>
        internal static void ResetForNewLaunch()
        {
            lock (Gate) Running.Clear();
        }

        // -----------------------------------------------------------------------------
        // Durations
        // -----------------------------------------------------------------------------

        /// <summary>
        /// How long <paramref name="timeline"/> runs for, resolving Silverlight's "Automatic"
        /// from whatever it contains.
        /// </summary>
        internal static TimeSpan NaturalDuration(Timeline timeline)
        {
            if (timeline == null) return TimeSpan.Zero;

            Duration declared = timeline.Duration;
            if (declared.HasTimeSpan) return declared.TimeSpan;
            if (declared == Duration.Forever) return TimeSpan.MaxValue;

            switch (timeline)
            {
                case Storyboard storyboard:
                {
                    TimeSpan longest = TimeSpan.Zero;
                    foreach (Timeline child in storyboard.Children)
                    {
                        if (child == null) continue;
                        TimeSpan childEnd = (child.BeginTime ?? TimeSpan.Zero) + NaturalDuration(child);
                        if (childEnd > longest) longest = childEnd;
                    }
                    return longest;
                }

                case DoubleAnimationUsingKeyFrames doubleKeys:
                    return LastKeyTime(doubleKeys);

                case ObjectAnimationUsingKeyFrames objectKeys:
                    return LastKeyTime(objectKeys);

                // Silverlight's default for a From/To animation with no Duration is one second.
                case DoubleAnimation _:
                    return TimeSpan.FromSeconds(1);

                default:
                    return TimeSpan.Zero;
            }
        }

        private static TimeSpan LastKeyTime(DoubleAnimationUsingKeyFrames animation)
        {
            TimeSpan last = TimeSpan.Zero;
            foreach (DoubleKeyFrame frame in animation.KeyFrames)
            {
                if (frame == null) continue;
                TimeSpan t = frame.KeyTime.TimeSpan;
                if (t > last) last = t;
            }
            return last;
        }

        private static TimeSpan LastKeyTime(ObjectAnimationUsingKeyFrames animation)
        {
            TimeSpan last = TimeSpan.Zero;
            foreach (ObjectKeyFrame frame in animation.KeyFrames)
            {
                if (frame == null) continue;
                TimeSpan t = frame.KeyTime.TimeSpan;
                if (t > last) last = t;
            }
            return last;
        }

        // -----------------------------------------------------------------------------
        // Applying a value
        // -----------------------------------------------------------------------------

        /// <summary>
        /// Writes <paramref name="child"/>'s value at <paramref name="localTime"/> onto its
        /// target. <paramref name="baseValue"/> is the target property's value when the
        /// storyboard began, which is what Silverlight interpolates the first key-frame segment
        /// from.
        /// </summary>
        internal static void Apply(Timeline child, TimeSpan localTime, object? target, object? baseValue)
        {
            if (child == null || target == null) return;

            string? path = Storyboard.GetTargetProperty(child)?.Path;
            if (string.IsNullOrEmpty(path)) return;

            object? value = Evaluate(child, localTime, baseValue);
            if (value == null) return;

            SetValue(target, path!, value);
        }

        private static object? Evaluate(Timeline child, TimeSpan t, object? baseValue)
        {
            switch (child)
            {
                case DoubleAnimationUsingKeyFrames keys:
                    return EvaluateDoubleKeyFrames(keys, t, baseValue);

                case ObjectAnimationUsingKeyFrames objectKeys:
                {
                    object? current = null;
                    foreach (ObjectKeyFrame frame in Sorted(objectKeys.KeyFrames))
                    {
                        if (frame.KeyTime.TimeSpan > t) break;
                        current = frame.Value;   // discrete: the last one reached wins
                    }
                    return current;
                }

                case DoubleAnimation animation:
                {
                    double from = animation.From ?? ToDouble(baseValue);
                    double to = animation.To
                        ?? (animation.By.HasValue ? from + animation.By.Value : from);
                    TimeSpan duration = NaturalDuration(animation);
                    double progress = duration <= TimeSpan.Zero
                        ? 1.0
                        : Math.Min(1.0, t.TotalSeconds / duration.TotalSeconds);
                    return from + (to - from) * Ease(animation.EasingFunction, progress);
                }

                default:
                    return null;
            }
        }

        private static object? EvaluateDoubleKeyFrames(
            DoubleAnimationUsingKeyFrames animation, TimeSpan t, object? baseValue)
        {
            List<DoubleKeyFrame> frames = Sorted(animation.KeyFrames);
            if (frames.Count == 0) return null;

            DoubleKeyFrame? previous = null;
            foreach (DoubleKeyFrame frame in frames)
            {
                if (frame.KeyTime.TimeSpan >= t)
                {
                    TimeSpan segmentStart = previous?.KeyTime.TimeSpan ?? TimeSpan.Zero;
                    // No previous frame means the segment runs from the property's value when the
                    // storyboard began — Silverlight's rule, and the difference between a logo
                    // that holds at full opacity for two seconds and one that fades in from zero.
                    double startValue = previous?.Value ?? ToDouble(baseValue);
                    TimeSpan span = frame.KeyTime.TimeSpan - segmentStart;
                    double progress = span <= TimeSpan.Zero
                        ? 1.0
                        : (t - segmentStart).TotalSeconds / span.TotalSeconds;
                    if (progress < 0) progress = 0;
                    if (progress > 1) progress = 1;

                    IEasingFunction? easing = (frame as EasingDoubleKeyFrame)?.EasingFunction;
                    return startValue + (frame.Value - startValue) * Ease(easing, progress);
                }
                previous = frame;
            }

            return frames[frames.Count - 1].Value;   // past the end: hold the last value
        }

        private static double Ease(IEasingFunction? easing, double progress)
            => easing is EasingFunctionBase fn ? fn.Ease(progress) : progress;

        private static List<T> Sorted<T>(IEnumerable<T> frames) where T : DependencyObject
        {
            var list = new List<T>();
            foreach (T f in frames) if (f != null) list.Add(f);
            list.Sort((a, b) => KeyTimeOf(a).CompareTo(KeyTimeOf(b)));
            return list;
        }

        private static TimeSpan KeyTimeOf(DependencyObject frame) => frame switch
        {
            DoubleKeyFrame d => d.KeyTime.TimeSpan,
            ObjectKeyFrame o => o.KeyTime.TimeSpan,
            _ => TimeSpan.Zero,
        };

        private static double ToDouble(object? value)
        {
            if (value is double d) return d;
            if (value is IConvertible c)
            {
                try { return c.ToDouble(CultureInfo.InvariantCulture); }
                catch (Exception) { return 0.0; }
            }
            return 0.0;
        }

        // -----------------------------------------------------------------------------
        // Property paths
        // -----------------------------------------------------------------------------

        /// <summary>
        /// Reads the current value of a storyboard target property, so the first key-frame
        /// segment has something to interpolate from.
        /// </summary>
        internal static object? ReadValue(object? target, string? path)
        {
            if (target == null || string.IsNullOrEmpty(path)) return null;
            try
            {
                object? owningObject = WalkToOwner(target, path!);
                if (owningObject == null) return null;

                (string owner, string member) = SplitPath(path!);
                if (TryAttached(owningObject, owner, member, read: true, null, out object? attached)) return attached;
                PropertyInfo? property = owningObject.GetType().GetProperty(
                    member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                return property?.GetValue(owningObject);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SetValue(object target, string path, object value)
        {
            try
            {
                object? owningObject = WalkToOwner(target, path);
                if (owningObject == null)
                {
                    ReportUnsupported(path);
                    return;
                }

                (string owner, string member) = SplitPath(path);
                if (TryAttached(owningObject, owner, member, read: false, value, out _)) return;

                PropertyInfo? property = owningObject.GetType().GetProperty(
                    member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (property == null || !property.CanWrite)
                {
                    ReportUnsupported(path);
                    return;
                }

                target = owningObject;

                Type wanted = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                object? converted = value;
                if (!wanted.IsInstanceOfType(value))
                {
                    if (wanted.IsEnum && value is IConvertible)
                        converted = Enum.ToObject(wanted, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    else if (value is IConvertible)
                        converted = Convert.ChangeType(value, wanted, CultureInfo.InvariantCulture);
                }
                property.SetValue(target, converted);
            }
            catch (Exception)
            {
                ReportUnsupported(path);
            }
        }

        /// <summary>
        /// Splits <c>"(UIElement.Opacity)"</c> into <c>("UIElement", "Opacity")</c>, and a bare
        /// <c>"Opacity"</c> into <c>("", "Opacity")</c>.
        /// </summary>
        /// <remarks>
        /// Only the LAST segment is used as the member. A Silverlight target property can be a
        /// whole chain — <c>"(UIElement.RenderTransform).(TranslateTransform.X)"</c> — and
        /// resolving those properly needs a real property-path walker; taking the last segment
        /// gets the common single-hop case right and leaves the chain case to report itself.
        /// </remarks>
        /// <summary>
        /// Follows every segment of <paramref name="path"/> except the last, so the caller reads or
        /// writes the final property on the object that actually owns it.
        /// </summary>
        /// <remarks>
        /// <para><b>A storyboard target path is not always a property of the element.</b> The
        /// commonest multi-segment path in WP7 XAML is
        /// <c>(UIElement.RenderTransform).(CompositeTransform.TranslateX)</c>, which means "follow
        /// the element's RenderTransform, then set TranslateX on THAT". Resolving only the last
        /// segment — which is what <see cref="SplitPath"/> does on its own — looked for
        /// <c>TranslateX</c> on the element, found nothing, and reported the whole animation
        /// unsupported. Every slide, scale and rotate driven by a storyboard therefore did nothing
        /// at all.</para>
        ///
        /// <para>Both spellings are handled: the parenthesised
        /// <c>(Owner.Property)</c> form and a bare dotted <c>RenderTransform.ScaleX</c>.</para>
        ///
        /// <para>Answers null when a leading segment resolves to null — an element whose
        /// RenderTransform was never set, which is a state to skip rather than a fault.</para>
        /// </remarks>
        private static object? WalkToOwner(object target, string path)
        {
            List<string> segments = SplitSegments(path);
            if (segments.Count <= 1) return target;

            object? current = target;
            for (int i = 0; i < segments.Count - 1 && current != null; i++)
            {
                string name = segments[i];
                int dot = name.LastIndexOf('.');
                if (dot >= 0) name = name.Substring(dot + 1);

                PropertyInfo? step = current.GetType().GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (step == null) return null;

                current = step.GetValue(current);
            }

            return current;
        }

        /// <summary>
        /// Splits a property path into its segments: each parenthesised group is one segment, and
        /// any unparenthesised remainder is split on dots.
        /// </summary>
        private static List<string> SplitSegments(string path)
        {
            var segments = new List<string>();
            string trimmed = path.Trim();

            int i = 0;
            while (i < trimmed.Length)
            {
                if (trimmed[i] == '(')
                {
                    int close = trimmed.IndexOf(')', i);
                    if (close < 0)
                    {
                        segments.Add(trimmed.Substring(i + 1));
                        break;
                    }

                    segments.Add(trimmed.Substring(i + 1, close - i - 1));
                    i = close + 1;
                    if (i < trimmed.Length && trimmed[i] == '.') i++;
                    continue;
                }

                int nextDot = trimmed.IndexOf('.', i);
                int nextParen = trimmed.IndexOf('(', i);

                // A dot that belongs to THIS segment, not one separating it from a parenthesised
                // one that follows.
                if (nextDot >= 0 && (nextParen < 0 || nextDot < nextParen))
                {
                    segments.Add(trimmed.Substring(i, nextDot - i));
                    i = nextDot + 1;
                }
                else if (nextParen >= 0)
                {
                    if (nextParen > i) segments.Add(trimmed.Substring(i, nextParen - i));
                    i = nextParen;
                }
                else
                {
                    segments.Add(trimmed.Substring(i));
                    break;
                }
            }

            segments.RemoveAll(string.IsNullOrWhiteSpace);
            return segments;
        }

        private static (string Owner, string Member) SplitPath(string path)
        {
            string trimmed = path.Trim();
            int lastGroup = trimmed.LastIndexOf('(');
            if (lastGroup >= 0)
            {
                int close = trimmed.IndexOf(')', lastGroup);
                trimmed = close > lastGroup
                    ? trimmed.Substring(lastGroup + 1, close - lastGroup - 1)
                    : trimmed.Substring(lastGroup + 1);
            }

            int dot = trimmed.LastIndexOf('.');
            return dot < 0
                ? (string.Empty, trimmed)
                : (trimmed.Substring(0, dot), trimmed.Substring(dot + 1));
        }

        /// <summary>
        /// The attached properties a storyboard actually targets. Reflection cannot reach these —
        /// they are statics on another type, not instance properties on the element.
        /// </summary>
        private static bool TryAttached(
            object target, string owner, string member, bool read, object? value, out object? result)
        {
            result = null;
            if (!string.Equals(owner, "Canvas", StringComparison.Ordinal)) return false;
            if (target is not UIElement element) return false;

            switch (member)
            {
                case "Left":
                    if (read) { result = Canvas.GetLeft(element); return true; }
                    Canvas.SetLeft(element, ToDouble(value));
                    return true;
                case "Top":
                    if (read) { result = Canvas.GetTop(element); return true; }
                    Canvas.SetTop(element, ToDouble(value));
                    return true;
                case "ZIndex":
                    if (read) { result = Canvas.GetZIndex(element); return true; }
                    Canvas.SetZIndex(element, (int)ToDouble(value));
                    return true;
                default:
                    return false;
            }
        }

        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Names an unanimatable target property once. A storyboard still runs and still
        /// completes when this fires — the game's logic keeps progressing and only the movement
        /// is missing, which is worth being able to tell apart from a clock that is not running.
        /// </summary>
        private static void ReportUnsupported(string path)
        {
            lock (Reported)
            {
                if (!Reported.Add(path)) return;
            }
            System.Diagnostics.Trace.WriteLine(
                "[wpr-anim] target property '" + path + "' is not animatable here; the storyboard " +
                "still runs and still completes.");
        }
    }
}
