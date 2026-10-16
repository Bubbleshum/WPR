using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using WPR.Common;
using WPR.Engine.Online;
using WPR.SilverlightCompability;
using WPR.Xna.Rhi;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace WPR
{
    /// <summary>
    /// The error screen: a game's failure drawn where its black frame would have been, so the
    /// player (and a screenshot in a bug report) sees what happened. See <see cref="ErrorScreen"/>
    /// for when it is shown.
    /// </summary>
    /// <remarks>
    /// <para>Text is drawn by the Silverlight CPU <see cref="TextRasteriser"/> (a system TrueType
    /// font, no content and no SpriteFont), into one buffer the size of the backbuffer, uploaded
    /// once and blitted each frame. So it looks the same on every driver.</para>
    /// <para>The top of the screen is left empty: Android's "stop &amp; send report" button sits
    /// in the top-right corner over the game during a logging run.</para>
    /// <para>Back leaves (the hardware button on a phone, Escape on the desktop). Nothing else
    /// does, so a stray tap cannot dismiss the one screen that explains the failure.</para>
    /// </remarks>
    internal sealed class ErrorScreenOverlay : IHostOverlay
    {
        private readonly string _title;
        private readonly GameErrorReport _report;
        private readonly Action _exit;
        private Texture2D? _texture;
        private SpriteBatch? _batch;
        private bool _backArmed;
        private bool _exitRequested;

        public ErrorScreenOverlay(string title, GameErrorReport report, Action exit)
        {
            _title = title;
            _report = report;
            _exit = exit;
        }

        public bool Update()
        {
            bool back;
            try { back = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed; }
            catch (Exception) { back = false; }

            // Armed once Back is up, so a press already held when the screen appeared does not
            // dismiss it before it is seen.
            if (!back) _backArmed = true;
            else if (_backArmed && !_exitRequested)
            {
                _exitRequested = true;
                XnaBackend.LogInfo("[wpr-error-screen] Back pressed; leaving the game.");
                _exit();
            }
            return true;
        }

        public void Draw(GraphicsDevice device)
        {
            int bw = device.PresentationParameters.BackBufferWidth;
            int bh = device.PresentationParameters.BackBufferHeight;
            if (bw <= 0 || bh <= 0) return;

            if (_texture == null || _texture.Width != bw || _texture.Height != bh)
            {
                _texture?.Dispose();
                _texture = new Texture2D(device, bw, bh, false, SurfaceFormat.Color);
                _texture.SetData(Render(bw, bh));
                _batch ??= new SpriteBatch(device);
            }

            RenderTargetBinding[] saved = device.GetRenderTargets();
            device.SetRenderTarget(null);
            device.Clear(Color.Black);
            _batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            _batch.Draw(_texture, new Rectangle(0, 0, bw, bh), Color.White);
            _batch.End();
            if (saved.Length > 0) device.SetRenderTargets(saved);
        }

        public void Finish(bool completed)
        {
            _texture?.Dispose();
            _texture = null;
            _batch?.Dispose();
            _batch = null;
        }

        // ---- Layout ----

        private static readonly uint Background = new Color(18, 18, 24).PackedValue;
        private static readonly uint Accent = new Color(229, 20, 0).PackedValue;   // WP7 red
        private static readonly uint White = new Color(255, 255, 255).PackedValue;
        private static readonly uint Grey = new Color(170, 170, 180).PackedValue;
        private static readonly uint Dim = new Color(120, 120, 132).PackedValue;

        /// <summary>The whole screen, premultiplied and packed as XNA's Color.</summary>
        internal uint[] Render(int w, int h)
        {
            uint[] px = new uint[w * h];
            Array.Fill(px, Background);

            double unit = Math.Min(w, h) / 480.0;
            int margin = (int)Math.Round(24 * unit);
            int bar = Math.Max(2, (int)Math.Round(6 * unit));
            double textX = margin + bar + Math.Round(14 * unit);
            double textW = w - textX - margin;

            // The accent bar down the left, the WP7 way of marking a system screen.
            int top = (int)Math.Round(h * 0.10);
            FillRect(px, w, h, margin, top, bar, h - top - margin, Accent);

            if (!TextRasteriser.IsAvailable || textW < 40)
            {
                return px;   // no font on this device: the screen says "error" by colour alone
            }

            Rect all = new Rect(0, 0, w, h);
            double y = top;

            y = Text(px, w, h, _report.Stopped ? "the game stopped" : "the game hit an error", 18 * unit, textX, y, textW, Accent, all, maxLines: 1);
            y += 4 * unit;
            y = Text(px, w, h, _title, 30 * unit, textX, y, textW, White, all, maxLines: 2);
            y += 10 * unit;
            y = Text(px, w, h, _report.Headline, 19 * unit, textX, y, textW, White, all, maxLines: 4);
            if (!string.IsNullOrEmpty(_report.Where))
            {
                y += 6 * unit;
                y = Text(px, w, h, "at " + _report.Where, 15 * unit, textX, y, textW, Grey, all, maxLines: 2);
            }

            // The footer, from the bottom up, so the detail can take whatever is left.
            double footerSize = 19 * unit;
            double smallSize = 14 * unit;
            double footerTop = h - margin - TextRasteriser.GetLineHeight(footerSize) - 6 * unit - TextRasteriser.GetLineHeight(smallSize) * 2;
            Text(px, w, h, ReportLine(), smallSize, textX, footerTop, textW, Grey, all, maxLines: 2);
            Text(px, w, h, "press Back to exit", footerSize, textX, h - margin - TextRasteriser.GetLineHeight(footerSize), textW, White, all, maxLines: 1);

            if (!string.IsNullOrEmpty(_report.Detail))
            {
                y += 12 * unit;
                double bottom = footerTop - 10 * unit;
                if (bottom - y > TextRasteriser.GetLineHeight(13 * unit))
                {
                    Rect clip = new Rect(0, y, w, bottom - y);
                    int lines = (int)((bottom - y) / TextRasteriser.GetLineHeight(13 * unit));
                    Text(px, w, h, _report.Detail!, 13 * unit, textX, y, textW, Dim, clip, maxLines: lines);
                }
            }

            return px;
        }

        private string ReportLine()
        {
            bool loggingOn = OnlineBackend.Crashes != null && Configuration.Current?.ServerLogging == true;
            if (_report.Reported) return "Queued for WPR Hub as a crash report; it is sent when you leave the game.";
            if (loggingOn) return "This goes to WPR Hub with the run's other problems when you leave the game.";
            return "Server logging is off, so WPR Hub was not told. A screenshot of this screen helps a bug report.";
        }

        /// <summary>Draws wrapped text and returns the y below it. Lines beyond
        /// <paramref name="maxLines"/> are dropped, the last one ending in an ellipsis.</summary>
        private static double Text(uint[] px, int w, int h, string text, double size, double x, double y,
            double width, uint color, Rect clip, int maxLines)
        {
            if (string.IsNullOrEmpty(text) || maxLines <= 0) return y;
            string fitted = FitLines(text, size, width, maxLines);
            TextRasteriser.Draw(px, w, h, fitted, size, x, y, color, clip, width);
            TextRasteriser.Measure(fitted, size, out _, out double height, width);
            return y + height;
        }

        /// <summary>Keeps the text to <paramref name="maxLines"/> wrapped lines.</summary>
        private static string FitLines(string text, double size, double width, int maxLines)
        {
            double lineHeight = TextRasteriser.GetLineHeight(size);
            TextRasteriser.Measure(text, size, out _, out double height, width);
            if (height <= lineHeight * maxLines + 0.5) return text;

            // Too long: take whole source lines while they fit, then shorten the last one.
            var kept = new List<string>();
            foreach (string line in text.Split('\n'))
            {
                string candidate = string.Join("\n", kept) + (kept.Count > 0 ? "\n" : "") + line;
                TextRasteriser.Measure(candidate, size, out _, out double ch, width);
                if (ch <= lineHeight * maxLines + 0.5)
                {
                    kept.Add(line);
                    continue;
                }

                // Shorten this line until it fits with an ellipsis.
                string prefix = string.Join("\n", kept) + (kept.Count > 0 ? "\n" : "");
                int lo = 0, hi = line.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    TextRasteriser.Measure(prefix + line.Substring(0, mid) + "…", size, out _, out double mh, width);
                    if (mh <= lineHeight * maxLines + 0.5) lo = mid; else hi = mid - 1;
                }
                if (lo > 0) kept.Add(line.Substring(0, lo).TrimEnd() + "…");
                break;
            }
            return string.Join("\n", kept);
        }

        private static void FillRect(uint[] px, int w, int h, int x, int y, int rw, int rh, uint color)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
            int x1 = Math.Min(w, x + rw), y1 = Math.Min(h, y + rh);
            for (int row = y0; row < y1; row++)
            {
                int i = row * w;
                for (int col = x0; col < x1; col++) px[i + col] = color;
            }
        }
    }
}
