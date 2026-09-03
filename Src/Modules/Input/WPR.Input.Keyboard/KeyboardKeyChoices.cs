using System;
using System.Collections.Generic;

namespace WPR.Input.Keyboard
{
    /// <summary>
    /// The key names a binding may use, and the rule for comparing them.
    ///
    /// <para>Shared by every kind of keyboard binding — tilt directions, the Back button and touch
    /// gestures — which is why it is here rather than on any one of them. It sat on the tilt
    /// bindings until 2026-09-03, so the Back-key picker and the per-game gesture editor both had
    /// to reach into a type named for tilt to populate a dropdown.</para>
    /// </summary>
    public static class KeyboardKeyChoices
    {
        /// <summary>
        /// Curated picker list. Restricted to names that spell identically in
        /// <c>Avalonia.Input.Key</c> and <c>Microsoft.Xna.Framework.Input.Keys</c> — letters,
        /// arrows, the unmodified specials. Modifier keys differ between the two enums (Avalonia
        /// <c>LeftCtrl</c> vs XNA <c>LeftControl</c>) and are excluded, so one persisted value
        /// resolves under both the Silverlight and the XNA host.
        /// </summary>
        public static IReadOnlyList<string> Common { get; } = new[]
        {
            "A","B","C","D","E","F","G","H","I","J","K","L","M",
            "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
            "Left","Right","Up","Down",
            "Space","Tab","Escape",
            "NumPad0","NumPad1","NumPad2","NumPad3","NumPad4",
            "NumPad5","NumPad6","NumPad7","NumPad8","NumPad9",
        };

        /// <summary>
        /// Do these two key names refer to the same key? Case-insensitive, because the names are
        /// persisted as text and hand-edited config should not care about casing.
        /// </summary>
        public static bool Same(string? a, string? b) =>
            !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
