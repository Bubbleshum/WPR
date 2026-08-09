using System;
using System.ComponentModel;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    // Derives from Page (not ContentControl directly) so the navigation lifecycle
    // hooks live on Page — matching SL's chain. User IL `base.OnNavigatedTo` calls
    // resolve to System.Windows.Controls.Page::OnNavigatedTo, which the patcher
    // rewrites to WPR.SilverlightCompability.Page::OnNavigatedTo.
    public class PhoneApplicationPage : Page
    {
        /// <summary>
        /// Raised when the user presses the hardware Back key. Real WP7 routes the
        /// capacitive back-button press here; user code sets <c>e.Cancel = true</c>
        /// to swallow it (e.g. close an in-page popup instead of navigating back).
        /// The desktop bezel's Back button (<c>PhoneHardwareButtons</c>) is the
        /// only source of this event on our host.
        /// </summary>
        public event EventHandler<CancelEventArgs>? BackKeyPress;

        protected virtual void OnBackKeyPress(CancelEventArgs e)
        {
            BackKeyPress?.Invoke(this, e);
        }

        /// <summary>
        /// Internal entry point for the host's bezel/back-button: invoke the page's
        /// virtual <see cref="OnBackKeyPress"/> and report whether user code asked
        /// to cancel default navigation. Wrapping the user invocation here keeps a
        /// thrown handler from propagating into the Avalonia UI thread.
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

        /// <summary>The current physical orientation of the page. Static no-op shim — always Portrait.</summary>
        public PageOrientation Orientation { get; set; } = PageOrientation.PortraitUp;

        /// <summary>Which orientations the page may rotate to. Setter is a no-op shim.</summary>
        public SupportedPageOrientation SupportedOrientations { get; set; } = SupportedPageOrientation.Portrait;

        /// <summary>Fired when the page rotates. Never raised by this shim.</summary>
        public event EventHandler<OrientationChangedEventArgs>? OrientationChanged;

        protected virtual void OnOrientationChanged(OrientationChangedEventArgs e)
        {
            OrientationChanged?.Invoke(this, e);
        }
    }
}
