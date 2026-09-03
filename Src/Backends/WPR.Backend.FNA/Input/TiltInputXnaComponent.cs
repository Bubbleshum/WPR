using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA.Input
{
    /// <summary>
    /// XNA <see cref="GameComponent"/> that polls <see cref="Keyboard.GetState"/> each Update and
    /// reports the held keys, plus the resolved display orientation, to the head's
    /// <see cref="IKeyboardEmulationHost"/>. Attached to <c>Game.Components</c> by
    /// <see cref="ApplicationLaunch"/> right after the Game ctor, and only when a head has
    /// registered an emulator.
    /// </summary>
    /// <remarks>
    /// <para>Polling rather than event-driven because FNA's <c>Keyboard.GetState</c> is the only
    /// keyboard surface XNA games expect — there's no public key-down event on Game.
    /// UpdateOrder is negative so we run before the game's own Update reads keyboard state,
    /// keeping the simulated reading consistent with whatever the game sees on the same tick.</para>
    ///
    /// <para><b>Lives here, not in the head</b> (moved 2026-09-01, Stage 5). It derives from a
    /// spine type, so the assembly holding it necessarily references FNA — which was the entire
    /// reason <c>WPR.Platform.Windows</c> was in <c>KnownBackendLeaks</c>. Meaning-of-a-key stays
    /// in the head behind <see cref="IKeyboardEmulationHost"/>; this class only knows how to read a
    /// keyboard and measure a viewport.</para>
    /// </remarks>
    internal sealed class TiltInputXnaComponent : GameComponent
    {
        private readonly IKeyboardEmulationHost _host;

        public TiltInputXnaComponent(Game game, IKeyboardEmulationHost host) : base(game)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            UpdateOrder = -1000;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            // Mirror the game's current orientation into the host so the screen-relative
            // intent (W = "tilt forward into the screen I see") gets rotated into the right
            // device-coord axis when the game is landscape.
            _host.ReportOrientation(ResolveOrientation());

            // If the FNA window doesn't have focus, FNA reports all keys released
            // anyway, so polling is naturally safe across alt-tab.
            KeyboardState ks = Keyboard.GetState();
            _host.ReportPressedKeys(ks.GetPressedKeys());

            // The Back key is deliberately NOT handled here. Polling GetState() drops a tap that
            // goes down and up inside one frame, which is most synthetic input and the occasional
            // fast human one. It is answered from the SDL keydown event instead — see
            // IKeyboardEmulationHost.IsBackKey and SDL2_FNAPlatform.PollEvents.
        }

        /// <summary>
        /// What the game is actually presenting at.
        ///
        /// <para>We can't just trust <c>Window.CurrentOrientation</c>: on desktop FNA only updates
        /// it from SDL display-rotation events that never fire (a Windows desktop never physically
        /// rotates), so a landscape WP7 game still reports Default. Fall back to inferring
        /// orientation from the actual presentation viewport — matching the same width-vs-height
        /// rule <c>GraphicsDeviceManager.RequestOrientationChange</c> uses to decide whether to ask
        /// the host to flip.</para>
        /// </summary>
        private DisplayOrientation ResolveOrientation()
        {
            // Prefer whatever the window reports IF it's set to a real orientation —
            // that gives mobile builds (where the event actually fires) the right answer.
            GameWindow? win = Game?.Window;
            DisplayOrientation co = win?.CurrentOrientation ?? DisplayOrientation.Default;
            if (co == DisplayOrientation.LandscapeLeft
             || co == DisplayOrientation.LandscapeRight
             || co == DisplayOrientation.Portrait)
            {
                return co;
            }

            // Desktop: read the actual presentation dimensions. Viewport survives an
            // un-applied PreferredBackBuffer* change so it's the one that matches what
            // the user actually sees on screen.
            var gd = Game?.GraphicsDevice;
            if (gd != null)
            {
                int w = gd.Viewport.Width;
                int h = gd.Viewport.Height;
                if (w > 0 && h > 0)
                {
                    return w > h ? DisplayOrientation.LandscapeRight : DisplayOrientation.Portrait;
                }
            }

            // Earliest ticks: GraphicsDevice hasn't been created yet. Fall back to the
            // window's client rect (always present once the SDL window exists).
            if (win != null)
            {
                Rectangle b = win.ClientBounds;
                if (b.Width > 0 && b.Height > 0)
                {
                    return b.Width > b.Height ? DisplayOrientation.LandscapeRight : DisplayOrientation.Portrait;
                }
            }

            return DisplayOrientation.Portrait;
        }
    }
}
