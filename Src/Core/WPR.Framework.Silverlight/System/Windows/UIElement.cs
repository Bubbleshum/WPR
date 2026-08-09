using System;

namespace WPR.SilverlightCompability
{
    public class UIElement : DependencyObject
    {
        /// <summary>
        /// Parent in the Silverlight visual tree. Set by Panel.Children / ContentControl
        /// when this element is hosted. Invalidation walks up via this pointer.
        /// </summary>
        public UIElement? Parent { get; private set; }

        internal void SetParent(UIElement? newParent)
        {
            if (newParent != null && Parent != null && !ReferenceEquals(Parent, newParent))
            {
                throw new InvalidOperationException(
                    "Element already has a parent. Remove it from its current parent before re-attaching.");
            }
            Parent = newParent;
        }

        /// <summary>
        /// Fires when this element transitions from measure-valid to measure-invalid.
        /// The host (PhoneApplicationFrameView) listens on the root page so any descendant
        /// invalidation triggers a repaint.
        /// </summary>
        internal event EventHandler? MeasureInvalidatedEvent;

        /// <summary>
        /// Silverlight routed focus events. Declared so app constructors that subscribe
        /// (e.g. SparkApplicationXaml's <c>App</c> in Assassin's Creed Pirates) can JIT
        /// without <c>MissingMethodException</c> on the CLR-generated <c>add_GotFocus</c>
        /// / <c>add_LostFocus</c> accessors. We do not currently raise these — WPR's host
        /// has no focus-tracking model — so handlers stay registered but never fire.
        /// Adding real focus dispatch would mean threading it through the input pipeline
        /// in <see cref="PhoneApplicationFrameView"/>; left as a future enhancement.
        /// </summary>
#pragma warning disable CS0067 // intentionally never raised; see XML comment above
        public event RoutedEventHandler? GotFocus;
        public event RoutedEventHandler? LostFocus;
#pragma warning restore CS0067

        public static readonly DependencyProperty VisibilityProperty =
            DependencyProperty.Register(nameof(Visibility), typeof(Visibility), typeof(UIElement),
                new PropertyMetadata(Visibility.Visible));

        public static readonly DependencyProperty OpacityProperty =
            DependencyProperty.Register(nameof(Opacity), typeof(double), typeof(UIElement),
                new PropertyMetadata(1.0));

        public static readonly DependencyProperty IsHitTestVisibleProperty =
            DependencyProperty.Register(nameof(IsHitTestVisible), typeof(bool), typeof(UIElement),
                new PropertyMetadata(true));

        public static readonly DependencyProperty RenderTransformProperty =
            DependencyProperty.Register(nameof(RenderTransform), typeof(Transform), typeof(UIElement),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty RenderTransformOriginProperty =
            DependencyProperty.Register(nameof(RenderTransformOrigin), typeof(Point), typeof(UIElement),
                new PropertyMetadata(new Point(0, 0)));

        public static readonly DependencyProperty CacheModeProperty =
            DependencyProperty.Register(nameof(CacheMode), typeof(CacheMode), typeof(UIElement),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty ClipProperty =
            DependencyProperty.Register(nameof(Clip), typeof(Geometry), typeof(UIElement),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty ProjectionProperty =
            DependencyProperty.Register(nameof(Projection), typeof(Projection), typeof(UIElement),
                new PropertyMetadata((object?)null));

        public Visibility Visibility
        {
            get => (Visibility)GetValue(VisibilityProperty)!;
            set => SetValue(VisibilityProperty, value);
        }

        public double Opacity
        {
            get => (double)GetValue(OpacityProperty)!;
            set => SetValue(OpacityProperty, value);
        }

        public bool IsHitTestVisible
        {
            get => (bool)GetValue(IsHitTestVisibleProperty)!;
            set => SetValue(IsHitTestVisibleProperty, value);
        }

        public Transform? RenderTransform
        {
            get => (Transform?)GetValue(RenderTransformProperty);
            set => SetValue(RenderTransformProperty, value);
        }

        public Point RenderTransformOrigin
        {
            get => (Point)GetValue(RenderTransformOriginProperty)!;
            set => SetValue(RenderTransformOriginProperty, value);
        }

        public CacheMode? CacheMode
        {
            get => (CacheMode?)GetValue(CacheModeProperty);
            set => SetValue(CacheModeProperty, value);
        }

        public Geometry? Clip
        {
            get => (Geometry?)GetValue(ClipProperty);
            set => SetValue(ClipProperty, value);
        }

        public Projection? Projection
        {
            get => (Projection?)GetValue(ProjectionProperty);
            set => SetValue(ProjectionProperty, value);
        }

        public Brush? OpacityMask { get; set; }

        // Manipulation events: WP Toolkit (Panorama, Pivot, LongListSelector) hooks these.
        // The renderer doesn't generate manipulation deltas yet — declared so the
        // CLR-emitted add_/remove_ accessors resolve.
#pragma warning disable CS0067
        public event EventHandler<ManipulationStartedEventArgs>? ManipulationStarted;
        public event EventHandler<ManipulationDeltaEventArgs>? ManipulationDelta;
        public event EventHandler<ManipulationCompletedEventArgs>? ManipulationCompleted;
        public event MouseButtonEventHandler? MouseLeftButtonDown;
        public event MouseButtonEventHandler? MouseLeftButtonUp;
        public event KeyEventHandler? KeyDown;
        public event KeyEventHandler? KeyUp;
#pragma warning restore CS0067

        /// <summary>Stub for <c>UIElement.ReleaseMouseCapture()</c> — no capture model yet.</summary>
        public void ReleaseMouseCapture() { }

        /// <summary>Stub for <c>UIElement.CaptureMouse()</c>.</summary>
        public bool CaptureMouse() => false;

        /// <summary>Forces a layout pass on the subtree. Our renderer drives layout
        /// from the host; this method exists so user code (Panorama recomputes its
        /// item slots after measure) can request a sync update — we satisfy it with
        /// a Measure+Arrange of this element at its current ArrangedRect.</summary>
        public void UpdateLayout()
        {
            // Best-effort: re-measure against last arrange size (or infinite if not yet arranged).
            Size avail = ArrangedRect.Width > 0 && ArrangedRect.Height > 0
                ? new Size(ArrangedRect.Width, ArrangedRect.Height)
                : new Size(double.PositiveInfinity, double.PositiveInfinity);
            Measure(avail);
            if (ArrangedRect.Width > 0 || ArrangedRect.Height > 0)
                Arrange(ArrangedRect);
        }

        /// <summary>
        /// Returns a transform that maps points from this element's coords to
        /// <paramref name="relativeTo"/>'s coords. We don't track transforms yet;
        /// returning the identity is correct as long as no element has a non-identity
        /// RenderTransform applied — close enough for the WP Toolkit code paths that
        /// use this for hit-testing.
        /// </summary>
        public GeneralTransform TransformToVisual(UIElement? relativeTo) => new IdentityTransform();

        private sealed class IdentityTransform : GeneralTransform
        {
            public override Point Transform(Point point) => point;
            public override GeneralTransform? Inverse => this;
        }

        public Size DesiredSize { get; private set; }

        /// <summary>
        /// The rect this element was arranged into, in its parent's coordinate space.
        /// Set by Arrange; consumed by the renderer.
        /// </summary>
        public Rect ArrangedRect { get; private set; }

        public bool IsMeasureValid { get; private set; }
        public bool IsArrangeValid { get; private set; }

        public void Measure(Size availableSize)
        {
            DesiredSize = MeasureCore(availableSize);
            IsMeasureValid = true;
        }

        public void Arrange(Rect finalRect)
        {
            // Let derived classes (FrameworkElement) reshape the rect according
            // to alignment/margin first; default keeps it as the parent's slot.
            Rect resolved = ResolveArrangeRect(finalRect);
            ArrangedRect = resolved;
            ArrangeCore(resolved);
            IsArrangeValid = true;
        }

        /// <summary>Reshape the parent's slot into this element's final placement.
        /// Default keeps the slot verbatim; <see cref="FrameworkElement"/> overrides
        /// to apply HorizontalAlignment / VerticalAlignment / Margin / fixed Width/Height.</summary>
        protected virtual Rect ResolveArrangeRect(Rect slot) => slot;

        public void InvalidateMeasure()
        {
            if (!IsMeasureValid) return; // already invalid; don't re-walk
            IsMeasureValid = false;
            MeasureInvalidatedEvent?.Invoke(this, EventArgs.Empty);
            Parent?.InvalidateMeasure();
        }

        public void InvalidateArrange()
        {
            IsArrangeValid = false;
        }

        protected virtual Size MeasureCore(Size availableSize) => Size.Empty;

        protected virtual void ArrangeCore(Rect finalRect)
        {
            // No-op at UIElement layer; FrameworkElement provides real arrangement.
        }

        protected static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return value;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
