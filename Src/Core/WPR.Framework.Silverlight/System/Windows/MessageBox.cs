using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.MessageBox</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>A blocking call, answered by the head.</b> WP7's <c>Show</c> does not return until the
    /// player answers, and games test the result on the next line, so this blocks the calling thread
    /// (the game thread) while <see cref="Presenter"/> shows a dialog on the platform's UI thread. The
    /// game stops drawing meanwhile, as a WP7 app under the system message box did. The presenter must
    /// therefore never need the calling thread to complete its task.</para>
    ///
    /// <para><b>Mapping, as documented for Silverlight / Windows Phone 7:</b> with
    /// <see cref="MessageBoxButton.OK"/> the result is <see cref="MessageBoxResult.OK"/> "in all
    /// cases"; with <see cref="MessageBoxButton.OKCancel"/> it is OK only when OK was chosen and
    /// <see cref="MessageBoxResult.Cancel"/> otherwise, i.e. Cancel, the hardware Back button, Esc and
    /// closing the window. WP7 has only those two values; the desktop-only YesNo/YesNoCancel this enum
    /// also carries map as WPF does (dismissing YesNo is No, YesNoCancel is Cancel).</para>
    ///
    /// <para>All of that lives here, in <see cref="Describe"/>, rather than in each head: a head only
    /// shows labelled buttons and reports which one was pressed, or -1 when the dialog was dismissed.
    /// Before 2026-10-16 each head carried its own commented-out mapping and assigned a placeholder
    /// that was never this class's field, so every <c>Show</c> NullReferenced.</para>
    /// </remarks>
    public static class MessageBox
    {
        /// <summary>
        /// Filled by the platform head: shows a dialog titled <c>caption</c> with body <c>text</c> and
        /// one button per label (in order), and completes with the index of the button pressed, or
        /// <c>-1</c> when the dialog was dismissed without one (Back, Esc, closing it). Must not need
        /// the calling (game) thread. Null means no UI: every call answers as if dismissed.
        /// </summary>
        public static Func<string, string, IReadOnlyList<string>, Task<int>>? Presenter;

        /// <summary>What a request for <paramref name="button"/> shows and how each outcome maps.</summary>
        /// <returns>Button labels (WP7's lower-case wording), the result for each, and the result
        /// when the dialog is dismissed without a button.</returns>
        public static (string[] Labels, MessageBoxResult[] Results, MessageBoxResult Dismissed) Describe(MessageBoxButton button)
        {
            switch (button)
            {
                case MessageBoxButton.OKCancel:
                    return (new[] { "ok", "cancel" },
                        new[] { MessageBoxResult.OK, MessageBoxResult.Cancel }, MessageBoxResult.Cancel);
                case MessageBoxButton.YesNo:
                    return (new[] { "yes", "no" },
                        new[] { MessageBoxResult.Yes, MessageBoxResult.No }, MessageBoxResult.No);
                case MessageBoxButton.YesNoCancel:
                    return (new[] { "yes", "no", "cancel" },
                        new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel }, MessageBoxResult.Cancel);
                default:
                    // OK, and anything unrecognised: one button, OK in all cases.
                    return (new[] { "ok" }, new[] { MessageBoxResult.OK }, MessageBoxResult.OK);
            }
        }

        /// <summary>Maps a presenter's answer (button index, or -1 for dismissed) to the result.</summary>
        public static MessageBoxResult Resolve(MessageBoxButton button, int chosen)
        {
            var (_, results, dismissed) = Describe(button);
            return chosen >= 0 && chosen < results.Length ? results[chosen] : dismissed;
        }

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
        {
            // Silverlight throws ArgumentNullException for a null text or caption. Showing an empty
            // string is kinder to a game that never expected to reach this on real hardware.
            messageBoxText ??= string.Empty;
            caption ??= string.Empty;

            var (labels, _, _) = Describe(button);
            var presenter = Presenter;
            int chosen = -1;
            if (presenter == null)
            {
                Trace.WriteLine($"[wpr-msgbox] no presenter on this host; \"{caption}\" ({button}) answered as dismissed.");
            }
            else
            {
                // The dialog takes focus from the game window, and the window's focus events wait in
                // its queue until the game thread pumps again, i.e. after this returns. WP7 raised no
                // Deactivated/Activated for its own message box, so absorb that blip on both sides.
                WPR.Xna.Rhi.XnaBackend.SuppressFocusActivation(TimeSpan.FromSeconds(2));
                try
                {
                    chosen = presenter(caption, messageBoxText, labels).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[wpr-msgbox] presenter failed; answered as dismissed: {ex}");
                    chosen = -1;
                }
                WPR.Xna.Rhi.XnaBackend.SuppressFocusActivation(TimeSpan.FromSeconds(2));
            }

            MessageBoxResult result = Resolve(button, chosen);
            Trace.WriteLine($"[wpr-msgbox] \"{caption}\" ({button}) -> {(chosen < 0 ? "dismissed" : labels[Math.Min(chosen, labels.Length - 1)])} = {result}");
            return result;
        }

        /// <summary>
        /// Silverlight's one-argument overload: message only, an OK button, no caption.
        /// </summary>
        /// <remarks>
        /// A separate method rather than an optional parameter, because a default argument is
        /// baked into the CALLER at compile time — a game's IL names <c>Show(string)</c> and
        /// resolves against that exact signature, so an optional-parameter version would still
        /// be a MissingMethodException. Flight Control Rocket is the case that found this.
        /// </remarks>
        public static MessageBoxResult Show(string message)
        {
            return Show(message, string.Empty, MessageBoxButton.OK);
        }
    }

}
