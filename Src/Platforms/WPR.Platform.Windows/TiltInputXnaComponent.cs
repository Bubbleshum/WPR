using System;
using Microsoft.Devices.Sensors;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// XNA <see cref="GameComponent"/> that polls <see cref="Keyboard.GetState"/> each Update
    /// and forwards transitions on the four configured tilt keys into
    /// <see cref="KeyboardAccelerometerHost"/>. Added to <c>Game.Components</c> by
    /// <see cref="XnaLauncher"/> right after the Game ctor.
    /// </summary>
    /// <remarks>
    /// Polling rather than event-driven because FNA's <c>Keyboard.GetState</c> is the only
    /// keyboard surface XNA games expect — there's no public key-down event on Game.
    /// UpdateOrder is negative so we run before the game's own Update reads keyboard state,
    /// keeping the simulated reading consistent with whatever the game sees on the same tick.
    /// </remarks>
    internal sealed class TiltInputXnaComponent : GameComponent
    {
        private bool _prevLeft, _prevRight, _prevForward, _prevBackward;

        public TiltInputXnaComponent(Game game) : base(game)
        {
            UpdateOrder = -1000;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            // Mirror the game's current orientation into the host so the screen-relative
            // intent (W = "tilt forward into the screen I see") gets rotated into the right
            // device-coord axis when the game is landscape.
            //
            // We can't just trust Window.CurrentOrientation: on desktop FNA only updates it
            // from SDL display-rotation events that never fire (a Windows desktop never
            // physically rotates), so a landscape WP7 game still reports Default. Fall back
            // to inferring orientation from the actual presentation viewport — matching the
            // same width-vs-height rule GraphicsDeviceManager2.RequestOrientationChange uses
            // to decide whether to ask the host to flip.
            KeyboardAccelerometerHost.Orientation = ResolveOrientation();

            // If the FNA window doesn't have focus, FNA reports all keys released
            // anyway, so polling is naturally safe across alt-tab.
            KeyboardState ks = Keyboard.GetState();
            bool left = false, right = false, forward = false, backward = false;

            foreach (XnaKeys k in ks.GetPressedKeys())
            {
                var dir = KeyboardTiltBinding.ResolveXnaKey(k);
                if (!dir.HasValue) continue;
                switch (dir.Value)
                {
                    case TiltDirection.Left:     left = true;     break;
                    case TiltDirection.Right:    right = true;    break;
                    case TiltDirection.Forward:  forward = true;  break;
                    case TiltDirection.Backward: backward = true; break;
                }
            }

            if (left     != _prevLeft)     KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Left,     left);
            if (right    != _prevRight)    KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Right,    right);
            if (forward  != _prevForward)  KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Forward,  forward);
            if (backward != _prevBackward) KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Backward, backward);

            _prevLeft = left;
            _prevRight = right;
            _prevForward = forward;
            _prevBackward = backward;
        }

        private Microsoft.Xna.Framework.DisplayOrientation ResolveOrientation()
        {
            // Prefer whatever the window reports IF it's set to a real orientation —
            // that gives mobile builds (where the event actually fires) the right answer.
            var win = Game?.Window;
            var co = win?.CurrentOrientation ?? Microsoft.Xna.Framework.DisplayOrientation.Default;
            if (co == Microsoft.Xna.Framework.DisplayOrientation.LandscapeLeft
             || co == Microsoft.Xna.Framework.DisplayOrientation.LandscapeRight
             || co == Microsoft.Xna.Framework.DisplayOrientation.Portrait)
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
                    return w > h
                        ? Microsoft.Xna.Framework.DisplayOrientation.LandscapeRight
                        : Microsoft.Xna.Framework.DisplayOrientation.Portrait;
                }
            }

            // Earliest ticks: GraphicsDevice hasn't been created yet. Fall back to the
            // window's client rect (always present once the SDL window exists).
            if (win != null)
            {
                var b = win.ClientBounds;
                if (b.Width > 0 && b.Height > 0)
                {
                    return b.Width > b.Height
                        ? Microsoft.Xna.Framework.DisplayOrientation.LandscapeRight
                        : Microsoft.Xna.Framework.DisplayOrientation.Portrait;
                }
            }

            return Microsoft.Xna.Framework.DisplayOrientation.Portrait;
        }
    }
}
