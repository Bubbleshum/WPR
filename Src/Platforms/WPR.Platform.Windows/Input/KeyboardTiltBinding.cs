using System;
using System.Collections.Generic;
using WPR.Common;
using AvKey = Avalonia.Input.Key;

namespace WPR.Platform.Windows.Input
{
    /// <summary>
    /// Maps a configured key name (the enum-name string we persist into Configuration)
    /// to either an Avalonia <see cref="AvKey"/> or an XNA
    /// <see cref="Microsoft.Xna.Framework.Input.Keys"/> depending on which host is asking,
    /// and dispatches the resulting press/release into <see cref="KeyboardAccelerometerHost"/>.
    /// </summary>
    /// <remarks>
    /// Storing the bindings as enum-name strings keeps the Configuration JSON readable and
    /// lets each launcher resolve them against its own input enum on launch. The two enums
    /// (Avalonia.Input.Key and XNA Keys) share most names verbatim — A/B/.../Left/Right/etc —
    /// so the same persisted value works for both hosts.
    /// </remarks>
    public static class KeyboardTiltBinding
    {
        /// <summary>
        /// Push the current Configuration values into the simulator's runtime knobs
        /// (sensitivity, master switch). Bindings are looked up on each key event rather
        /// than cached so that an edit in the Controls page takes effect on the running
        /// game without a restart.
        /// </summary>
        public static void ApplyConfigurationToHost()
        {
            var cfg = Configuration.Current;
            if (cfg == null) return;
            KeyboardAccelerometerHost.Sensitivity = cfg.TiltSensitivity;
            KeyboardAccelerometerHost.Enabled = cfg.TiltSimulationEnabled;
        }

        /// <summary>
        /// Try to match an Avalonia key (from a Silverlight host's KeyDown/KeyUp event) to a
        /// configured tilt direction. Returns null if the key isn't bound to any direction.
        /// </summary>
        public static TiltDirection? ResolveAvaloniaKey(AvKey key)
        {
            var cfg = Configuration.Current;
            if (cfg == null) return null;
            string name = key.ToString();
            if (Equals(name, cfg.TiltKeyLeft))     return TiltDirection.Left;
            if (Equals(name, cfg.TiltKeyRight))    return TiltDirection.Right;
            if (Equals(name, cfg.TiltKeyForward))  return TiltDirection.Forward;
            if (Equals(name, cfg.TiltKeyBackward)) return TiltDirection.Backward;
            return null;
        }

        /// <summary>
        /// Try to match an XNA <c>Keys</c> value (from a polled <c>KeyboardState</c>) to a
        /// configured tilt direction. Identical name semantics to <see cref="ResolveAvaloniaKey"/>.
        /// </summary>
        public static TiltDirection? ResolveXnaKey(Microsoft.Xna.Framework.Input.Keys key)
        {
            var cfg = Configuration.Current;
            if (cfg == null) return null;
            string name = key.ToString();
            if (Equals(name, cfg.TiltKeyLeft))     return TiltDirection.Left;
            if (Equals(name, cfg.TiltKeyRight))    return TiltDirection.Right;
            if (Equals(name, cfg.TiltKeyForward))  return TiltDirection.Forward;
            if (Equals(name, cfg.TiltKeyBackward)) return TiltDirection.Backward;
            return null;
        }

        /// <summary>
        /// Curated picker list. Restricted to names that share spelling between
        /// <see cref="AvKey"/> and <see cref="Microsoft.Xna.Framework.Input.Keys"/>
        /// — letters, arrows, the unmodified specials. Modifier keys differ in
        /// spelling between the two enums (Avalonia: <c>LeftCtrl</c>, XNA: <c>LeftControl</c>)
        /// and are excluded so a configured value works for both Silverlight and XNA hosts.
        /// </summary>
        public static IReadOnlyList<string> CommonChoices { get; } = new[]
        {
            "A","B","C","D","E","F","G","H","I","J","K","L","M",
            "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
            "Left","Right","Up","Down",
            "Space","Tab","Escape",
            "NumPad0","NumPad1","NumPad2","NumPad3","NumPad4",
            "NumPad5","NumPad6","NumPad7","NumPad8","NumPad9",
        };

        private static bool Equals(string a, string? b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
