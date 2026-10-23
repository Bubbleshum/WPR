using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Xna.Framework.Storage;

namespace Microsoft.Xna.Framework.GamerServices
{
    public static class Guide
    {
        public static bool _SimulateTrialMode = false;

        /// <summary>
        /// Filled by the platform head: shows <c>(title, text, buttons, focusButton, icon)</c> and
        /// completes with the index of the button pressed, or <see cref="MessageBoxDismissed"/> when
        /// the box went away without one (Back, Esc, closing it). Every way out must complete the
        /// task: a game that polls <see cref="IsVisible"/> or waits on the result would otherwise
        /// wait for ever. Null means no UI, and every box answers as dismissed.
        /// </summary>
        public static Func<string, string, IEnumerable<string>, int, MessageBoxIcon, Task<int>> ShowMessageBoxFunc;

        /// <summary>What <see cref="ShowMessageBoxFunc"/> answers for a box closed without a button.</summary>
        public const int MessageBoxDismissed = -1;

        /// <summary>
        /// XNA's answer for a presenter's <paramref name="chosen"/>: the button's index, or null when
        /// the player backed out ("null if the user cancelled", as documented for
        /// <see cref="EndShowMessageBox"/>). An index outside the buttons shown counts as cancelled.
        /// </summary>
        public static int? ResolveMessageBoxResult(int chosen, int buttonCount) =>
            chosen >= 0 && chosen < buttonCount ? chosen : null;
        public static Func<string, string, string, Task<string?>> ShowInputBoxFunc;

        static Guide() {
            IsVisible = false;
        }

        public static IAsyncResult BeginShowKeyboardInput(PlayerIndex player, string title, string description, string defaultText, AsyncCallback callback, object state)
        {
            return Task.Run(async () =>
            {
                IsVisible = true;

                string? result = await ShowInputBoxFunc(title, description, defaultText);
                if (result == null)
                {
                    result = defaultText;
                }

                IsVisible = false;

                if (callback != null)
                {
                    TaskCompletionSource<string> compSource = new TaskCompletionSource<string>(state);
                    compSource.SetResult(result);

                    callback.Invoke(compSource.Task);
                }

                return result;
            });
        }

        /// <summary>
        /// The password-mode overload. WPR's input box has no masked mode, so
        /// <paramref name="usePasswordMode"/> is accepted and ignored rather than the overload
        /// being absent — an absent overload is a MissingMethodException at JIT time, which is
        /// strictly worse than an unmasked prompt. Skulls of the Shogun
        /// (AngelXNA.Util.PlatformXBL) calls this form.
        /// </summary>
        public static IAsyncResult BeginShowKeyboardInput(PlayerIndex player, string title, string description, string defaultText, AsyncCallback callback, object state, bool usePasswordMode)
        {
            return BeginShowKeyboardInput(player, title, description, defaultText, callback, state);
        }

        public static IAsyncResult BeginShowMessageBox(string title, string text, IEnumerable<string> buttons, int focusButton, MessageBoxIcon icon, AsyncCallback callback, object state)
        {
            List<string> labels = buttons?.ToList() ?? new List<string>();
            if (labels.Count > 2)
            {
                throw new ArgumentException("Show message box can't handle more than two buttons!");
            }

            return Task.Run(async () =>
            {
                IsVisible = true;
                int chosen = MessageBoxDismissed;
                try
                {
                    var show = ShowMessageBoxFunc;
                    if (show == null)
                    {
                        Trace.WriteLine($"[wpr-guide] no message box on this host; \"{title}\" answered as dismissed.");
                    }
                    else
                    {
                        // The box takes focus from the game window. WP7 raised no Deactivated /
                        // Activated for its own message box, and a spurious resume re-raises the
                        // game's Activated, so absorb the blip on both sides (same as Silverlight's
                        // MessageBox.Show).
                        WPR.Xna.Rhi.XnaBackend.SuppressFocusActivation(TimeSpan.FromSeconds(2));
                        chosen = await show(title, text, labels, focusButton, icon).ConfigureAwait(false);
                        WPR.Xna.Rhi.XnaBackend.SuppressFocusActivation(TimeSpan.FromSeconds(2));
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[wpr-guide] message box failed; answered as dismissed: {ex}");
                    chosen = MessageBoxDismissed;
                }
                finally
                {
                    IsVisible = false;
                }

                int? result = ResolveMessageBoxResult(chosen, labels.Count);
                Trace.WriteLine($"[wpr-guide] \"{title}\" -> {(result is int i ? "\"" + labels[i] + "\" (" + i + ")" : "dismissed (null)")}");

                if (callback != null)
                {
                    TaskCompletionSource<int?> compSource = new TaskCompletionSource<int?>(state);
                    compSource.SetResult(result);

                    callback.Invoke(compSource.Task);
                }

                return result;
            });
        }

        public static IAsyncResult BeginShowMessageBox(PlayerIndex player, string title, string text, IEnumerable<string> buttons, int focusButton, MessageBoxIcon icon, AsyncCallback callback, object state)
        {
            return BeginShowMessageBox(title, text, buttons, focusButton, icon, callback, state);
        }

        public static IAsyncResult BeginShowStorageDeviceSelector(AsyncCallback callback, object state)
        {
            return StorageDevice.BeginShowSelector(callback, state);
        }

        public static IAsyncResult BeginShowStorageDeviceSelector(PlayerIndex player, AsyncCallback callback, object state)
        {
            return StorageDevice.BeginShowSelector(player, callback, state);
        }

        public static IAsyncResult BeginShowStorageDeviceSelector(int sizeInBytes, int directoryCount, AsyncCallback callback, object state)
        {
            return StorageDevice.BeginShowSelector(sizeInBytes, directoryCount, callback, state);
        }

        public static IAsyncResult BeginShowStorageDeviceSelector(PlayerIndex player, int sizeInBytes, int directoryCount, AsyncCallback callback, object state)
        {
            return StorageDevice.BeginShowSelector(player, sizeInBytes, directoryCount, callback, state);
        }

        public static StorageDevice EndShowStorageDeviceSelector(IAsyncResult result)
        {
            return StorageDevice.EndShowSelector(result);
        }

        public static void DelayNotifications(TimeSpan delay)
        {
        }

        public static string EndShowKeyboardInput(IAsyncResult result)
        {
            return (result as Task<string>)!.Result;
        }

        public static int? EndShowMessageBox(IAsyncResult result)
        {
            return (result as Task<int?>)!.Result;
        }

        // The Xbox LIVE overlay screens. On the phone each opened a system page over the game and
        // returned at once; the game got control back when the player pressed Back. WPR has none
        // of those pages, so each one returns without doing anything: the game carries on as if
        // the player had looked and come straight back.
        //
        // They used to throw NotImplementedException. Fruit Ninja calls ShowGamerCard when the
        // player taps their gamerpic on the main menu; it caught the throw and showed its own
        // error box, but the menu was left half-way through handling the tap and took no more
        // touches for the rest of the session.

        public static void ShowComposeMessage(PlayerIndex player, string text, IEnumerable<Gamer> recipients) => OverlayUnavailable(nameof(ShowComposeMessage));

        public static void ShowFriendRequest(PlayerIndex player, Gamer gamer) => OverlayUnavailable(nameof(ShowFriendRequest));

        public static void ShowFriends(PlayerIndex player) => OverlayUnavailable(nameof(ShowFriends));

        // Game invites go to WPR Hub friends as hub messages, through the platform's friend picker
        // (WPR.Engine.Online.GameInvites). The Xbox LIVE recipients a game passes can't be reached,
        // so both overloads open the picker.
        public static void ShowGameInvite(PlayerIndex player, IEnumerable<Gamer> recipients) => ShowGameInvite(sessionId: null);

        // The Windows Phone 7.1 overload: invite friends to the session the game names. Pool Pro
        // Online 3 calls it from its online lobby's Invite button.
        public static void ShowGameInvite(string sessionId)
        {
            // WP7 threw GuideAlreadyVisibleException here, but its invite page covered the game, so
            // a game could not ask twice. The picker here is a window over the game, and on the
            // desktop the game still takes clicks: a second press of the Invite button would throw
            // into a game that never expected it (Pool Pro Online 3 doesn't catch it). Ignore it.
            if (IsVisible)
            {
                Trace.WriteLine("[wpr-invite] the invite picker is already open; ignoring the second request.");
                return;
            }
            // As on WP7 it returns at once and the game keeps running under the picker.
            IsVisible = true;
            WPR.Xna.Rhi.XnaBackend.SuppressFocusActivation(TimeSpan.FromSeconds(2));
            _ = WPR.Engine.Online.GameInvites.ShowAsync(
                WPR.Common.WprHostEnvironment.CurrentProductId,
                WPR.Common.WprHostEnvironment.CurrentTitleName,
                onClosed: () =>
                {
                    WPR.Xna.Rhi.XnaBackend.SuppressFocusActivation(TimeSpan.FromSeconds(2));
                    IsVisible = false;
                });
        }

        public static void ShowGamerCard(PlayerIndex player, Gamer gamer) => OverlayUnavailable(nameof(ShowGamerCard));

        public static void ShowMarketplace(PlayerIndex player) => OverlayUnavailable(nameof(ShowMarketplace));

        public static void ShowMessages(PlayerIndex player) => OverlayUnavailable(nameof(ShowMessages));

        public static void ShowParty(PlayerIndex player) => OverlayUnavailable(nameof(ShowParty));

        public static void ShowPartySessions(PlayerIndex player) => OverlayUnavailable(nameof(ShowPartySessions));

        public static void ShowPlayerReview(PlayerIndex player, Gamer gamer) => OverlayUnavailable(nameof(ShowPlayerReview));

        public static void ShowPlayers(PlayerIndex player) => OverlayUnavailable(nameof(ShowPlayers));

        // Trace reaches the per-game log in a Release build; without this line "the game asked for
        // an overlay WPR doesn't have" and "the tap never arrived" look the same from outside.
        private static void OverlayUnavailable(string overlay) =>
            Trace.WriteLine($"[wpr-guide] Guide.{overlay}: no Xbox LIVE overlay in WPR; returning to the game.");

        public static void ShowSignIn(int paneCount, bool onlineOnly)
        {
        }

        public static bool IsScreenSaverEnabled
        {
            get
            {
                return true;
            }
            set
            {
            }
        }

        /// <summary>Counts <see cref="IsTrialMode"/> reads so the trace can stay bounded.</summary>
        private static int _isTrialModeReads;

        /// <summary>
        /// Always false: every game WPR runs is installed from a full XAP, so nothing is a trial.
        ///
        /// <para>Traced (first few reads only) because a WP7 title's menu often differs entirely
        /// between trial and full — Doodle Jump gates a "buy full game" entry and its "local
        /// challenge" mode on it — so "what did the game ask, and when" is the first question when
        /// a menu comes up wrong. A game that never reads this is telling you the menu is being
        /// driven by something else.</para>
        /// </summary>
        public static bool IsTrialMode
        {
            get
            {
                int n = System.Threading.Interlocked.Increment(ref _isTrialModeReads);
                if (n <= 8)
                {
                    Trace.WriteLine($"[wpr-trial] Guide.IsTrialMode read #{n} → false");
                }
                return false;
            }
        }

        public static bool IsVisible
        {
            get;
            private set;
        }

        private static NotificationPosition _notificationPosition = NotificationPosition.TopCenter;

        public static NotificationPosition NotificationPosition
        {
            get
            {
                return _notificationPosition;
            }
            set
            {
                _notificationPosition = value;
            }
        }

        public static bool SimulateTrialMode
        {
            get => _SimulateTrialMode;
            set => _SimulateTrialMode = value;
        }

    }
}
