using System;
using Microsoft.Xna.Framework;
using WPR.Common;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA.Input
{
    /// <summary>
    /// Attaches the keyboard-tilt emulator to a freshly constructed <see cref="Game"/>, if a head
    /// registered one.
    ///
    /// <para>This used to be a lambda in <c>WPR.Platform.Windows.XnaLauncher</c> passed down as an
    /// <c>Action&lt;Game&gt;</c>. That single parameter was why <b>both</b> heads carried an FNA
    /// reference — the Windows head for the components themselves, and the Android head purely
    /// because the call site named the ctor's full signature even though it never passed one.
    /// Moving the attach in here and dropping that parameter is what emptied
    /// <c>KnownBackendLeaks</c> (Stage 5, 2026-09-01).</para>
    /// </summary>
    internal static class KeyboardEmulation
    {
        /// <summary>
        /// No-op when no head registered an emulator — the normal case on Android, which has a
        /// real accelerometer. Never throws: tilt emulation is a convenience, and an exception
        /// here would abort a launch that would otherwise have run fine without it.
        /// </summary>
        internal static void AttachTo(Game game)
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
    }
}
