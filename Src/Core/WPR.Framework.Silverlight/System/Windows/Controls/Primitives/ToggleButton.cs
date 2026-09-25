namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.Primitives.ToggleButton</c>.</summary>
    /// <remarks>
    /// Derives from <see cref="ButtonBase"/>, as Silverlight does, so it inherits <c>Click</c>
    /// rather than being a sibling that happens to look similar — a game hooking
    /// <c>ButtonBase::add_Click</c> on a toggle then resolves.
    /// </remarks>
    public class ToggleButton : ButtonBase
    {
        public static readonly DependencyProperty IsCheckedProperty =
            DependencyProperty.Register(nameof(IsChecked), typeof(bool?), typeof(ToggleButton),
                new PropertyMetadata((object?)false, OnIsCheckedChanged));

        /// <summary>
        /// Whether a third, indeterminate state is cycled through. False — the WP7 default — means
        /// the button flips between checked and unchecked only.
        /// </summary>
        public static readonly DependencyProperty IsThreeStateProperty =
            DependencyProperty.Register(nameof(IsThreeState), typeof(bool), typeof(ToggleButton),
                new PropertyMetadata((object)false));

        public bool? IsChecked
        {
            get => (bool?)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

        public bool IsThreeState
        {
            get => (bool)GetValue(IsThreeStateProperty)!;
            set => SetValue(IsThreeStateProperty, value);
        }

        public event RoutedEventHandler? Checked;
        public event RoutedEventHandler? Unchecked;
        public event RoutedEventHandler? Indeterminate;

        /// <summary>Cycles the state, which is what makes a tap on a toggle actually toggle.</summary>
        /// <remarks>
        /// Reachable now that input is routed: before, nothing ever pressed one of these, so the
        /// state only moved when a game set it in code.
        /// </remarks>
        protected override void OnClick()
        {
            base.OnClick();

            IsChecked = IsChecked switch
            {
                true => IsThreeState ? (bool?)null : false,
                false => true,
                _ => false,   // indeterminate -> unchecked
            };
        }

        private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ToggleButton toggle) return;

            var args = new RoutedEventArgs { OriginalSource = toggle };
            try
            {
                switch (toggle.IsChecked)
                {
                    case true: toggle.Checked?.Invoke(toggle, args); break;
                    case false: toggle.Unchecked?.Invoke(toggle, args); break;
                    default: toggle.Indeterminate?.Invoke(toggle, args); break;
                }
            }
            catch
            {
                // A game's state handler throwing must not escape a property set.
            }
        }
    }
}
