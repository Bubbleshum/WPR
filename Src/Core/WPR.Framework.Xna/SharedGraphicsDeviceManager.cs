using System;
using Microsoft.Xna.Framework.Graphics;

namespace Microsoft.Xna.Framework
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.SharedGraphicsDeviceManager</c>, from the WP7.1
    /// <c>Microsoft.Xna.Framework.Interop</c> assembly.
    ///
    /// <para>On the phone this is the device manager of a Silverlight/XNA <em>mixed-mode</em>
    /// app — one with no <see cref="Game"/> at all. It is declared in App.xaml as an
    /// <c>&lt;Application.ApplicationLifetimeObjects&gt;</c> entry, and the "shared" in the name
    /// means the GPU is shared between the Silverlight compositor and XNA, with
    /// <see cref="Graphics.GraphicsDeviceExtensions.SetSharingMode"/> deciding which of the two
    /// owns the backbuffer at any moment.</para>
    ///
    /// <para>Under WPR there is a real <see cref="Game"/> underneath (<c>MixedModeGame</c>) and
    /// XNA owns the screen outright, so this is a facade over that game's ordinary
    /// <see cref="GraphicsDeviceManager"/>. Nothing here creates or owns a device.</para>
    /// </summary>
    public class SharedGraphicsDeviceManager : IGraphicsDeviceService, IDisposable
    {
        // WP7's fixed hardware surface. Mirrors the clamp in the patched WP7
        // GraphicsDeviceManager, and applies unconditionally here because this type only
        // exists on the phone — there is no desktop XNA equivalent to stay compatible with.
        private const int PhoneLongDim = 800;
        private const int PhoneShortDim = 480;

        private static SharedGraphicsDeviceManager _Current;
        private static readonly object _Sync = new object();

        /// <summary>
        /// The real manager, shared by EVERY instance rather than held per-instance.
        /// </summary>
        /// <remarks>
        /// A launch has exactly one device, so this is faithful — but it is also load-bearing,
        /// because an app reliably ends up with TWO of these objects. The host constructs one in
        /// its own ctor (to bind a device before any game code runs), and parsing the game's
        /// App.xaml constructs a second from its <c>&lt;xna:SharedGraphicsDeviceManager /&gt;</c>
        /// lifetime-object entry. Only the first wins <see cref="Current"/> — but the second is
        /// the one the app registers as its <see cref="IGraphicsDeviceService"/>, which is what
        /// <c>ContentManager.GetGraphicsDevice</c> resolves through. With the manager held
        /// per-instance, every single <c>Content.Load</c> failed on the unattached copy while
        /// <c>Current.GraphicsDevice</c> worked, so the game booted and then could not load one
        /// texture.
        /// </remarks>
        private static GraphicsDeviceManager _Manager;

        private int _preferredWidth = PhoneShortDim;
        private int _preferredHeight = PhoneLongDim;
        private SurfaceFormat _preferredFormat = SurfaceFormat.Color;
        private DepthFormat _preferredDepthStencil = DepthFormat.Depth24Stencil8;
        private bool _synchronizeWithVerticalRetrace = true;

        /// <summary>
        /// Constructed by the XAML reader when it parses <c>&lt;xna:SharedGraphicsDeviceManager /&gt;</c>
        /// out of the game's App.xaml. The first instance to exist wins <see cref="Current"/>,
        /// which matches the phone: an app declares exactly one.
        /// </summary>
        public SharedGraphicsDeviceManager()
        {
            lock (_Sync)
            {
                if (_Current == null) _Current = this;
            }
        }

        /// <summary>
        /// The app's single manager.
        /// </summary>
        /// <remarks>
        /// Self-creating when nothing has constructed one yet. A mixed-mode title normally
        /// declares it in App.xaml, so by the time any game code reads this the XAML reader has
        /// already made it — but a title that reads <c>Current</c> from a static initialiser
        /// can get here first, and on the phone that returns a manager rather than null.
        /// </remarks>
        public static SharedGraphicsDeviceManager Current
        {
            get
            {
                lock (_Sync)
                {
                    if (_Current == null) _Current = new SharedGraphicsDeviceManager();
                    return _Current;
                }
            }
        }

        /// <summary>
        /// The live device. Backed by the host game's own manager.
        /// </summary>
        public GraphicsDevice GraphicsDevice
        {
            get
            {
                GraphicsDeviceManager m = _Manager;
                if (m == null)
                {
                    throw new InvalidOperationException(
                        "SharedGraphicsDeviceManager.GraphicsDevice was read before the mixed-mode " +
                        "host attached a device. The host attaches one before it boots the app, so " +
                        "this means game code reached the device from a static initialiser that ran " +
                        "earlier than the Application subclass.");
                }
                return m.GraphicsDevice;
            }
        }

        public int PreferredBackBufferWidth
        {
            get { return _preferredWidth; }
            set
            {
                _preferredWidth = ClampToPhoneSurface(value, _preferredHeight);
                if (_Manager != null) _Manager.PreferredBackBufferWidth = _preferredWidth;
            }
        }

        public int PreferredBackBufferHeight
        {
            get { return _preferredHeight; }
            set
            {
                _preferredHeight = ClampToPhoneSurface(value, _preferredWidth);
                if (_Manager != null) _Manager.PreferredBackBufferHeight = _preferredHeight;
            }
        }

        public SurfaceFormat PreferredBackBufferFormat
        {
            get { return _preferredFormat; }
            set
            {
                _preferredFormat = value;
                if (_Manager != null) _Manager.PreferredBackBufferFormat = value;
            }
        }

        public DepthFormat PreferredDepthStencilFormat
        {
            get { return _preferredDepthStencil; }
            set
            {
                _preferredDepthStencil = value;
                if (_Manager != null) _Manager.PreferredDepthStencilFormat = value;
            }
        }

        public bool SynchronizeWithVerticalRetrace
        {
            get { return _synchronizeWithVerticalRetrace; }
            set
            {
                _synchronizeWithVerticalRetrace = value;
                if (_Manager != null) _Manager.SynchronizeWithVerticalRetrace = value;
            }
        }

        /// <summary>
        /// Applies the preferred settings above to the real device.
        /// </summary>
        public void ApplyChanges()
        {
            if (_Manager == null) return;
            _Manager.ApplyChanges();
        }

        // IGraphicsDeviceService. A mixed-mode title can put this into Game.Services and have
        // its ContentManager resolve the device through it, exactly as an XNA title does.
        public event EventHandler<EventArgs> DeviceCreated;
        public event EventHandler<EventArgs> DeviceDisposing;
        public event EventHandler<EventArgs> DeviceReset;
        public event EventHandler<EventArgs> DeviceResetting;

        public void Dispose()
        {
            // Deliberately does NOT dispose the device or the underlying manager: neither is
            // ours. The host game owns both and tears them down with the launch.
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Host hook: bind this facade to the real manager, and push across anything game code
        /// set before the device existed.
        /// </summary>
        internal void Attach(GraphicsDeviceManager manager)
        {
            if (manager == null) throw new ArgumentNullException("manager");
            _Manager = manager;

            manager.PreferredBackBufferWidth = _preferredWidth;
            manager.PreferredBackBufferHeight = _preferredHeight;
            manager.PreferredBackBufferFormat = _preferredFormat;
            manager.PreferredDepthStencilFormat = _preferredDepthStencil;
            manager.SynchronizeWithVerticalRetrace = _synchronizeWithVerticalRetrace;
        }

        /// <summary>
        /// Host hook: raise <see cref="DeviceCreated"/> once the device exists. Separate from
        /// <see cref="Attach"/> because a subscriber added by the app between the two is still
        /// entitled to the event.
        /// </summary>
        internal void RaiseDeviceCreated()
        {
            EventHandler<EventArgs> handler = DeviceCreated;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// Drops both process-wide statics so the next launch builds its own. Without this the
        /// second mixed-mode game of a session inherits the first one's manager — and through it
        /// the first game's disposed device, which is worse than no device at all: reads succeed
        /// and then fail deep inside the driver.
        /// </summary>
        internal static void ResetForNewLaunch()
        {
            lock (_Sync)
            {
                _Current = null;
                _Manager = null;
            }
        }

        private static int ClampToPhoneSurface(int requested, int otherDim)
        {
            if (requested <= 0) return requested;
            int max = requested >= otherDim ? PhoneLongDim : PhoneShortDim;
            return requested > max ? max : requested;
        }
    }
}
