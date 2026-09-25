using WPR.SilverlightCompability;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Shell.SystemTray</c> — the WP7 status bar along the top of
    /// the screen, and the attached properties a page uses to hide or tint it.
    /// </summary>
    /// <remarks>
    /// <para>There is no system tray to show, so these are stored and ignored. The type exists
    /// because the attached property is set <b>in XAML</b> rather than in IL — nearly every WP7
    /// page carries <c>shell:SystemTray.IsVisible="False"</c>, including both Cut the Rope
    /// titles — and an unresolvable attached property makes the XAML reader throw while parsing
    /// the page. A static refcheck of the game's assemblies will NOT find this: nothing in the
    /// IL names it.</para>
    ///
    /// <para>Ignoring it is also right rather than merely convenient: a game asking to hide the
    /// status bar wants the full screen, which is what it already has here.</para>
    /// </remarks>
    public static class SystemTray
    {
        public static readonly DependencyProperty IsVisibleProperty =
            DependencyProperty.RegisterAttached("IsVisible", typeof(bool), typeof(SystemTray),
                new PropertyMetadata(false));

        public static readonly DependencyProperty OpacityProperty =
            DependencyProperty.RegisterAttached("Opacity", typeof(double), typeof(SystemTray),
                new PropertyMetadata(1.0));

        public static readonly DependencyProperty BackgroundColorProperty =
            DependencyProperty.RegisterAttached("BackgroundColor", typeof(Color), typeof(SystemTray),
                new PropertyMetadata(default(Color)));

        public static readonly DependencyProperty ForegroundColorProperty =
            DependencyProperty.RegisterAttached("ForegroundColor", typeof(Color), typeof(SystemTray),
                new PropertyMetadata(default(Color)));

        public static readonly DependencyProperty IsIndeterminateProperty =
            DependencyProperty.RegisterAttached("IsIndeterminate", typeof(bool), typeof(SystemTray),
                new PropertyMetadata(false));

        public static bool GetIsVisible(DependencyObject element) =>
            element != null && (bool)(element.GetValue(IsVisibleProperty) ?? false);

        public static void SetIsVisible(DependencyObject element, bool value) =>
            element?.SetValue(IsVisibleProperty, value);

        public static double GetOpacity(DependencyObject element) =>
            element == null ? 1.0 : (double)(element.GetValue(OpacityProperty) ?? 1.0);

        public static void SetOpacity(DependencyObject element, double value) =>
            element?.SetValue(OpacityProperty, value);

        public static Color GetBackgroundColor(DependencyObject element) =>
            element == null ? default : (Color)(element.GetValue(BackgroundColorProperty) ?? default(Color));

        public static void SetBackgroundColor(DependencyObject element, Color value) =>
            element?.SetValue(BackgroundColorProperty, value);

        public static Color GetForegroundColor(DependencyObject element) =>
            element == null ? default : (Color)(element.GetValue(ForegroundColorProperty) ?? default(Color));

        public static void SetForegroundColor(DependencyObject element, Color value) =>
            element?.SetValue(ForegroundColorProperty, value);

        public static bool GetIsIndeterminate(DependencyObject element) =>
            element != null && (bool)(element.GetValue(IsIndeterminateProperty) ?? false);

        public static void SetIsIndeterminate(DependencyObject element, bool value) =>
            element?.SetValue(IsIndeterminateProperty, value);

        /// <summary>Attached <c>ProgressIndicator</c>; stored and ignored.</summary>
        public static object? GetProgressIndicator(DependencyObject element) => null;

        public static void SetProgressIndicator(DependencyObject element, object? value)
        {
        }
    }
}
