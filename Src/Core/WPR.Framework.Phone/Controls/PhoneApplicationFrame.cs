using System;
using System.ComponentModel;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    public class PhoneApplicationFrame : Frame
    {
        /// <summary>
        /// Becomes the application's <c>RootVisual</c> immediately if nothing else has claimed it.
        /// </summary>
        /// <remarks>
        /// <para>On WP7 the Silverlight host has a visual root before it constructs the app class,
        /// so shipped code reads <c>Application.Current.RootVisual</c> from inside its own
        /// constructor and finds something there. WPR left it null until the first navigation,
        /// because that is when the generated <c>CompleteInitializePhoneApplication</c> assigns it
        /// — which is far too late for a constructor.</para>
        ///
        /// <para>Carcassonne is the reference case:
        /// <c>((DependencyObject)Application.Current.RootVisual).Dispatcher</c>, twice, in
        /// <c>App..ctor</c> right after <c>InitializePhoneApplication()</c>. The whole application
        /// failed to construct, so nothing rendered at all — and the report is a bare
        /// <c>NullReferenceException</c> in the game's own constructor with no indication that a
        /// WPR property is the one that is null.</para>
        ///
        /// <para><b>This aliases nothing and changes no fallback.</b> The frame assigned here is
        /// the same object every consumer already falls back to — <c>SilverlightAppHost</c>'s
        /// "RootFrame but no RootVisual" backstop and <c>MixedModeGame</c>'s
        /// <c>RootVisual ?? RootFrame</c> both converge on it — and the generated
        /// <c>if (RootVisual != RootFrame) RootVisual = RootFrame</c> simply finds them already
        /// equal. An app that assigns a different root later still wins, because this only fires
        /// when the slot is empty.</para>
        /// </remarks>
        public PhoneApplicationFrame()
        {
            try
            {
                var app = WPR.WindowsCompability.Application.Current;
                if (app != null && app.RootVisual == null) app.RootVisual = this;
            }
            catch
            {
                // Never let claiming the root visual stop a frame being constructed.
            }
        }

        // _currentPage is typed as the SL Page base on Frame; surface it as PhoneApplicationPage
        // (all navigable WP pages derive from it) for the WP-shaped Content property.
        public new PhoneApplicationPage? Content => _currentPage as PhoneApplicationPage;

        /// <summary>Fired when the frame becomes obscured (e.g. lock screen). No-op shim — never raised.</summary>
        public event EventHandler<ObscuredEventArgs>? Obscured;

        /// <summary>
        /// Fired when the frame becomes unobscured. The real WP API is asymmetric — Obscured
        /// passes an <see cref="ObscuredEventArgs"/>, but Unobscured is plain <see cref="EventHandler"/>
        /// because there's nothing to report when the frame regains visibility. We mirror that
        /// signature so user IL <c>add_Unobscured(EventHandler)</c> resolves correctly.
        /// </summary>
        public event EventHandler? Unobscured;

        // Keep these "used" to silence CS0067 — they're part of the public contract for IL binding.
        private void _SuppressUnusedWarnings()
        {
            Obscured?.Invoke(this, new ObscuredEventArgs(false));
            Unobscured?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Dispatch a hardware Back-key press. Order matches real WP7:
        ///   1. Current page's <c>BackKeyPress</c> handler runs; if it cancels,
        ///      the press is consumed and nothing else happens (return true).
        ///   2. Otherwise, if <c>CanGoBack</c>, navigate back and return true.
        ///   3. Otherwise return false — at the root of the back-stack, real
        ///      WP7 exits the app; the host should close its window.
        /// </summary>
        /// <summary>
        /// Frame-level Back-key notification, raised before the current page's own handler.
        /// </summary>
        /// <remarks>
        /// An app subscribes here to intercept Back globally rather than page by page — Galactic
        /// Reign does it from its App constructor, and without the event failed to construct at
        /// all. Cancelling it consumes the press: neither the page's handler nor the navigation
        /// runs, which is what an app that is showing its own modal over the top wants.
        /// </remarks>
        public event EventHandler<CancelEventArgs>? BackKeyPress;

        public bool HandleBackKey()
        {
            EventHandler<CancelEventArgs>? frameHandler = BackKeyPress;
            if (frameHandler != null)
            {
                var args = new CancelEventArgs();
                frameHandler(this, args);
                if (args.Cancel) return true;
            }

            if (_currentPage is PhoneApplicationPage page && page.RaiseBackKeyPress())
                return true;
            if (CanGoBack) { GoBack(); return true; }
            return false;
        }
    }
}
