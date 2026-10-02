using System;
using System.IO;

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The WPR Hub logo in its two versions, both shipped as assets when the files exist (see the
    /// csproj):
    /// <list type="bullet">
    /// <item><see cref="Load"/>: the full-colour logo (<c>Images/wprhub.png</c>), for the hub pages,
    /// which are black.</item>
    /// <item><see cref="LoadWhite"/>: the white version (<c>Images/white_wpr_hub.png</c>), for the
    /// accent-coloured Start tile, where a white mark is what every Windows Phone tile carries.</item>
    /// </list>
    /// Without them the screens keep their "WPR HUB" text header and the tile its glyph. The Windows
    /// twin is <c>Views/HubLogo</c>.
    /// </summary>
    internal static class HubLogo
    {
        private static Bitmap? _colour, _white;
        private static bool _triedColour, _triedWhite;

        public static Bitmap? Load(Context context)
        {
            if (!_triedColour)
            {
                _triedColour = true;
                _colour = Decode(context, "hub/wpr-hub-logo.png");
                if (_colour != null) _colour = KeyOutBackground(_colour);
            }
            return _colour;
        }

        /// <summary>The white logo, already transparent around the mark.</summary>
        public static Bitmap? LoadWhite(Context context)
        {
            if (!_triedWhite)
            {
                _triedWhite = true;
                _white = Decode(context, "hub/wpr-hub-logo-white.png");
            }
            return _white;
        }

        private static Bitmap? Decode(Context context, string asset)
        {
            try
            {
                using Stream? stream = context.Assets?.Open(asset);
                // The sources are 1254 px and 1774 px wide; nothing shows them larger than ~400 px,
                // and full size would hold several MB of pixels for the life of the process.
                return stream == null ? null : BitmapFactory.DecodeStream(stream, null, new BitmapFactory.Options { InSampleSize = 2 });
            }
            catch (Exception)
            {
                return null; // not in this build
            }
        }

        /// <summary>
        /// The colour logo is drawn on a near-black square that is not quite even (0-12 per channel,
        /// lighter towards the top), so on the black hub pages its edge can show as a faint box. This
        /// makes the near-black transparent, ramping in so the glow keeps its soft edge. Runs once,
        /// over the half-size decode.
        /// </summary>
        private static Bitmap KeyOutBackground(Bitmap source)
        {
            const int Clear = 14, Solid = 44; // brightest channel at or below Clear is fully transparent
            int w = source.Width, h = source.Height;
            int[] pixels = new int[w * h];
            source.GetPixels(pixels, 0, w, 0, 0, w, h);
            for (int i = 0; i < pixels.Length; i++)
            {
                int p = pixels[i];
                int m = Math.Max((p >> 16) & 0xFF, Math.Max((p >> 8) & 0xFF, p & 0xFF));
                int alpha = m <= Clear ? 0 : m >= Solid ? 255 : (m - Clear) * 255 / (Solid - Clear);
                pixels[i] = (alpha << 24) | (p & 0xFFFFFF);
            }

            Bitmap keyed = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
            keyed.SetPixels(pixels, 0, w, 0, 0, w, h);
            source.Recycle();
            return keyed;
        }

        /// <summary>Shows the colour logo in <paramref name="image"/>, or <paramref name="textFallback"/> without one.</summary>
        public static void Apply(Context context, ImageView image, View? textFallback) =>
            Show(Load(context), image, textFallback);

        /// <summary>Shows the white logo in <paramref name="image"/>, or <paramref name="textFallback"/> without one.</summary>
        public static void ApplyWhite(Context context, ImageView image, View? textFallback) =>
            Show(LoadWhite(context), image, textFallback);

        private static void Show(Bitmap? logo, ImageView image, View? textFallback)
        {
            if (logo != null) image.SetImageBitmap(logo);
            image.Visibility = logo != null ? ViewStates.Visible : ViewStates.Gone;
            if (textFallback != null) textFallback.Visibility = logo != null ? ViewStates.Gone : ViewStates.Visible;
        }
    }
}
