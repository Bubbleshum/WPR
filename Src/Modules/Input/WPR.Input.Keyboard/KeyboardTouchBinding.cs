using System;
using System.Collections.Generic;

namespace WPR.Input.Keyboard
{
    /// <summary>What shape of touch a key produces.</summary>
    public enum KeyboardTouchGestureKind
    {
        /// <summary>Press and release at one point. Reaches a game as a Tap gesture and as a
        /// one-finger Pressed/Released pair in <c>TouchPanel.GetState()</c>.</summary>
        Tap,

        /// <summary>Press at the start point, travel to the end point, release. Reaches a game as
        /// a drag/flick gesture depending on how fast it is.</summary>
        Swipe,
    }

    /// <summary>
    /// One key bound to one synthetic touch gesture — "Q means swipe from (20,30) to (200,30)".
    ///
    /// <para><b>Coordinates are in WP7 display space</b> (<c>TouchPanel.DisplayWidth</c> x
    /// <c>DisplayHeight</c>, in practice 800x480), which is what makes them writable by hand: the
    /// backbuffer is fixed, so a point on screen is a point in the file. They are not normalised
    /// for the same reason — 0..1 fractions would be unreadable in a config a person edits.</para>
    ///
    /// <para><b>The trigger is a key NAME, not a key enum</b>, matching
    /// <see cref="KeyboardTiltBindings.ResolveKeyName"/>. That is what lets the same binding table
    /// serve a future gamepad source without being redesigned: a button name is just another
    /// string.</para>
    /// </summary>
    public sealed class KeyboardTouchBinding
    {
        /// <summary>Enum-name string of the triggering key, e.g. "Q". Case-insensitive.</summary>
        public string? Key { get; set; }

        public KeyboardTouchGestureKind Kind { get; set; }

        public float StartX { get; set; }
        public float StartY { get; set; }

        /// <summary>Ignored for <see cref="KeyboardTouchGestureKind.Tap"/>.</summary>
        public float EndX { get; set; }
        public float EndY { get; set; }

        /// <summary>
        /// How long the gesture takes. Clamped on use to at least two ticks' worth: a press and a
        /// release inside a single tick gives <c>GestureDetector</c> no frame in between, and a
        /// zero-length drag reads as a Tap rather than the swipe that was asked for.
        /// </summary>
        public int DurationMs { get; set; } = 120;

        public override string ToString() =>
            Kind == KeyboardTouchGestureKind.Tap
                ? $"{Key}: tap ({StartX},{StartY})"
                : $"{Key}: swipe ({StartX},{StartY})->({EndX},{EndY}) in {DurationMs}ms";
    }

    /// <summary>
    /// The active key-to-touch bindings.
    ///
    /// <para>Empty by default, which is the whole feature switched off: no bindings means the
    /// injector never has anything to play and the touch pipeline behaves exactly as before.
    /// Populated per game — a gesture's coordinates only mean anything against one game's
    /// layout.</para>
    /// </summary>
    public static class KeyboardTouchBindings
    {
        private static IReadOnlyList<KeyboardTouchBinding> _bindings = Array.Empty<KeyboardTouchBinding>();

        public static IReadOnlyList<KeyboardTouchBinding> Current => _bindings;

        public static bool Any => _bindings.Count > 0;

        /// <summary>Replaces the whole set. Null clears it.</summary>
        public static void Set(IReadOnlyList<KeyboardTouchBinding>? bindings) =>
            _bindings = bindings ?? (IReadOnlyList<KeyboardTouchBinding>)Array.Empty<KeyboardTouchBinding>();

        /// <summary>First binding whose key name matches, or null.</summary>
        public static KeyboardTouchBinding? Resolve(string? keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return null;

            IReadOnlyList<KeyboardTouchBinding> snapshot = _bindings;
            for (int i = 0; i < snapshot.Count; i += 1)
            {
                if (string.Equals(snapshot[i].Key, keyName, StringComparison.OrdinalIgnoreCase))
                {
                    return snapshot[i];
                }
            }
            return null;
        }
    }
}
