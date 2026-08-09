using System;

namespace WPR.Diagnostics;

/// <summary>
/// The diagnostics sink. The runtime and layers above log through this rather than
/// writing files/console directly, so a launcher can route output (per-game
/// wpr_game_debug.log, IDE console, telemetry) by swapping the implementation.
/// </summary>
public interface IWprLog
{
    void Log(LogLevel level, string message, Exception? exception = null);

    void Trace(string message) => Log(LogLevel.Trace, message);
    void Info(string message) => Log(LogLevel.Info, message);
    void Warning(string message, Exception? exception = null) => Log(LogLevel.Warning, message, exception);
    void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
}
