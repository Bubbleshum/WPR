using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Animation.Timeline</c>. Base for storyboards
    /// and per-property animations. We surface the handful of properties WP7 user
    /// code touches (<see cref="Duration"/>, <see cref="RepeatBehavior"/>,
    /// <see cref="AutoReverse"/>, <see cref="BeginTime"/>, <see cref="FillBehavior"/>,
    /// <see cref="Name"/>, <see cref="SpeedRatio"/>) and the
    /// <see cref="Completed"/> event. Nothing animates — but everything compiles
    /// and a Storyboard.Begin call fires Completed synchronously so logic gated
    /// on "the timer finished" keeps progressing.
    /// </summary>
    public class Timeline : DependencyObject
    {
        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register(nameof(Duration), typeof(Duration), typeof(Timeline),
                new PropertyMetadata(Duration.Automatic));

        public static readonly DependencyProperty RepeatBehaviorProperty =
            DependencyProperty.Register(nameof(RepeatBehavior), typeof(RepeatBehavior), typeof(Timeline),
                new PropertyMetadata(new RepeatBehavior(1)));

        public static readonly DependencyProperty AutoReverseProperty =
            DependencyProperty.Register(nameof(AutoReverse), typeof(bool), typeof(Timeline),
                new PropertyMetadata((object)false));

        public static readonly DependencyProperty BeginTimeProperty =
            DependencyProperty.Register(nameof(BeginTime), typeof(TimeSpan?), typeof(Timeline),
                new PropertyMetadata((object?)TimeSpan.Zero));

        public static readonly DependencyProperty FillBehaviorProperty =
            DependencyProperty.Register(nameof(FillBehavior), typeof(FillBehavior), typeof(Timeline),
                new PropertyMetadata(FillBehavior.HoldEnd));

        public static readonly DependencyProperty SpeedRatioProperty =
            DependencyProperty.Register(nameof(SpeedRatio), typeof(double), typeof(Timeline),
                new PropertyMetadata((object)1.0));

        public Duration Duration
        {
            get => (Duration)GetValue(DurationProperty)!;
            set => SetValue(DurationProperty, value);
        }

        public RepeatBehavior RepeatBehavior
        {
            get => (RepeatBehavior)GetValue(RepeatBehaviorProperty)!;
            set => SetValue(RepeatBehaviorProperty, value);
        }

        public bool AutoReverse
        {
            get => (bool)GetValue(AutoReverseProperty)!;
            set => SetValue(AutoReverseProperty, value);
        }

        public TimeSpan? BeginTime
        {
            get => (TimeSpan?)GetValue(BeginTimeProperty);
            set => SetValue(BeginTimeProperty, value);
        }

        public FillBehavior FillBehavior
        {
            get => (FillBehavior)GetValue(FillBehaviorProperty)!;
            set => SetValue(FillBehaviorProperty, value);
        }

        public double SpeedRatio
        {
            get => (double)GetValue(SpeedRatioProperty)!;
            set => SetValue(SpeedRatioProperty, value);
        }

        public string? Name { get; set; }

        public event EventHandler? Completed;

        /// <summary>Raised by <see cref="Storyboard.Begin"/> so user code that waits
        /// on a one-shot timer can complete. Animation-typed subclasses override to
        /// short-circuit nothing meaningful.</summary>
        protected internal void RaiseCompleted() => Completed?.Invoke(this, EventArgs.Empty);
    }
}
