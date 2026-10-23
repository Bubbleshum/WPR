using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WPR.Common;
using WPR.SilverlightCompability;
using WPR.Xna.Rhi;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace WPR
{
    /// <summary>
    /// The "achievement unlocked" pop-up (<see cref="AchievementToast"/>): a small dark banner at the
    /// top of the game's screen with the achievement's icon, "achievement unlocked" and
    /// "15G · name". It fades and slides in, stays for about three seconds, and fades out; several
    /// unlocks at once follow each other.
    /// </summary>
    /// <remarks>
    /// <para>Composed once per unlock on the CPU (the Silverlight <see cref="TextRasteriser"/>,
    /// the error screen's font) into one texture, plus the icon, and drawn with a SpriteBatch over
    /// the finished frame, so it looks the same on every driver. The device state the game left
    /// is put back afterwards, so a game that sets its state once and relies on it is unaffected.</para>
    /// <para>Never drawn while a render target is bound (only the backbuffer is the screen); the
    /// clock keeps running, so it can't get stuck.</para>
    /// </remarks>
    internal sealed class AchievementToastOverlay
    {
        private const double FadeIn = 0.25;
        private const double Hold = 3.0;
        private const double FadeOut = 0.35;
        private const double Total = FadeIn + Hold + FadeOut;

        private AchievementToastRequest? _current;
        private readonly Stopwatch _clock = new Stopwatch();
        private Texture2D? _panel;
        private Texture2D? _icon;
        private SpriteBatch? _batch;
        private int _builtFor; // backbuffer width*height the panel was built for

        public void Draw(GraphicsDevice device)
        {
            if (_current == null)
            {
                if (!AchievementToast.TryTake(out _current) || _current == null) return;
                _clock.Restart();
                DisposeTextures();
            }

            double t = _clock.Elapsed.TotalSeconds;
            if (t > Total)
            {
                _current = null;
                DisposeTextures();
                return;
            }

            if (device.GetRenderTargets().Length > 0) return;
            // The presented frame, which is the host backbuffer while a portrait backbuffer is
            // shown turned on a landscape window (PresentationTurn).
            Rectangle presented = device.WprPresentedBounds;
            int bw = presented.Width;
            int bh = presented.Height;
            if (bw <= 0 || bh <= 0) return;

            double unit = Math.Min(bw, bh) / 480.0;
            int panelW = (int)Math.Min(bw - 32 * unit, 340 * unit);
            int panelH = (int)Math.Round(64 * unit);
            if (_panel == null || _builtFor != bw * bh)
            {
                DisposeTextures();
                _builtFor = bw * bh;
                _icon = LoadIcon(device, _current.IconPath);
                _panel = new Texture2D(device, panelW, panelH, false, SurfaceFormat.Color);
                _panel.SetData(RenderPanel(_current, panelW, panelH, unit, hasIcon: _icon != null));
                _batch ??= new SpriteBatch(device);
            }

            // Ease in and out; the banner drops a little as it appears.
            double alpha = t < FadeIn ? Ease(t / FadeIn) : t > FadeIn + Hold ? Ease((Total - t) / FadeOut) : 1.0;
            double slide = t < FadeIn ? (1 - Ease(t / FadeIn)) * -12 * unit : 0;
            int x = (bw - panelW) / 2;
            int y = (int)Math.Round(16 * unit + slide);
            Color tint = Color.White * (float)alpha;

            // Put back everything a SpriteBatch changes that a game might rely on.
            BlendState blend = device.BlendState;
            DepthStencilState depth = device.DepthStencilState;
            RasterizerState raster = device.RasterizerState;
            SamplerState sampler = device.SamplerStates[0];
            Texture? texture = device.Textures[0];
            Viewport viewport = device.Viewport;
            Rectangle scissor = device.ScissorRectangle;
            try
            {
                device.Viewport = new Viewport(0, 0, bw, bh);
                _batch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
                _batch.Draw(_panel!, new Rectangle(x, y, panelW, panelH), tint);
                if (_icon != null)
                {
                    int pad = (int)Math.Round(8 * unit);
                    int size = panelH - 2 * pad;
                    _batch.Draw(_icon, new Rectangle(x + (int)Math.Round(4 * unit) + pad, y + pad, size, size), tint);
                }
                _batch.End();
            }
            finally
            {
                device.BlendState = blend;
                device.DepthStencilState = depth;
                device.RasterizerState = raster;
                device.SamplerStates[0] = sampler;
                device.Textures[0] = texture;
                device.Viewport = viewport;
                device.ScissorRectangle = scissor;
            }
        }

        public void Dispose()
        {
            DisposeTextures();
            _batch?.Dispose();
            _batch = null;
            _current = null;
        }

        private void DisposeTextures()
        {
            _panel?.Dispose();
            _panel = null;
            _icon?.Dispose();
            _icon = null;
            _builtFor = 0;
        }

        private static double Ease(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return 1 - (1 - x) * (1 - x);
        }

        private static Texture2D? LoadIcon(GraphicsDevice device, string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using FileStream stream = File.OpenRead(path);
                return Texture2D.FromStream(device, stream);
            }
            catch (Exception ex)
            {
                XnaBackend.LogWarn("[wpr-achievement] icon not loaded: " + ex.Message);
                return null;
            }
        }

        // ---- The banner, premultiplied and packed as XNA's Color ----

        internal static uint[] RenderPanel(AchievementToastRequest request, int w, int h, double unit, bool hasIcon)
        {
            uint[] px = new uint[w * h];
            Array.Fill(px, Premultiplied(20, 20, 24, 235));

            // The accent edge on the left, the WP way of marking something the system says.
            int edge = Math.Max(2, (int)Math.Round(4 * unit));
            uint accent = Accent();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < edge; x++)
                    px[y * w + x] = accent;

            int pad = (int)Math.Round(8 * unit);
            int iconSize = h - 2 * pad;
            // Without an icon the text moves up to where it would have been.
            double textX = edge + (hasIcon ? pad + iconSize : 0) + Math.Round(12 * unit);
            double textW = w - textX - pad;
            if (!TextRasteriser.IsAvailable || textW < 20) return px;

            var clip = new Rect(0, 0, w, h);
            double small = 12 * unit;
            double big = 17 * unit;
            double top = (h - TextRasteriser.GetLineHeight(small) - TextRasteriser.GetLineHeight(big)) / 2;
            TextRasteriser.Draw(px, w, h, "achievement unlocked", small, textX, top,
                Premultiplied(200, 200, 210, 255), clip);
            string line = request.Score.ToString(CultureInfo.InvariantCulture) + "G · " + request.Name;
            TextRasteriser.Draw(px, w, h, Fit(line, big, textW), big, textX, top + TextRasteriser.GetLineHeight(small),
                Premultiplied(255, 255, 255, 255), clip);
            return px;
        }

        /// <summary>The text cut to one line of <paramref name="width"/>, with an ellipsis if it had to be.</summary>
        private static string Fit(string text, double size, double width)
        {
            TextRasteriser.Measure(text, size, out double w, out _);
            if (w <= width) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                TextRasteriser.Measure(text.Substring(0, mid) + "…", size, out double mw, out _);
                if (mw <= width) lo = mid; else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + "…";
        }

        private static uint Accent()
        {
            string? hex = Configuration.Current?.AccentColor?.TrimStart('#');
            if (hex is { Length: 8 }) hex = hex.Substring(2); // AARRGGBB
            if (hex is { Length: 6 } && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                return Premultiplied((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            }
            return Premultiplied(27, 161, 226, 255); // WP cyan
        }

        private static uint Premultiplied(byte r, byte g, byte b, byte a) =>
            new Color(r * a / 255, g * a / 255, b * a / 255, (int)a).PackedValue;
    }
}
