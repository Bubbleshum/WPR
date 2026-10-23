using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

using WPR.Engine.Online;
using WPR.Online.Hub;
using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The game-invite friend picker (<see cref="GameInvites.Picker"/>), drawn the way the WPR Hub
    /// screens are: a full-screen black panel over the game with the game's tile, a big light
    /// "invite friends" title, one row per friend (gamerpic, name, presence, an accent tick box) and
    /// invite / cancel along the bottom. Back or cancel picks nobody.
    /// </summary>
    internal static class InvitePicker
    {
        public static Task<IReadOnlyList<string>?> ShowAsync(string gameName, IReadOnlyList<InviteFriend> friends, string? emptyMessage)
        {
            var source = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Activity? activity = MessageBoxUtils.MainActivity;
            if (activity == null || activity.IsFinishing)
            {
                source.TrySetResult(null);
                return source.Task;
            }

            activity.RunOnUiThread(() =>
            {
                try
                {
                    Show(activity, gameName, friends, emptyMessage, source);
                }
                catch (Exception ex)
                {
                    Common.Log.Error(Common.LogCategory.AppList, "Invite picker failed: " + ex);
                    source.TrySetResult(null);
                }
            });
            return source.Task;
        }

        private static void Show(Activity activity, string gameName, IReadOnlyList<InviteFriend> friends, string? emptyMessage,
            TaskCompletionSource<IReadOnlyList<string>?> source)
        {
            int Dp(int dp) => HubViews.Dp(activity, dp);
            HubOnline? hub = HubSetup.Current;
            var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var dialog = new Dialog(activity, global::Android.Resource.Style.ThemeBlackNoTitleBarFullScreen);
            dialog.SetCancelable(true);

            var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(Color.Black);
            root.SetPadding(Dp(24), Dp(20), Dp(24), Dp(16));

            // Header: the game's tile beside "INVITE FRIENDS" / the game's name.
            var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            header.SetGravity(GravityFlags.CenterVertical);
            Bitmap? art = null;
            try { art = GameTileArt.Decode(null, Common.WprHostEnvironment.CurrentProductId ?? ""); } catch (Exception) { }
            if (art != null)
            {
                var tile = new ImageView(activity);
                tile.SetImageBitmap(art);
                tile.SetScaleType(ImageView.ScaleType.CenterCrop);
                header.AddView(tile, new LinearLayout.LayoutParams(Dp(64), Dp(64)) { RightMargin = Dp(16) });
            }
            var titles = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            TextView small = HubViews.Text(activity, "INVITE FRIENDS", 14, WpTheme.Accent);
            small.LetterSpacing = 0.1f;
            titles.AddView(small);
            TextView big = HubViews.Text(activity, gameName, 34, WpTheme.Foreground, light: true);
            big.SetMaxLines(1);
            big.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            titles.AddView(big);
            header.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            root.AddView(header);

            // The list, or why there is none.
            var list = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            list.SetPadding(0, Dp(12), 0, Dp(12));
            TextView invite = HubViews.Button(activity, "invite", accent: true);
            void Refresh()
            {
                invite.Enabled = chosen.Count > 0;
                invite.Alpha = chosen.Count > 0 ? 1f : 0.4f;
                invite.Text = chosen.Count > 1 ? $"invite {chosen.Count}" : "invite";
            }

            if (emptyMessage != null || friends.Count == 0)
            {
                list.AddView(HubViews.Text(activity, emptyMessage ?? "You have no WPR Hub friends yet.", 18, HubViews.Subtle, light: true));
            }
            else
            {
                string? lastSection = null;
                foreach (InviteFriend friend in friends)
                {
                    string section = friend.Online ? "online" : "offline";
                    if (section != lastSection)
                    {
                        list.AddView(HubViews.Section(activity, section));
                        lastSection = section;
                    }

                    string line = friend.Playing is { Length: > 0 } playing ? "playing " + playing : friend.Online ? "online" : "offline";
                    Color lineColor = friend.Online ? WpTheme.Accent : HubViews.Subtle;
                    LinearLayout row = hub != null
                        ? HubViews.PersonRow(activity, hub, friend.GamerpicUrl, friend.Username, line, lineColor, out LinearLayout trailing)
                        : PlainRow(activity, friend.Username, line, lineColor, out trailing);

                    View tick = new View(activity);
                    trailing.AddView(tick, new LinearLayout.LayoutParams(Dp(28), Dp(28)) { LeftMargin = Dp(12) });
                    void Paint()
                    {
                        var box = new GradientDrawable();
                        box.SetShape(ShapeType.Rectangle);
                        bool on = chosen.Contains(friend.Username);
                        box.SetColor(on ? WpTheme.Accent : Color.Transparent);
                        box.SetStroke(Dp(2), on ? WpTheme.Accent : Color.White);
                        tick.Background = box;
                    }
                    Paint();

                    row.Clickable = true;
                    WpTheme.ApplyTilt(row, 0.97f);
                    row.Click += (_, _) =>
                    {
                        if (!chosen.Remove(friend.Username)) chosen.Add(friend.Username);
                        Paint();
                        Refresh();
                    };
                    list.AddView(row);
                }
            }
            // A gap under the header and a fade at the list's top edge, so a row scrolled half
            // away doesn't sit against the title.
            var scroll = new ScrollView(activity) { VerticalFadingEdgeEnabled = true };
            scroll.SetFadingEdgeLength(Dp(32));
            scroll.AddView(list);
            root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f) { TopMargin = Dp(12) });

            // Buttons along the bottom.
            var buttons = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            buttons.SetGravity(GravityFlags.End);
            bool canInvite = emptyMessage == null && friends.Count > 0;
            if (canInvite)
            {
                invite.Click += (_, _) =>
                {
                    if (chosen.Count == 0) return;
                    source.TrySetResult(chosen.ToList());
                    dialog.Dismiss();
                };
                buttons.AddView(invite, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
                Refresh();
            }
            TextView cancel = HubViews.Button(activity, canInvite ? "cancel" : "ok", accent: !canInvite);
            cancel.Click += (_, _) => dialog.Dismiss();
            buttons.AddView(cancel);
            root.AddView(buttons);

            // The panel's own padding plus whatever the camera cut-out (or a bar that is showing
            // for a moment) takes from that edge, so nothing sits under either.
            int padLeft = Dp(24), padTop = Dp(20), padRight = Dp(24), padBottom = Dp(16);
            root.SetOnApplyWindowInsetsListener(new InsetsListener(insets =>
            {
                int l = 0, t = 0, r = 0, b = 0;
                if (OperatingSystem.IsAndroidVersionAtLeast(28) && insets.DisplayCutout is { } cutout)
                {
                    l = cutout.SafeInsetLeft; t = cutout.SafeInsetTop; r = cutout.SafeInsetRight; b = cutout.SafeInsetBottom;
                }
                root.SetPadding(padLeft + l, padTop + t, padRight + r, padBottom + b);
            }));

            dialog.SetContentView(root);
            dialog.DismissEvent += (_, _) => source.TrySetResult(null);
            dialog.CancelEvent += (_, _) => source.TrySetResult(null);
            Immersive(dialog.Window);
            dialog.Show();
            Immersive(dialog.Window); // again once shown: showing a dialog can bring the bars back
        }

        /// <summary>
        /// Full screen over the game the way the game itself is: system bars hidden (a swipe shows
        /// them for a moment), drawn into the camera cut-out, black behind.
        /// </summary>
        private static void Immersive(Window? window)
        {
            if (window == null) return;
            window.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            window.SetBackgroundDrawable(new ColorDrawable(Color.Black));
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                WindowManagerLayoutParams attributes = window.Attributes!;
                attributes.LayoutInDisplayCutoutMode = OperatingSystem.IsAndroidVersionAtLeast(30)
                    ? LayoutInDisplayCutoutMode.Always
                    : LayoutInDisplayCutoutMode.ShortEdges;
                window.Attributes = attributes;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                window.SetDecorFitsSystemWindows(false);
                if (window.InsetsController is { } controller)
                {
                    controller.Hide(WindowInsets.Type.SystemBars());
                    controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                }
            }
            else
            {
#pragma warning disable CA1422 // the pre-30 way, only reached below 30
                window.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation
                    | SystemUiFlags.ImmersiveSticky | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);
#pragma warning restore CA1422
            }
        }

        private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
        {
            private readonly Action<WindowInsets> _apply;
            public InsetsListener(Action<WindowInsets> apply) => _apply = apply;

            public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
            {
                _apply(insets);
                return insets;
            }
        }

        /// <summary>A friend row without a hub connection to load the gamerpic through.</summary>
        private static LinearLayout PlainRow(Activity activity, string name, string line, Color lineColor, out LinearLayout trailing)
        {
            var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, HubViews.Dp(activity, 8), 0, HubViews.Dp(activity, 8));
            var text = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            text.AddView(HubViews.Text(activity, name, 22, WpTheme.Foreground, light: true));
            text.AddView(HubViews.Text(activity, line, 13, lineColor));
            row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            trailing = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            row.AddView(trailing);
            return row;
        }
    }
}
