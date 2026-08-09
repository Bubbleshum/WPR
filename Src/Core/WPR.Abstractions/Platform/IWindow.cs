using System;
using System.Drawing;

namespace WPR.Abstractions.Platform;

/// <summary>
/// The host window/surface. A <see cref="WPR.Platform"/> or backend provider
/// implements it; the XNA <c>GameWindow</c> reimplementation consumes it (Stage 5c),
/// replacing direct use of FNA's <c>Game.Window</c> (Title / native Handle /
/// orientation) in ApplicationLaunch.cs.
/// </summary>
public interface IWindow
{
    string Title { get; set; }

    /// <summary>Client area size in pixels.</summary>
    Size ClientSize { get; }

    /// <summary>Native window handle (HWND / ANativeWindow / etc.).</summary>
    nint Handle { get; }

    ScreenOrientation Orientation { get; }

    /// <summary>Raised when the client size changes.</summary>
    event Action? Resized;
}
