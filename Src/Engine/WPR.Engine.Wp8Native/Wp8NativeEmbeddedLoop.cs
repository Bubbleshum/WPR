using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WPR.Xna.Rhi;

namespace WPR.Wp8Native
{
    /// <summary>
    /// A Direct3D/XAML title's native component drawn under its own managed page: the
    /// <see cref="Wp8NativeSession"/> as the Silverlight host's embedded loop, the way the phone's
    /// compositor drew a <c>DrawingSurfaceBackgroundGrid</c>'s provider behind the page. The page
    /// (run by <c>SilverlightGame</c>) reaches the component through <see cref="Wp8NativeComponentHost"/>.
    /// </summary>
    public sealed class Wp8NativeEmbeddedLoop : IEmbeddedGameLoop, IWp8SessionHost, IDisposable
    {
        private GraphicsDevice? _device;

        /// <param name="component">The component DLL in the install folder.</param>
        public Wp8NativeEmbeddedLoop(string component, string sandboxRoot, Action<string>? log, IXboxLiveHost? xboxHost)
        {
            Session = new Wp8NativeSession(this, component, sandboxRoot, log, xboxHost, bridged: true);
            Host = new Wp8NativeComponentHost(Session);
        }

        public Wp8NativeSession Session { get; }

        /// <summary>What <c>WinRtComponentBridge.Host</c> is set to for the launch.</summary>
        public Wp8NativeComponentHost Host { get; }

        public bool ExitRequested { get; private set; }

        GraphicsDevice IWp8SessionHost.GraphicsDevice => _device ?? throw new InvalidOperationException("no frame drawn yet");

        void IWp8SessionHost.Exit() => ExitRequested = true;

        /// <summary>Starts the guest. Called before the page is built: its constructor already activates the component.</summary>
        public void Start()
        {
            // Called on the game thread, which is the page's UI thread.
            Session.BridgeUiThread = Thread.CurrentThread;
            Session.Start();
        }

        public void Update(GameTime gameTime) => Session.Update();

        public void Draw(GameTime gameTime, GraphicsDevice device)
        {
            _device = device;
            Session.Draw();
            Session.AfterDraw();
        }

        public void SetActive(bool active)
        {
            if (active)
            {
                Session.Activated();
            }
            else
            {
                Session.Deactivated();
            }
        }

        public void Dispose()
        {
            Session.Exiting();
            Session.Dispose();
        }
    }
}
