using System;
using Microsoft.Devices.Sensors;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// XNA <see cref="DrawableGameComponent"/> that paints a simple tilt indicator over
    /// the running game's framebuffer. Draws a circular dial in the top-right corner
    /// with a dot that tracks the current synthesized acceleration vector.
    /// </summary>
    /// <remarks>
    /// We can't easily render text without a SpriteFont (those come from a content
    /// pipeline), so we restrict the overlay to geometric primitives drawn with a 1×1
    /// white texture. <c>DrawOrder = int.MaxValue</c> keeps the overlay above whatever
    /// the user game draws.
    /// </remarks>
    internal sealed class TiltOverlayXnaComponent : DrawableGameComponent
    {
        private SpriteBatch? _batch;
        private Texture2D? _pixel;

        public TiltOverlayXnaComponent(Game game) : base(game)
        {
            DrawOrder = int.MaxValue;
            UpdateOrder = int.MaxValue;
        }

        protected override void LoadContent()
        {
            base.LoadContent();
            _batch = new SpriteBatch(GraphicsDevice);
            _pixel = new Texture2D(GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            _pixel.SetData(new[] { Color.White });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _batch?.Dispose();
                _pixel?.Dispose();
                _batch = null;
                _pixel = null;
            }
            base.Dispose(disposing);
        }

        public override void Draw(GameTime gameTime)
        {
            base.Draw(gameTime);
            if (_batch == null || _pixel == null) return;
            var gd = GraphicsDevice;
            if (gd == null) return;

            int vw = gd.Viewport.Width;
            int vh = gd.Viewport.Height;
            const int pad = 12;
            const int size = 110;
            int cx = vw - pad - size / 2;
            int cy = pad + size / 2;
            int radius = size / 2;

            // Screen-relative reading so the dot mirrors the user's key presses 1:1 — even
            // though the game itself is reading the rotated device-frame reading.
            var reading = KeyboardAccelerometerHost.CurrentScreenReading.Acceleration;

            _batch.Begin();
            // Dark backing rectangle behind the dial — easier than drawing a filled
            // circle from primitives and still clearly visible against bright games.
            FillRect(cx - radius, cy - radius, size, size, new Color(0, 0, 0, 180));
            // White border on the four edges.
            FillRect(cx - radius, cy - radius, size, 1, new Color(255, 255, 255, 128));
            FillRect(cx - radius, cy + radius - 1, size, 1, new Color(255, 255, 255, 128));
            FillRect(cx - radius, cy - radius, 1, size, new Color(255, 255, 255, 128));
            FillRect(cx + radius - 1, cy - radius, 1, size, new Color(255, 255, 255, 128));
            // Crosshair through the centre.
            FillRect(cx - radius + 4, cy, size - 8, 1, new Color(255, 255, 255, 96));
            FillRect(cx, cy - radius + 4, 1, size - 8, new Color(255, 255, 255, 96));
            // Tilt dot — clamp into the inner area so it doesn't escape the dial.
            int dotR = 5;
            int travel = radius - 8;
            int dotX = cx + (int)Math.Round(Math.Clamp(reading.X, -1f, 1f) * travel);
            int dotY = cy - (int)Math.Round(Math.Clamp(reading.Y, -1f, 1f) * travel);
            FillRect(dotX - dotR, dotY - dotR, dotR * 2, dotR * 2, new Color(64, 208, 112, 255));
            _batch.End();
        }

        private void FillRect(int x, int y, int w, int h, Color c)
        {
            _batch!.Draw(_pixel!, new Rectangle(x, y, w, h), c);
        }
    }
}
