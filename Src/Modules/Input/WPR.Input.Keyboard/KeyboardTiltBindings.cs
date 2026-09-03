using System;
using WPR.Common;

namespace WPR.Input.Keyboard
{
    /// <summary>
    /// Which keys tilt the emulated accelerometer, and in which direction.
    ///
    /// <para>Renamed from <c>KeyboardTiltBindings</c> on 2026-09-03 (plural, matching
    /// <see cref="KeyboardTouchBindings"/>) when the Back-key lookup moved to
    /// <see cref="KeyboardBackBinding"/> and the shared key list to
    /// <see cref="KeyboardKeyChoices"/>. What is left is genuinely only tilt.</para>
    /// </summary>
    /// <remarks>
    /// Bindings are persisted as enum-name strings, which keeps the Configuration JSON readable
    /// and lets each launcher resolve them against its own input enum — see
    /// <see cref="KeyboardKeyChoices.Common"/> for why one string works for both.
    /// </remarks>
    public static class KeyboardTiltBindings
    {
        /// <summary>
        /// Push the current Configuration values into the simulator's runtime knobs (sensitivity,
        /// master switch). Bindings themselves are looked up per key event rather than cached, so
        /// an edit in the Controls page takes effect on the running game without a restart.
        /// </summary>
        public static void ApplyConfigurationToHost()
        {
            var cfg = Configuration.Current;
            if (cfg == null) return;
            KeyboardAccelerometerHost.Sensitivity = cfg.TiltSensitivity;
            KeyboardAccelerometerHost.Enabled = cfg.TiltSimulationEnabled;
        }

        /// <summary>
        /// Try to match a key <b>name</b> — the enum-name string, exactly as persisted — to a
        /// configured tilt direction. Returns null if the name is not bound to a direction.
        ///
        /// <para>This takes a string rather than a key enum on purpose, and that is what let this
        /// module leave the Windows head. There used to be two methods here,
        /// <c>ResolveAvaloniaKey(Avalonia.Input.Key)</c> and <c>ResolveXnaKey(Keys)</c>, whose
        /// bodies were character-identical: both called <c>ToString()</c> on the enum and compared
        /// the result against the persisted name. The Avalonia parameter type was the only thing
        /// putting a UI framework in this file, and it bought nothing. Callers pass
        /// <c>key.ToString()</c> from whichever enum they hold.</para>
        /// </summary>
        public static TiltDirection? ResolveKeyName(string? name)
        {
            var cfg = Configuration.Current;
            if (cfg == null) return null;
            if (KeyboardKeyChoices.Same(name, cfg.TiltKeyLeft))     return TiltDirection.Left;
            if (KeyboardKeyChoices.Same(name, cfg.TiltKeyRight))    return TiltDirection.Right;
            if (KeyboardKeyChoices.Same(name, cfg.TiltKeyForward))  return TiltDirection.Forward;
            if (KeyboardKeyChoices.Same(name, cfg.TiltKeyBackward)) return TiltDirection.Backward;
            return null;
        }

        /// <summary>
        /// Convenience for the XNA polling path (a <c>KeyboardState</c> scan), which holds the
        /// enum rather than its name. Delegates to <see cref="ResolveKeyName"/>.
        /// </summary>
        public static TiltDirection? ResolveXnaKey(Microsoft.Xna.Framework.Input.Keys key)
            => ResolveKeyName(key.ToString());
    }
}
