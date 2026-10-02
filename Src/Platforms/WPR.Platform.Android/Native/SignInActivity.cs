using System;
using System.Threading;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using WPR.Common;
using WPR.Online.Hub;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// WPR Hub sign-in (and sign-up: the first sign-in makes the account): "continue with GitHub /
    /// Microsoft / Google", then the link key, the hub's /link page opened at that provider, and a
    /// wait for the player to approve there. The flow is <see cref="HubAccountService"/>; this is its
    /// face, the twin of the desktop's <c>SignInWindow</c>. Leaving the page gives up, and the key
    /// then simply expires on the hub.
    /// </summary>
    [Activity(
        Label = "sign in",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.SignInActivity")]
    public class SignInActivity : Activity
    {
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private HubAccountService.SignIn? _signIn;
        private DateTimeOffset _expiresAt;
        private bool _waiting;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            WprStartup.EnsureInitialized(this);

            SetContentView(Resource.Layout.activity_sign_in);
            WpTheme.ApplySystemBars(this);

            TextView appTitle = FindViewById<TextView>(Resource.Id.appTitle)!;
            appTitle.SetTextColor(WpTheme.Accent);
            HubLogo.Apply(this, FindViewById<ImageView>(Resource.Id.hubLogo)!, appTitle);

            FindViewById<TextView>(Resource.Id.keyText)!.SetTextColor(WpTheme.Accent);

            TextView open = FindViewById<TextView>(Resource.Id.openButton)!;
            open.SetBackgroundColor(WpTheme.Accent);
            WpTheme.ApplyTilt(open);
            open.Click += (_, _) => OpenLink();

            TextView copy = FindViewById<TextView>(Resource.Id.copyButton)!;
            WpTheme.ApplyTilt(copy);
            copy.Click += (_, _) =>
            {
                if (_signIn == null) return;
                var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
                if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText("WPR Hub link key", _signIn.LinkKey);
                copy.Text = "copied";
            };

            TextView primary = FindViewById<TextView>(Resource.Id.primaryButton)!;
            primary.SetBackgroundColor(WpTheme.Accent);
            WpTheme.ApplyTilt(primary);

            TextView close = FindViewById<TextView>(Resource.Id.closeButton)!;
            WpTheme.ApplyTilt(close);
            close.Click += (_, _) => Finish();

            // No browser here, or rather use one elsewhere: show the key and the page to type it into.
            TextView otherDevice = FindViewById<TextView>(Resource.Id.otherDeviceButton)!;
            otherDevice.Click += (_, _) =>
            {
                if (_signIn == null) return;
                _provider = null;
                ShowWaiting();
            };

            _ = RunAsync();
        }

        protected override void OnDestroy()
        {
            _cancel.Cancel();
            base.OnDestroy();
        }

        private async Task RunAsync()
        {
            ShowSteps();
            HubOnline? hub = WPR.Shell.HubSetup.Current;
            if (hub == null)
            {
                ShowFailed("WPR Hub is not available in this copy of WPR.");
                return;
            }

            try
            {
                _signIn = await hub.Account.StartAsync(
                    WPR.Shell.HubSetup.ClientName($"{Build.Manufacturer} {Build.Model}"), _cancel.Token);
                if (_signIn == null)
                {
                    ShowFailed("can't reach WPR Hub. check your internet connection and try again.");
                    return;
                }

                FindViewById<TextView>(Resource.Id.keyText)!.Text = _signIn.LinkKey;
                FindViewById<TextView>(Resource.Id.linkPageText)!.Text =
                    $"or go to {_signIn.LinkPage} on any device and type the key.";
                _expiresAt = DateTimeOffset.UtcNow + _signIn.ExpiresIn;
                _waiting = true;
                _ = CountdownAsync();
                ShowProviders(_signIn);

                string? username = await _signIn.CompleteAsync(_cancel.Token);
                _waiting = false;

                if (username == null)
                {
                    ShowFailed("the sign-in was declined on the website, or the key expired before it was approved.");
                    return;
                }

                await ShowDoneAsync(hub, username);
            }
            catch (System.OperationCanceledException)
            {
                // Page closed.
            }
            catch (Exception ex)
            {
                _waiting = false;
                Log.Warn(LogCategory.AppList, "[wpr-online] sign-in failed: " + ex);
                ShowFailed("sign-in failed: " + ex.Message);
            }
        }

        private async Task CountdownAsync()
        {
            TextView status = FindViewById<TextView>(Resource.Id.statusText)!;
            while (_waiting && !IsDestroyed)
            {
                TimeSpan left = _expiresAt - DateTimeOffset.UtcNow;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                status.Text = $"waiting for you to approve in the browser. the key expires in {(int)left.TotalMinutes}:{left.Seconds:00}.";
                try { await Task.Delay(1000, _cancel.Token); }
                catch (System.OperationCanceledException) { return; }
            }
        }

        /// <summary>The provider picked on the chooser, sent to the link page as a hint. Null: the plain page.</summary>
        private string? _provider;

        /// <summary>One "continue with …" button per sign-in the hub offers; the first takes the accent.</summary>
        private void ShowProviders(HubAccountService.SignIn signIn)
        {
            LinearLayout buttons = FindViewById<LinearLayout>(Resource.Id.providerButtons)!;
            buttons.RemoveAllViews();
            int pad = (int)(12 * Resources!.DisplayMetrics!.Density);
            bool first = true;
            foreach (var provider in signIn.Providers)
            {
                TextView button = new TextView(this)
                {
                    Text = "continue with " + provider.Label,
                    TextSize = 18,
                    Clickable = true,
                    Focusable = true,
                };
                button.SetTextColor(WpTheme.Foreground);
                button.Typeface = Typeface.Create("sans-serif", TypefaceStyle.Normal);
                button.SetPadding(pad + pad / 2, pad + pad / 3, pad, pad + pad / 3);
                button.SetBackgroundColor(first ? WpTheme.Accent : WpTheme.Chrome);
                WpTheme.ApplyTilt(button, 0.97f);
                string key = provider.Key;
                button.Click += (_, _) =>
                {
                    _provider = key;
                    ShowWaiting();
                    OpenLink();
                };

                var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                {
                    BottomMargin = pad * 2 / 3,
                };
                buttons.AddView(button, lp);
                first = false;
            }
            FindViewById<TextView>(Resource.Id.chooserStatusText)!.Visibility = ViewStates.Gone;
        }

        /// <summary>From the chooser to the key and the wait for approval.</summary>
        private void ShowWaiting()
        {
            FindViewById<View>(Resource.Id.chooserPanel)!.Visibility = ViewStates.Gone;
            FindViewById<View>(Resource.Id.stepsPanel)!.Visibility = ViewStates.Visible;
        }

        private void OpenLink()
        {
            if (_signIn == null) return;
            string url = _provider == null ? _signIn.LinkUrl : _signIn.LinkUrlFor(_provider);
            try
            {
                StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)));
            }
            catch (Exception ex)
            {
                FindViewById<TextView>(Resource.Id.statusText)!.Text =
                    $"could not open a browser ({ex.Message}). go to {_signIn.LinkUrl} yourself.";
            }
        }

        private void ShowSteps()
        {
            FindViewById<View>(Resource.Id.chooserPanel)!.Visibility = ViewStates.Visible;
            FindViewById<LinearLayout>(Resource.Id.providerButtons)!.RemoveAllViews();
            TextView chooserStatus = FindViewById<TextView>(Resource.Id.chooserStatusText)!;
            chooserStatus.Text = "getting ready…";
            chooserStatus.Visibility = ViewStates.Visible;
            FindViewById<View>(Resource.Id.stepsPanel)!.Visibility = ViewStates.Gone;
            FindViewById<View>(Resource.Id.donePanel)!.Visibility = ViewStates.Gone;
            FindViewById<View>(Resource.Id.failedText)!.Visibility = ViewStates.Gone;
            FindViewById<View>(Resource.Id.primaryButton)!.Visibility = ViewStates.Gone;
            FindViewById<TextView>(Resource.Id.closeButton)!.Text = "cancel";
            FindViewById<TextView>(Resource.Id.copyButton)!.Text = "copy key";
            FindViewById<TextView>(Resource.Id.keyText)!.Text = "····-····";
            FindViewById<TextView>(Resource.Id.statusText)!.Text = "asking WPR Hub for a key…";
            FindViewById<TextView>(Resource.Id.pageTitle)!.Text = "sign in";
        }

        private void ShowFailed(string message)
        {
            FindViewById<View>(Resource.Id.chooserPanel)!.Visibility = ViewStates.Gone;
            FindViewById<View>(Resource.Id.stepsPanel)!.Visibility = ViewStates.Gone;
            TextView failed = FindViewById<TextView>(Resource.Id.failedText)!;
            failed.Text = message;
            failed.Visibility = ViewStates.Visible;

            TextView primary = FindViewById<TextView>(Resource.Id.primaryButton)!;
            primary.Text = "try again";
            primary.Visibility = ViewStates.Visible;
            ReplaceClick(primary, () => _ = RunAsync());
            FindViewById<TextView>(Resource.Id.closeButton)!.Text = "close";
        }

        private async Task ShowDoneAsync(HubOnline hub, string username)
        {
            FindViewById<View>(Resource.Id.chooserPanel)!.Visibility = ViewStates.Gone;
            FindViewById<View>(Resource.Id.stepsPanel)!.Visibility = ViewStates.Gone;
            FindViewById<View>(Resource.Id.donePanel)!.Visibility = ViewStates.Visible;
            FindViewById<TextView>(Resource.Id.pageTitle)!.Text = "you're in";
            FindViewById<TextView>(Resource.Id.introText)!.Visibility = ViewStates.Gone;
            FindViewById<TextView>(Resource.Id.usernameText)!.Text = username;

            TextView primary = FindViewById<TextView>(Resource.Id.primaryButton)!;
            primary.Text = "choose a gamerpic";
            primary.Visibility = ViewStates.Visible;
            ReplaceClick(primary, () =>
            {
                StartActivity(new Intent(this, typeof(GamerpicsActivity)));
                Finish();
            });
            FindViewById<TextView>(Resource.Id.closeButton)!.Text = "done";

            Bitmap? picture = await LoadCurrentGamerpicAsync(hub);
            if (picture != null && !IsDestroyed) FindViewById<ImageView>(Resource.Id.gamerpicImage)!.SetImageBitmap(picture);
        }

        /// <summary>The account's gamerpic, or null. Refreshing also saves it as the in-game gamer picture when appropriate.</summary>
        internal static async Task<Bitmap?> LoadCurrentGamerpicAsync(HubOnline? hub)
        {
            if (hub == null) return null;
            var account = await hub.Account.RefreshAsync();
            byte[]? bytes = await hub.Account.GetImageAsync(account?.Gamerpic?.Url);
            return bytes == null ? null : BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
        }

        /// <summary>One click handler at a time on a reused button, whichever state set it last.</summary>
        private static void ReplaceClick(View view, Action action)
        {
            view.SetOnClickListener(new ClickListener(action));
        }

        private sealed class ClickListener : Java.Lang.Object, View.IOnClickListener
        {
            private readonly Action _action;
            public ClickListener(Action action) => _action = action;
            public void OnClick(View? v) => _action();
        }
    }
}
