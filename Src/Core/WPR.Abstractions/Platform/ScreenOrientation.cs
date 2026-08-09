namespace WPR.Abstractions.Platform;

/// <summary>
/// Backend-neutral display orientation. Distinct from the WP7/XNA
/// <c>DisplayOrientation</c> (an owned value type arriving in Stage 5a); the launcher
/// and platform layers speak this, and WPR.Framework.Xna maps between the two.
/// </summary>
public enum ScreenOrientation
{
    Portrait,
    PortraitDown,
    LandscapeLeft,
    LandscapeRight,
}
