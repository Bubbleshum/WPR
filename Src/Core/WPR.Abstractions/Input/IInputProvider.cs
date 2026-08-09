using System.Collections.Generic;
using System.Drawing;

namespace WPR.Abstractions.Input;

/// <summary>Lifecycle phase of a touch contact.</summary>
public enum TouchPhase
{
    Began,
    Moved,
    Stationary,
    Ended,
    Canceled,
}

/// <summary>A single touch contact at a point in time.</summary>
public readonly record struct TouchPoint(int Id, PointF Position, TouchPhase Phase);

/// <summary>
/// Platform-side raw input. A <see cref="WPR.Platform"/> provider implements it from
/// the OS; the XNA input reimplementation (<c>TouchPanel</c>/<c>Keyboard</c>/
/// <c>Mouse</c>/<c>GamePad</c>, Stage 5c) and gesture routing consume it. Virtual key
/// codes are plain ints here; the XNA layer maps them to owned <c>Keys</c> values.
///
/// Minimal scaffold — gamepad/gesture surface is added in Stage 5c.
/// </summary>
public interface IInputProvider
{
    /// <summary>The current set of active touch contacts.</summary>
    IReadOnlyList<TouchPoint> GetTouches();

    /// <summary>The current pointer/mouse position in client pixels.</summary>
    PointF PointerPosition { get; }

    /// <summary>Whether the given platform virtual-key code is currently pressed.</summary>
    bool IsKeyDown(int virtualKey);
}
