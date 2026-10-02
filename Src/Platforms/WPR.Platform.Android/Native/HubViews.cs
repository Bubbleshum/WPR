using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;

using WPR.Online.Hub;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The building blocks the WPR Hub screens (<see cref="HubActivity"/>, <see cref="SocialActivity"/>,
    /// <see cref="ConversationActivity"/>) make their rows from. Those pages are built in code
    /// because nearly everything on them is a list of data, so these are the "layout".
    /// </summary>
    internal static class HubViews
    {
        /// <summary>@color/wp_subtle, the secondary text colour the layouts use.</summary>
        public static readonly Color Subtle = Color.ParseColor("#99FFFFFF");

        public static int Dp(Context context, int dp) =>
            (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, context.Resources!.DisplayMetrics);

        public static TextView Text(Context context, string text, float sp, Color color, bool light = false)
        {
            TextView view = new TextView(context) { Text = text, TextSize = sp };
            view.SetTextColor(color);
            view.Typeface = Typeface.Create(light ? "sans-serif-light" : "sans-serif", TypefaceStyle.Normal);
            return view;
        }

        /// <summary>A flat WP button; <paramref name="accent"/> fills it with the accent colour.</summary>
        public static TextView Button(Context context, string label, bool accent = false)
        {
            TextView button = Text(context, label, 16, WpTheme.Foreground);
            button.SetBackgroundColor(accent ? WpTheme.Accent : WpTheme.Chrome);
            button.SetPadding(Dp(context, 18), Dp(context, 10), Dp(context, 18), Dp(context, 10));
            button.Clickable = true;
            button.Focusable = true;
            WpTheme.ApplyTilt(button);
            return button;
        }

        public static TextView Section(Context context, string title)
        {
            TextView header = Text(context, title, 13, Subtle);
            header.LetterSpacing = 0.08f;
            header.SetPadding(0, Dp(context, 26), 0, Dp(context, 6));
            return header;
        }

        public static TextView Hint(Context context, string text)
        {
            TextView hint = Text(context, text, 13, Subtle);
            hint.SetPadding(0, Dp(context, 6), 0, 0);
            return hint;
        }

        /// <summary>
        /// A player row: gamerpic, name, a second line, and a slot on the right for buttons.
        /// </summary>
        public static LinearLayout PersonRow(Activity activity, HubOnline hub, string? gamerpicUrl, string name, string line,
            Color lineColor, out LinearLayout trailing)
        {
            LinearLayout row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            row.SetPadding(0, Dp(activity, 8), 0, Dp(activity, 8));
            row.SetGravity(GravityFlags.CenterVertical);

            ImageView picture = new ImageView(activity) { ContentDescription = name };
            picture.SetScaleType(ImageView.ScaleType.CenterCrop);
            picture.SetBackgroundColor(WpTheme.Chrome);
            row.AddView(picture, new LinearLayout.LayoutParams(Dp(activity, 56), Dp(activity, 56)));
            if (!string.IsNullOrEmpty(gamerpicUrl)) _ = LoadImageAsync(activity, hub, picture, gamerpicUrl!);

            LinearLayout text = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            text.SetPadding(Dp(activity, 14), 0, Dp(activity, 8), 0);
            text.AddView(Text(activity, name, 22, WpTheme.Foreground, light: true));
            TextView second = Text(activity, line, 13, lineColor);
            second.SetMaxLines(2);
            second.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            text.AddView(second);
            row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            trailing = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            trailing.SetGravity(GravityFlags.CenterVertical);
            row.AddView(trailing);
            return row;
        }

        /// <summary>A hub image (a gamerpic) into <paramref name="target"/>, once it arrives. Cached by the account service.</summary>
        public static async Task LoadImageAsync(Activity activity, HubOnline hub, ImageView target, string url)
        {
            byte[]? bytes = await hub.Account.GetImageAsync(url);
            if (bytes == null || activity.IsDestroyed) return;
            Bitmap? bitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            if (bitmap != null) target.SetImageBitmap(bitmap);
        }

        /// <summary>The WPR Hub page header: logo (or "WPR HUB" text) and the page title.</summary>
        public static void Header(Activity activity, LinearLayout content, string title)
        {
            ImageView logo = new ImageView(activity) { ContentDescription = "WPR Hub" };
            logo.SetAdjustViewBounds(true);
            content.AddView(logo, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(activity, 64)));

            TextView appTitle = Text(activity, "WPR HUB", 14, WpTheme.Accent);
            appTitle.LetterSpacing = 0.1f;
            content.AddView(appTitle);
            HubLogo.Apply(activity, logo, appTitle);

            content.AddView(Text(activity, title, 56, WpTheme.Foreground, light: true));
        }
    }
}
