using System;

namespace Microsoft.Phone.Logging
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Logging.LogFlags</c>. Real WP toolkit uses this
    /// flags enum to gate categories of internal diagnostic logging. The exact bit
    /// layout isn't documented; we provide a generous superset. Our
    /// <see cref="Logger"/> ignores the flag — the type just needs to exist so
    /// PerfLog.BeginLogMarker (etc.) JITs.
    /// </summary>
    [Flags]
    public enum LogFlags : uint
    {
        None        = 0,
        General     = 1 << 0,
        Perf        = 1 << 1,
        Layout      = 1 << 2,
        Animation   = 1 << 3,
        Input       = 1 << 4,
        Navigation  = 1 << 5,
        All         = 0xFFFFFFFF,
    }

    /// <summary>
    /// Shim for <c>Microsoft.Phone.Logging.Logger</c>. All overloads are no-ops; we
    /// don't surface WP toolkit's internal diagnostics anywhere.
    /// </summary>
    public static class Logger
    {
        public static void Log(LogFlags flags, string message) { }
        public static void Log(LogFlags flags, string format, params object?[] args) { }
        public static void LogError(string message) { }
        public static void LogError(string format, params object?[] args) { }
        public static void LogWarning(string message) { }
        public static void LogWarning(string format, params object?[] args) { }
        public static bool IsEnabled(LogFlags flags) => false;

        /// <summary>
        /// WP Toolkit's internal perf-tracing entry point — <c>PerfLog.BeginLogMarker</c>
        /// and friends route through this. Real signature takes an event id, sub-event
        /// id, category flags, and a message. We drop all of it.
        /// </summary>
        public static void YLogEvent(uint eventId, uint subEventId, LogFlags flags, string message) { }
    }
}
