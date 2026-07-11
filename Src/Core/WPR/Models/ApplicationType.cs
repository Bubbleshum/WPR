namespace WPR.Models
{
    public enum ApplicationType
    {
        XNA = 0,
        Silverlight = 1,
        ModernNative = 2,

        /// <summary>
        /// An externally-built native port (e.g. a Unity game rebuilt via AssetRipper) that WPR
        /// launches as a standalone binary rather than hosting in-process. The port target is
        /// described by a <c>wpr-port.json</c> (<see cref="WPR.UnityPortManifest"/>) in the
        /// install folder, which is the authoritative signal — a title is treated as a port
        /// whenever that manifest is present, regardless of this stored type.
        /// </summary>
        UnityPort = 3,
    }
}
