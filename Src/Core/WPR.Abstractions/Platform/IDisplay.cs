using System.Drawing;

namespace WPR.Abstractions.Platform;

/// <summary>
/// Physical display metrics. Backs the XNA <c>GraphicsAdapter</c>/<c>DisplayMode</c>
/// reimplementation (Stage 5c) and orientation reporting. On desktop, orientation is
/// inferred from the backbuffer (see the desktop CurrentOrientation handling that
/// currently lives in FNA's FNAWindow).
/// </summary>
public interface IDisplay
{
    /// <summary>Display bounds in pixels.</summary>
    Rectangle Bounds { get; }

    /// <summary>Dots per inch.</summary>
    float Dpi { get; }

    ScreenOrientation Orientation { get; }
}
