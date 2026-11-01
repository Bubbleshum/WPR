using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Wp8Native
{
    /// <summary>
    /// A WP8 "Modern Native" title as an XNA <see cref="Game"/>, so it gets the same window,
    /// graphics stack, input and lifecycle as any XNA title: GameActivity/SDL with FNA3D on
    /// Android, D3D11 on Windows. The title itself is a <see cref="Wp8NativeSession"/>; this class
    /// owns the window and the 60 Hz timestep and hands each turn to it.
    /// </summary>
    public sealed class Wp8NativeGame : Game, IWp8SessionHost
    {
        private readonly Wp8NativeSession _session;

        /// <param name="executable">The title's executable inside its install folder.</param>
        /// <param name="sandboxRoot">Where the title's local folder lives on the host.</param>
        /// <param name="log">Per-game trace sink; null to stay quiet.</param>
        public Wp8NativeGame(string executable, string sandboxRoot, Action<string>? log = null, IXboxLiveHost? xboxHost = null)
        {
            _session = new Wp8NativeSession(this, executable, sandboxRoot, log, xboxHost, bridged: false);
            _session.GuestFailed += failure => GuestFailed?.Invoke(failure);

            _ = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = FrameCapture.Width,
                PreferredBackBufferHeight = FrameCapture.Height,
                SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
                IsFullScreen = true,
            };

            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromTicks(166667);
        }

        /// <inheritdoc cref="Wp8NativeSession.Failure"/>
        public Wp8GuestFailure? Failure => _session.Failure;

        /// <inheritdoc cref="Wp8NativeSession.Outcome"/>
        public string? Outcome => _session.Outcome;

        /// <inheritdoc cref="Wp8NativeSession.GuestFailed"/>
        public event Action<Wp8GuestFailure>? GuestFailed;

        /// <inheritdoc cref="Wp8NativeSession.FailureReported"/>
        public bool FailureReported
        {
            get => _session.FailureReported;
            set => _session.FailureReported = value;
        }

        GraphicsDevice IWp8SessionHost.GraphicsDevice => GraphicsDevice;

        void IWp8SessionHost.Exit() => Exit();

        protected override void LoadContent()
        {
            base.LoadContent();
            _session.Start();
        }

        protected override void Update(GameTime gameTime)
        {
            _session.Update();
            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            _session.Draw();
            base.Draw(gameTime);
        }

        protected override void EndDraw()
        {
            base.EndDraw();
            _session.AfterDraw();
        }

        protected override void OnDeactivated(object sender, EventArgs args)
        {
            _session.Deactivated();
            base.OnDeactivated(sender, args);
        }

        protected override void OnActivated(object sender, EventArgs args)
        {
            _session.Activated();
            base.OnActivated(sender, args);
        }

        protected override void OnExiting(object sender, EventArgs args)
        {
            _session.Exiting();
            base.OnExiting(sender, args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _session.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
