using System;
using Microsoft.Xna.Framework;
using WPR.Common;
using WPR.Xna.Rhi;

namespace WPR.Engine.Input
{
    /// <summary>
    /// Composes the keyboard-driven input emulation into a game run, if a head registered an
    /// <see cref="IKeyboardEmulationHost"/>. Two entry points, one per direction the emulation
    /// reaches the game: <see cref="AttachTo"/> for the tilt components on <c>Game.Components</c>,
    /// and <see cref="WrapInput"/> for the synthetic-touch decorator over the platform's
    /// <see cref="IInputBackend"/>.
    ///
    /// <para>This used to be a lambda in <c>WPR.Platform.Windows.XnaLauncher</c> passed down as an
    /// <c>Action&lt;Game&gt;</c>, then a static in <c>WPR.Backend.FNA</c>. It is engine code:
    /// nothing here knows which windowing backend is underneath, only that a game exists and a
    /// head may have registered an emulator. Both members are no-ops when none has — the normal
    /// case on Android, which has a real accelerometer and a real touchscreen.</para>
    /// </summary>
    public static class KeyboardEmulation
    {
        /// <summary>
        /// Attaches the tilt input component (and the overlay, when enabled) to a freshly
        /// constructed <see cref="Game"/>. Never throws: tilt emulation is a convenience, and an
        /// exception here would abort a launch that would otherwise have run fine without it.
        /// </summary>
        public static void AttachTo(Game game)
        {
            IKeyboardEmulationHost? host = XnaBackend.KeyboardEmulation;
            if (game == null || host == null) return;

            try
            {
                // Ordering preserved from the old launcher lambda: configuration is pushed into
                // the head's runtime knobs BEFORE the components are attached, so the overlay
                // flag we read below is the one the user last saved.
                host.PrepareForLaunch();
                game.Components.Add(new TiltInputXnaComponent(game, host));
                if (host.IsOverlayEnabled)
                {
                    game.Components.Add(new TiltOverlayXnaComponent(game, host));
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppLaunch, $"Failed to wire tilt input/overlay component: {ex.Message}");
            }
        }

        /// <summary>
        /// Layers the synthetic-touch injector over <paramref name="platformInput"/> when a head
        /// registered an emulator, and returns <paramref name="platformInput"/> unchanged when
        /// none did. The backend registers the result with <c>XnaBackend.SetInput</c>.
        ///
        /// <para>A decorator rather than a component because the injector has to be the LAST
        /// writer inside <c>UpdateTouchPanelState</c>, which only wrapping the drain can
        /// guarantee — see <see cref="SyntheticTouchInputBackend"/> for the trap.</para>
        /// </summary>
        public static IInputBackend WrapInput(IInputBackend platformInput)
        {
            if (platformInput == null) throw new ArgumentNullException(nameof(platformInput));
            IKeyboardEmulationHost? host = XnaBackend.KeyboardEmulation;
            return host == null ? platformInput : new SyntheticTouchInputBackend(platformInput, host);
        }
    }
}
