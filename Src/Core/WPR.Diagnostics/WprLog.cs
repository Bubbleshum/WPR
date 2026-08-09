using System;

namespace WPR.Diagnostics;

/// <summary>
/// Ambient access to the current diagnostics sink. A launcher sets
/// <see cref="Current"/> once at startup; everything else logs through it. Defaults
/// to <see cref="NullLog"/> so logging is always safe to call.
/// </summary>
public static class WprLog
{
    private static IWprLog _current = NullLog.Instance;

    public static IWprLog Current
    {
        get => _current;
        set => _current = value ?? NullLog.Instance;
    }
}
