using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Minimal stand-in for a Control base — the WP TextBox derives from Control, but
    /// our framework collapses Control to FrameworkElement since most of Control's chrome
    /// (template, focus visual) isn't relevant on WPR.</summary>
    public class Control : FrameworkElement
    {
        // Alias of FrameworkElement.BackgroundProperty so user IL calling our
        // Control.set_Background on a non-Control instance (Minesweeper's
        // Panorama IL does exactly that, inheriting the pre-patch Silverlight
        // Panorama:Control chain) writes to the same DP slot that the
        // renderer reads via Panel.Background. The static field stays so
        // ldsfld Control::BackgroundProperty still resolves; Background is
        // the FE-inherited CLR property.
        public static readonly DependencyProperty BackgroundProperty = FrameworkElement.BackgroundProperty;

        public static readonly DependencyProperty BorderBrushProperty =
            DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(Control),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty BorderThicknessProperty =
            DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(Control),
                new PropertyMetadata(new Thickness(0)));

        public static readonly DependencyProperty PaddingProperty =
            DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(Control),
                new PropertyMetadata(new Thickness(0)));

        // Real WP/Silverlight expose IsEnabled on Control (it bubbles
        // visually through templates as state-group activation). Our renderer
        // doesn't honour it visually yet, but games still read/write it from
        // code (Minesweeper.HelpOptionsPage.RefreshSettings toggles per-option
        // buttons by IsEnabled). Without the DP, the user-IL `set_IsEnabled`
        // call raises MissingMethodException at the first call site, taking
        // down navigation entirely.
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.Register(nameof(IsEnabled), typeof(bool), typeof(Control),
                new PropertyMetadata((object)true));

        // Background is inherited from FrameworkElement — no shadow here.

        public Brush? BorderBrush
        {
            get => (Brush?)GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        public Thickness BorderThickness
        {
            get => (Thickness)GetValue(BorderThicknessProperty)!;
            set => SetValue(BorderThicknessProperty, value);
        }

        public Thickness Padding
        {
            get => (Thickness)GetValue(PaddingProperty)!;
            set => SetValue(PaddingProperty, value);
        }

        public bool IsEnabled
        {
            get => (bool)GetValue(IsEnabledProperty)!;
            set => SetValue(IsEnabledProperty, value);
        }

        // Silverlight's templated-control plumbing — every WP toolkit control sets
        // DefaultStyleKey = typeof(self) in its ctor so a generic.xaml-style lookup
        // can find the matching ControlTemplate. We don't apply templates.
        //
        // The setter is intentionally a NO-OP that never touches `this` — the
        // patched WP Toolkit IL emits `call Control::set_DefaultStyleKey` with
        // `this` of types (Panorama, Pivot, …) whose post-patch base chain bypasses
        // Control (TemplatedItemsControl<T> → ItemsControl → StackPanel → Panel).
        // Storing to a backing field on this object would corrupt memory in that
        // case; throwing away the value is safe.
        public object? DefaultStyleKey
        {
            set { /* no-op on purpose, see comment above */ }
        }
    }
}
