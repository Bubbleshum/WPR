namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.RadioButton</c>.</summary>
    /// <remarks>
    /// <para>A checkable button that clears its siblings. Galactic Reign's <c>MenuPage</c> names it
    /// and, without the type, failed to load the whole page.</para>
    ///
    /// <para><b>Grouping is by <see cref="GroupName"/> within the same parent</b>, which is
    /// Silverlight's rule: buttons with no group name are grouped by their container, and buttons
    /// with one are grouped across the tree by that name. Both are handled below, because a menu
    /// commonly uses the first and a settings page the second.</para>
    /// </remarks>
    public class RadioButton : ToggleButton
    {
        public static readonly DependencyProperty GroupNameProperty =
            DependencyProperty.Register(nameof(GroupName), typeof(string), typeof(RadioButton),
                new PropertyMetadata((object?)null));

        public string? GroupName
        {
            get => (string?)GetValue(GroupNameProperty);
            set => SetValue(GroupNameProperty, value);
        }

        /// <summary>
        /// A radio button checks on click and never unchecks itself — only another button in its
        /// group can clear it.
        /// </summary>
        protected override void OnClick()
        {
            // Deliberately NOT base.OnClick(): ToggleButton would cycle this back off on a second
            // tap, and a radio group with nothing selected is not a state the control can reach.
            if (IsChecked != true) IsChecked = true;
            else ClearSiblings();
        }

        /// <summary>Unchecks every other button in this one's group.</summary>
        private void ClearSiblings()
        {
            // ((UIElement)this).Parent, because FrameworkElement hides Parent as a
            // DependencyObject — Silverlight's own signature — and the visual-tree link is on
            // UIElement.
            UIElement? scope = ((UIElement)this).Parent;

            // A named group spans the page rather than the immediate container, so search from as
            // high as we can reach.
            if (!string.IsNullOrEmpty(GroupName))
            {
                for (UIElement? el = scope; el != null; el = el.Parent) scope = el;
            }

            if (scope == null) return;
            Clear(scope, 0);
        }

        private void Clear(UIElement element, int depth)
        {
            if (depth > 32) return;

            if (element is RadioButton other
                && !ReferenceEquals(other, this)
                && string.Equals(other.GroupName, GroupName, System.StringComparison.Ordinal)
                && other.IsChecked == true)
            {
                other.IsChecked = false;
            }

            switch (element)
            {
                case Panel panel:
                    foreach (UIElement child in panel.Children) Clear(child, depth + 1);
                    break;

                case Border border when border.Child != null:
                    Clear(border.Child, depth + 1);
                    break;

                case ContentControl content when content.Presenter != null:
                    Clear(content.Presenter, depth + 1);
                    break;
            }
        }

        /// <summary>Checking a radio button clears the rest of its group.</summary>
        internal void NotifyChecked()
        {
            if (IsChecked == true) ClearSiblings();
        }
    }
}
