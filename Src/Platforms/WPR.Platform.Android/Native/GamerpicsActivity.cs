using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;

using WPR.Online.Hub;
using WPR.Online.Hub.Client;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Pick a WPR Hub gamerpic, the twin of the desktop's <c>GamerpicWindow</c>. Reward pictures
    /// are shown locked, and tapping one says which milestone unlocks it. The chosen picture is also
    /// saved as the in-game gamer picture.
    /// </summary>
    /// <remarks>Built in code rather than a layout: the grid's contents are entirely data.</remarks>
    [Activity(
        Label = "gamerpic",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.GamerpicsActivity")]
    public class GamerpicsActivity : Activity
    {
        private const int Columns = 4;

        // @color/wp_subtle, the secondary text colour the layouts use.
        private static readonly Color Subtle = Color.ParseColor("#99FFFFFF");

        private readonly Dictionary<long, FrameLayout> _tiles = new Dictionary<long, FrameLayout>();
        private LinearLayout _content = null!;
        private TextView _status = null!;
        private long? _currentId;
        private bool _busy;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            WprStartup.EnsureInitialized(this);

            DisplayMetrics metrics = Resources!.DisplayMetrics!;
            int margin = Resources.GetDimensionPixelSize(Resource.Dimension.wp_page_margin);

            ScrollView scroll = new ScrollView(this) { FillViewport = true };
            scroll.SetBackgroundColor(Color.Black);
            _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _content.SetPadding(margin, Dp(28), margin, Dp(36));
            scroll.AddView(_content);
            SetContentView(scroll);
            WpTheme.ApplySystemBars(this);

            ImageView logo = new ImageView(this) { ContentDescription = "WPR Hub" };
            logo.SetAdjustViewBounds(true);
            _content.AddView(logo, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(64)));

            TextView appTitle = Text("WPR HUB", 14, WpTheme.Accent);
            appTitle.LetterSpacing = 0.1f;
            _content.AddView(appTitle);
            HubLogo.Apply(this, logo, appTitle);

            TextView title = Text("gamerpic", 56, WpTheme.Foreground, light: true);
            _content.AddView(title);

            TextView intro = Text("other players see this beside your name on the leaderboards. it also becomes your gamer picture inside games.",
                15, WpTheme.Foreground);
            intro.SetPadding(0, Dp(6), 0, Dp(18));
            _content.AddView(intro);

            _status = Text("loading gamerpics…", 13, Subtle);
            _status.SetPadding(0, 0, 0, Dp(14));
            _content.AddView(_status);

            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            HubOnline? hub = WPR.Shell.HubSetup.Current;
            if (hub == null) { _status.Text = "WPR Hub is not available."; return; }

            GamerpicCatalog? catalog;
            try
            {
                catalog = await hub.Account.GetGamerpicsAsync();
                _currentId = (await hub.Account.RefreshAsync())?.Gamerpic?.Id;
            }
            catch (Exception ex)
            {
                _status.Text = "could not load gamerpics: " + ex.Message;
                return;
            }
            if (IsDestroyed) return;

            if (catalog == null) { _status.Text = "sign in to choose a gamerpic."; return; }
            if (catalog.Gamerpics.Count == 0) { _status.Text = "there are no gamerpics on this hub yet."; return; }

            int gap = Dp(8);
            int margin = Resources!.GetDimensionPixelSize(Resource.Dimension.wp_page_margin);
            int tile = (Resources.DisplayMetrics!.WidthPixels - margin * 2 - gap * (Columns - 1)) / Columns;

            foreach (var group in catalog.Gamerpics.GroupBy(g => string.IsNullOrEmpty(g.Category) ? "gamerpics" : g.Category!))
            {
                TextView header = Text(group.Key.ToUpperInvariant(), 13, Subtle);
                header.LetterSpacing = 0.08f;
                header.SetPadding(0, Dp(10), 0, Dp(8));
                _content.AddView(header);

                GridLayout grid = new GridLayout(this) { ColumnCount = Columns };
                int index = 0;
                foreach (GamerpicInfo pic in group)
                {
                    var lp = new GridLayout.LayoutParams { Width = tile, Height = tile };
                    lp.SetMargins(0, 0, (index % Columns) == Columns - 1 ? 0 : gap, gap);
                    grid.AddView(BuildTile(hub, pic), lp);
                    index++;
                }
                _content.AddView(grid);
            }

            _currentId ??= catalog.DefaultId;
            Highlight();
            _status.Text = "tap one to use it. locked pictures are unlocked by milestones.";
        }

        private View BuildTile(HubOnline hub, GamerpicInfo pic)
        {
            FrameLayout frame = new FrameLayout(this);
            int ring = Dp(3);
            frame.SetPadding(ring, ring, ring, ring);

            ImageView image = new ImageView(this) { ContentDescription = pic.Name };
            image.SetScaleType(ImageView.ScaleType.CenterCrop);
            image.SetBackgroundColor(WpTheme.Chrome);
            frame.AddView(image, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            if (pic.Locked)
            {
                image.Alpha = 0.3f;
                TextView lockGlyph = Text("\U0001F512", 26, WpTheme.Foreground);
                lockGlyph.Gravity = GravityFlags.Center;
                frame.AddView(lockGlyph, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            }

            WpTheme.ApplyTilt(frame);
            frame.Click += async (_, _) => await ChooseAsync(hub, pic);
            _tiles[pic.Id] = frame;

            _ = LoadImageAsync(hub, image, pic.Url);
            return frame;
        }

        private async Task ChooseAsync(HubOnline hub, GamerpicInfo pic)
        {
            if (_busy) return;
            if (pic.Locked)
            {
                string how = pic.UnlockedBy is { Count: > 0 } ways ? " " + string.Join(" or: ", ways.Select(u => u.Requirement)) : "";
                _status.Text = $"\"{pic.Name}\" is locked.{how}";
                return;
            }
            if (pic.Id == _currentId) return;

            _busy = true;
            _status.Text = $"setting \"{pic.Name}\"…";
            try
            {
                await hub.Account.ChooseGamerpicAsync(pic.Id);
                _currentId = pic.Id;
                Highlight();
                _status.Text = $"your gamerpic is now \"{pic.Name}\".";
            }
            catch (Exception ex)
            {
                _status.Text = "could not set it: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private void Highlight()
        {
            foreach (var (id, frame) in _tiles)
                frame.SetBackgroundColor(id == _currentId ? WpTheme.Foreground : Color.Transparent);
        }

        private async Task LoadImageAsync(HubOnline hub, ImageView target, string url)
        {
            byte[]? bytes = await hub.Account.GetImageAsync(url);
            if (bytes == null || IsDestroyed) return;
            Bitmap? bitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            if (bitmap != null) target.SetImageBitmap(bitmap);
        }

        private TextView Text(string text, float sp, Color color, bool light = false)
        {
            TextView view = new TextView(this) { Text = text, TextSize = sp };
            view.SetTextColor(color);
            view.Typeface = Typeface.Create(light ? "sans-serif-light" : "sans-serif", TypefaceStyle.Normal);
            return view;
        }

        private int Dp(int dp) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, Resources!.DisplayMetrics);
    }
}
