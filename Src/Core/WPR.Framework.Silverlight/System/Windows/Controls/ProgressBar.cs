namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.ProgressBar</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>It draws nothing yet, and that is not why it exists.</b> A missing type is
    /// resolved when the method naming it is compiled, so one unresolvable reference kills the
    /// whole method — and in a WP7 loading screen that method is the one that builds the screen.
    /// Rabbids Go Phone is the reference case: <c>RabbidsHD.Screens.Loading.LoadContent</c>
    /// creates an indeterminate ProgressBar for its spinner, so tapping <i>My Rabbid</i> threw
    /// <c>TypeLoadException</c> out of the game's own <c>GameTimer</c> update, the screen was
    /// never added, and the menu simply sat there. Nothing on screen said so.</para>
    ///
    /// <para>The software rasteriser draws the WP7 bar or indeterminate dots through
    /// <c>WpChrome</c>, for this type and for <c>Microsoft.Phone.Controls.PerformanceProgressBar</c>.</para>
    ///
    /// <para><b>The properties live on <see cref="RangeBase"/></b>, as Silverlight declares them —
    /// see the remarks there for why the hierarchy has to match rather than merely the member
    /// names.</para>
    /// </remarks>
    public class ProgressBar : RangeBase
    {
        public static readonly DependencyProperty IsIndeterminateProperty =
            DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(ProgressBar),
                new PropertyMetadata((object)false));

        public bool IsIndeterminate
        {
            get => (bool)GetValue(IsIndeterminateProperty)!;
            set => SetValue(IsIndeterminateProperty, value);
        }

        public ProgressBar()
        {
            // Silverlight's ProgressBar defaults to 0..100, unlike RangeBase's 0..1.
            Maximum = 100.0;
        }
    }
}
