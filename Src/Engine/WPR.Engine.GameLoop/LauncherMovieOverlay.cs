using System;
using System.IO;
using Microsoft.Phone.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Microsoft.Xna.Framework.Media;
using WPR.Xna.Media;
using WPR.Xna.Rhi;

namespace WPR
{
    /// <summary>
    /// Plays a <see cref="MediaPlayerLauncher"/> movie full screen, in place of the game.
    ///
    /// <para>On WP7, <c>Show()</c> handed the phone to the system media player: the app was
    /// deactivated, the movie played (letterboxed, landscape unless the title asked otherwise),
    /// and when it ended or the player pressed Back the app was activated again. Titles hang
    /// their next step on that reactivation (Star Wars: The Battle for Hoth loads its level from
    /// it), which is why <paramref name="onFinished"/> is the existing reactivation path
    /// unchanged. Before this the movie was simply skipped.</para>
    ///
    /// <para>Controls follow <see cref="MediaPlaybackControls"/>: Back always leaves (the
    /// hardware button always did), a tap skips when the title allowed Skip or Stop.</para>
    /// </summary>
    internal sealed class LauncherMovieOverlay : IHostOverlay
    {
        private readonly WprMoviePlayer _movie;
        private readonly bool _canSkip;
        private readonly bool _portrait;
        private readonly Action _onFinished;
        private readonly bool _resumeSong;
        private Texture2D? _texture;
        private SpriteBatch? _batch;
        private int _uploadedVersion = -1;
        // Input is armed only once nothing is held, so the tap or Back press that opened the movie
        // cannot also close it; after that, a press-and-release skips.
        private bool _touchArmed;
        private bool _touchWasDown;
        private bool _backArmed;
        private bool _finished;

        private LauncherMovieOverlay(WprMoviePlayer movie, bool canSkip, bool portrait, Action onFinished)
        {
            _movie = movie;
            _canSkip = canSkip;
            _portrait = portrait;
            _onFinished = onFinished;

            // The system player took the audio as well as the screen.
            if (MediaPlayer.State == MediaState.Playing)
            {
                try { MediaPlayer.Pause(); _resumeSong = true; } catch (Exception) { }
            }
            _movie.Play();
        }

        /// <summary>
        /// Starts the overlay for <paramref name="launcher"/>. False when there is nothing to play
        /// (no decoder, no file, an unplayable file); the caller then completes at once, which is
        /// what WPR always did.
        /// </summary>
        public static bool TryShow(MediaPlayerLauncher launcher, Action onFinished)
        {
            string? path = Resolve(launcher);
            if (path == null)
            {
                XnaBackend.LogInfo("[wpr-video] MediaPlayerLauncher: no file for " + launcher.Media + " (" + launcher.Location + ")");
                return false;
            }
            WprMoviePlayer? movie = WprMoviePlayer.TryOpen(path);
            if (movie == null) return false;

            bool canSkip = (launcher.Controls & (MediaPlaybackControls.Skip | MediaPlaybackControls.Stop)) != 0;
            HostOverlay.Show(new LauncherMovieOverlay(movie, canSkip, launcher.Orientation == MediaPlayerOrientation.Portrait, onFinished));
            return true;
        }

        private static string? Resolve(MediaPlayerLauncher launcher)
        {
            Uri? uri = launcher.Media;
            if (uri == null) return null;
            string rel = uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString;
            rel = rel.Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0) return null;

            string? root = null;
            string? productId = WPR.Common.WprHostEnvironment.CurrentProductId;
            if (launcher.Location == MediaLocationType.Data && productId != null)
            {
                root = WPR.WindowsCompability.PerGameIsolatedStorage.GameRoot(productId);
            }
            else
            {
                root = WPR.Common.WprHostEnvironment.CurrentInstallFolder;
            }
            if (root == null) return null;

            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return full;

            // An absolute file URI a title built itself.
            if (uri.IsAbsoluteUri && uri.IsFile && File.Exists(uri.LocalPath)) return uri.LocalPath;
            return null;
        }

        public bool Update()
        {
            if (_finished) return false;
            _movie.Update();
            if (_movie.Ended) return false;

            bool back = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed;
            if (!back) _backArmed = true;
            else if (_backArmed) return false;

            bool touch = false;
            foreach (TouchLocation t in TouchPanel.GetState())
            {
                if (t.State == TouchLocationState.Pressed || t.State == TouchLocationState.Moved)
                {
                    touch = true;
                }
            }
            if (!_touchArmed)
            {
                _touchArmed = !touch;
            }
            else
            {
                bool released = !touch && _touchWasDown;   // act on release, like a button
                _touchWasDown = touch;
                if (released && _canSkip) return false;
            }

            return true;
        }

        public void Draw(GraphicsDevice device)
        {
            if (_texture == null)
            {
                _texture = new Texture2D(device, _movie.Width, _movie.Height, false, SurfaceFormat.Color);
                _batch = new SpriteBatch(device);
            }
            if (_uploadedVersion != _movie.FrameVersion)
            {
                _texture.SetData(_movie.Frame);
                _uploadedVersion = _movie.FrameVersion;
            }

            RenderTargetBinding[] saved = device.GetRenderTargets();
            device.SetRenderTarget(null);
            device.Clear(Color.Black);

            int bw = device.PresentationParameters.BackBufferWidth;
            int bh = device.PresentationParameters.BackBufferHeight;

            // WP7's player shows a landscape movie landscape. When the game's backbuffer is
            // portrait, that means turning the picture a quarter so it fills the long side, as
            // the phone did when it rotated into the player.
            bool rotate = !_portrait && bh > bw && _movie.Width >= _movie.Height;
            if (rotate)
            {
                // Turned +90 degrees about the centre: the picture's top is the right edge, so a
                // desktop window stands it up by turning anticlockwise (PresentationTurn).
                WPR.Xna.Rhi.PresentationTurn.NoteTurnedFrame(WPR.Xna.Rhi.BackBufferTurn.Anticlockwise);
            }
            float targetW = rotate ? bh : bw;
            float targetH = rotate ? bw : bh;
            float scale = Math.Min(targetW / _movie.Width, targetH / _movie.Height);

            _batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            _batch.Draw(
                _texture,
                new Vector2(bw / 2f, bh / 2f),
                null,
                Color.White,
                rotate ? MathHelper.PiOver2 : 0f,
                new Vector2(_movie.Width / 2f, _movie.Height / 2f),
                scale,
                SpriteEffects.None,
                0f);
            _batch.End();

            if (saved.Length > 0)
            {
                device.SetRenderTargets(saved);
            }
        }

        public void Finish(bool completed)
        {
            if (_finished) return;
            _finished = true;
            XnaBackend.LogInfo($"[wpr-video] {_movie.Name} closed at {_movie.Position.TotalSeconds:0.0} s ({(_movie.Ended ? "ended" : "skipped")})");
            _movie.Dispose();
            _texture?.Dispose();
            _batch?.Dispose();
            if (!completed) return;
            if (_resumeSong)
            {
                try { MediaPlayer.Resume(); } catch (Exception) { }
            }
            _onFinished();
        }
    }
}
