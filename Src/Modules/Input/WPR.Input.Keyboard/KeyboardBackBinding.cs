using WPR.Common;

namespace WPR.Input.Keyboard
{
    /// <summary>
    /// The keyboard key that stands in for the WP7 hardware Back button.
    ///
    /// <para>Its own type since 2026-09-03. It lived on the tilt bindings, which made a type named
    /// for one emulated device answer questions about another — the same drift that renamed
    /// <c>ITiltEmulationHost</c> and this module.</para>
    ///
    /// <para>Resolved per key event rather than cached, so a Controls-page edit reaches a running
    /// game immediately. The phone's own Back keycode (<c>SDLK_AC_BACK</c>) and Android's system
    /// Back never come through here: a hardware button is not a preference.</para>
    /// </summary>
    public static class KeyboardBackBinding
    {
        /// <summary>True when this key name is the configured Back key. Default is Escape.</summary>
        public static bool IsBackKey(string? keyName) =>
            KeyboardKeyChoices.Same(keyName, Configuration.Current?.BackKey);

        /// <summary>Convenience for the XNA path, which holds the enum rather than its name.</summary>
        public static bool IsBackKey(Microsoft.Xna.Framework.Input.Keys key) =>
            IsBackKey(key.ToString());
    }
}
