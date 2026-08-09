using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.VisualStateManager</c>. The single touched API is
    /// the static <see cref="GoToState"/>; our implementation returns <c>false</c>
    /// to signal "no transition occurred", which user code typically treats as
    /// "OK, we'll skip the animation" without erroring.
    /// </summary>
    public class VisualStateManager : DependencyObject
    {
        public static readonly DependencyProperty VisualStateGroupsProperty =
            DependencyProperty.RegisterAttached("VisualStateGroups", typeof(IList<VisualStateGroup>),
                typeof(VisualStateManager), new PropertyMetadata((object?)null));

        public static IList<VisualStateGroup>? GetVisualStateGroups(DependencyObject obj)
            => (IList<VisualStateGroup>?)obj?.GetValue(VisualStateGroupsProperty);

        public static void SetVisualStateGroups(DependencyObject obj, IList<VisualStateGroup> value)
            => obj?.SetValue(VisualStateGroupsProperty, value);

        public static bool GoToState(Control control, string stateName, bool useTransitions)
            => false;
    }
}
