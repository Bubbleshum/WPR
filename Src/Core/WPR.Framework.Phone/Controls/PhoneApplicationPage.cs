using System;
using System.ComponentModel;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    // Derives from Page (not ContentControl directly) so the navigation lifecycle
    // hooks live on Page — matching SL's chain. User IL `base.OnNavigatedTo` calls
    // resolve to System.Windows.Controls.Page::OnNavigatedTo, which the patcher
    // rewrites to System.Windows.Controls.Page::OnNavigatedTo.
    public class PhoneApplicationPage : Page
    {
        /// <summary>
        /// Raised when the user presses the hardware Back key. Real WP7 routes the
        /// capacitive back-button press here; user code sets <c>e.Cancel = true</c>
        /// to swallow it (e.g. close an in-page popup instead of navigating back).
        /// On our host the source is the hardware Back key as the XNA loop sees it
        /// (Esc / Android Back / a pad's Back), which <c>SilverlightGame</c> routes into
        /// <see cref="PhoneApplicationFrame.HandleBackKey"/>.
        /// </summary>
        public event EventHandler<CancelEventArgs>? BackKeyPress;

        protected virtual void OnBackKeyPress(CancelEventArgs e)
        {
            BackKeyPress?.Invoke(this, e);
        }

        /// <summary>
        /// Internal entry point for the host's back key: invoke the page's
        /// virtual <see cref="OnBackKeyPress"/> and report whether user code asked
        /// to cancel default navigation. Wrapping the user invocation here keeps a
        /// thrown handler from propagating into the game loop.
        /// </summary>
        internal bool RaiseBackKeyPress()
        {
            var args = new CancelEventArgs();
            try { OnBackKeyPress(args); }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackKey] handler threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            return args.Cancel;
        }

        /// <summary>
        /// The page's application bar. WPR draws none (see <see cref="Microsoft.Phone.Shell.ApplicationBar"/>);
        /// it is kept so a page that builds or updates its bar in code or XAML runs.
        /// </summary>
        public Microsoft.Phone.Shell.IApplicationBar? ApplicationBar { get; set; }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(PhoneApplicationPage),
                new PropertyMetadata(string.Empty));

        // 'new' because Page already declares Title (different storage — DP vs auto-prop —
        // but the WP API surface is identical; CLR resolves whichever the caller's
        // strong-type expression names).
        public new string? Title
        {
            get => (string?)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>
        /// Per-page tombstoning state. WP7 persisted it across a deactivate/reactivate of the
        /// app; here a game run is never tombstoned, so it simply lives as long as the page.
        /// </summary>
        public System.Collections.Generic.IDictionary<string, object> State { get; } =
            new System.Collections.Generic.Dictionary<string, object>();

        /// <summary>The current physical orientation of the page. Static no-op shim — always Portrait.</summary>
        public PageOrientation Orientation { get; set; } = PageOrientation.PortraitUp;

        /// <summary>Which orientations the page may rotate to. Setter is a no-op shim.</summary>
        public SupportedPageOrientation SupportedOrientations { get; set; } = SupportedPageOrientation.Portrait;

        /// <summary>
        /// Fired when the page rotates. The device never turns here, so it is raised once, when the
        /// host shows a landscape-only page that still says portrait (<see cref="WprShownLandscape"/>),
        /// as the phone did turning to such a page.
        /// </summary>
        public event EventHandler<OrientationChangedEventArgs>? OrientationChanged;

        protected virtual void OnOrientationChanged(OrientationChangedEventArgs e)
        {
            OrientationChanged?.Invoke(this, e);
        }

        /// <summary>
        /// WPR host: the page is shown landscape. A page whose orientation is not yet a side
        /// (portrait, or plain Landscape) becomes LandscapeLeft and hears <see cref="OnOrientationChanged"/>, which is where WP8
        /// Direct3D pages tell their component which way round it is (AC Pirates).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void WprShownLandscape()
        {
            // Already on a side. Plain Landscape (what XAML usually says) is not a side: the
            // phone resolved it to one when the page was shown, and that was the change.
            if (Orientation is PageOrientation.LandscapeLeft or PageOrientation.LandscapeRight)
            {
                return;
            }

            Orientation = PageOrientation.LandscapeLeft;
            try { OnOrientationChanged(new OrientationChangedEventArgs(PageOrientation.LandscapeLeft)); }
            catch (Exception ex)
            {
                Console.WriteLine($"[OrientationChanged] handler threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
