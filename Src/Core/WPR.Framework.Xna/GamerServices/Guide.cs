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

        public static Func<string, string, IEnumerable<string>, int, MessageBoxIcon, Task<int>> ShowMessageBoxFunc;
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
            if (buttons.Count() > 2)
            {
                throw new ArgumentException("Show message box can't handle more than two buttons!");
            }

            return Task.Run(async () =>
            {
                IsVisible = true;

                int result = await ShowMessageBoxFunc(title, text, buttons, focusButton, icon);

                IsVisible = false;

                if (callback != null)
                {
                    TaskCompletionSource<int> compSource = new TaskCompletionSource<int>(state);
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
            return (result as Task<int>)!.Result;
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

        public static void ShowGameInvite(PlayerIndex player, IEnumerable<Gamer> recipients) => OverlayUnavailable(nameof(ShowGameInvite));

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
