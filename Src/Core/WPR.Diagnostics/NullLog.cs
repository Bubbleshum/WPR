using System;

namespace WPR.Diagnostics;

/// <summary>A no-op sink. The default so unconfigured code never NREs on logging.</summary>
public sealed class NullLog : IWprLog
{
    public static readonly NullLog Instance = new();

    private NullLog() { }

    public void Log(LogLevel level, string message, Exception? exception = null) { }
}
