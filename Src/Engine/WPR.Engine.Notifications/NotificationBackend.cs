#nullable enable

namespace WPR.Engine.Notifications
{
    /// <summary>
    /// Registry for the platform's notification manager — the direct counterpart of
    /// <c>WPR.Engine.Sensors.SensorBackend</c> and <c>WPR.Engine.Audio.AudioTranscoderBackend</c>,
    /// and filled the same way: a head declares <c>caps.Notifications(...)</c> and the composition
    /// root writes it here.
    ///
    /// <para>Replaces <c>WPR.Common.NativeUI</c> (2026-09-01). That holder sat in the general
    /// utility assembly because there was nowhere better; it now sits with the rest of the
    /// notification API, and is named like every other subsystem registry rather than after the
    /// fact that it is native UI.</para>
    ///
    /// <para><b>Null is the normal unset state, not an error.</b> A platform that declares no
    /// manager simply shows no notifications — the achievement is still awarded and persisted,
    /// which is exactly what happened on Android for a long time when nothing assigned this at
    /// all. Consumers null-check; they must not assume a manager exists.</para>
    /// </summary>
    public static class NotificationBackend
    {
        /// <summary>The active manager, or null where the platform declared none.</summary>
        public static INotificationManager? Manager { get; private set; }

        /// <summary>True when a platform supplied a notification manager.</summary>
        public static bool HasManager => Manager != null;

        /// <summary>
        /// Registers the manager. Called once per process by the composition root; assignment
        /// rather than accumulation, because Android re-runs composition in its <c>:game</c>
        /// process. Passing null clears it, which is what the Windows head does when constructing
        /// its manager fails.
        /// </summary>
        public static void SetManager(INotificationManager? manager) => Manager = manager;
    }
}
