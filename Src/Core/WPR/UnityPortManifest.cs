using System;
using System.IO;

using Newtonsoft.Json;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Describes an externally-built native <em>port</em> that WPR launches instead of hosting
    /// in-process. Written as <c>wpr-port.json</c> at the root of a game's install folder
    /// (<c>%LocalAppData%\WPR\AppData\&lt;ProductId&gt;\</c>).
    ///
    /// <para>
    /// Background: a Unity Windows-Phone title installs with <c>RuntimeType="Silverlight"</c>, so
    /// WPR treats it as a Silverlight app and boots it to the shell — where it hits the
    /// "no Direct3D content" placeholder, because Unity's engine is native ARM code we can't host
    /// (see <c>Docs/Unity_WP8_Feasibility.md</c>). The supported route to a playable build is a
    /// one-time, per-game rebuild of the Unity project (via AssetRipper) into standalone Windows /
    /// Android binaries. Dropping those binaries plus this manifest into the install folder flips
    /// the game into "launch the port" mode on whichever platform WPR is running, so it still
    /// appears and launches from the WPR library like any other title.
    /// </para>
    ///
    /// <para>
    /// The manifest is the single source of truth for port dispatch: launchers probe for it in the
    /// install folder before falling back to the normal Silverlight / XNA hosts. This needs no EF
    /// schema change and no installer change — a title becomes a port the moment its manifest is
    /// present.
    /// </para>
    /// </summary>
    public class UnityPortManifest
    {
        /// <summary>Manifest file name, at the root of the game's install folder.</summary>
        public const string FileName = "wpr-port.json";

        /// <summary>Required value of <see cref="Type"/> for a Unity port.</summary>
        public const string UnityPortType = "unity-port";

        /// <summary>Manifest discriminator; must be <c>"unity-port"</c>.</summary>
        [JsonProperty("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Windows standalone executable to launch on the desktop host, relative to the install
        /// folder (e.g. <c>"port/Twins.exe"</c>). Null when there is no desktop build.
        /// </summary>
        [JsonProperty("windows")]
        public string? Windows { get; set; }

        /// <summary>Android launch target (Unity-as-a-Library host, or a separate APK). Null when there is no Android build.</summary>
        [JsonProperty("android")]
        public AndroidTarget? Android { get; set; }

        /// <summary>Android-specific port target.</summary>
        public class AndroidTarget
        {
            /// <summary>
            /// Android package id of the port. Used both as the Unity-as-a-Library activity's host
            /// package and (fallback) as the target for a launch-by-package intent.
            /// </summary>
            [JsonProperty("package")]
            public string? Package { get; set; }

            /// <summary>
            /// Fully-qualified activity to launch. Defaults to the package's launcher activity when
            /// unset; for a Unity-as-a-Library embed this is typically the bound
            /// <c>com.unity3d.player.UnityPlayerActivity</c>.
            /// </summary>
            [JsonProperty("activity")]
            public string? Activity { get; set; }

            /// <summary>
            /// APK bundled in the install folder (relative path) to offer for install on first
            /// launch when the port package isn't yet present on the device. Optional.
            /// </summary>
            [JsonProperty("apk")]
            public string? Apk { get; set; }
        }

        /// <summary>True when this manifest describes a Unity port.</summary>
        public bool IsUnityPort =>
            string.Equals(Type, UnityPortType, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Load the port manifest from an install folder, or null when there is none — the common
        /// case, i.e. a normal Silverlight / XNA title. Never throws; a malformed or non-port
        /// manifest logs a warning and returns null so the caller falls through to normal hosting.
        /// </summary>
        public static UnityPortManifest? TryLoad(string installFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(installFolder)) return null;
                string path = Path.Combine(installFolder, FileName);
                if (!File.Exists(path)) return null;

                var manifest = JsonConvert.DeserializeObject<UnityPortManifest>(File.ReadAllText(path));
                if (manifest == null || !manifest.IsUnityPort)
                {
                    Log.Warn(LogCategory.AppLaunch,
                        $"Ignoring {FileName} in '{installFolder}': missing or non-\"{UnityPortType}\" type");
                    return null;
                }
                return manifest;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppLaunch, $"Failed to read {FileName} in '{installFolder}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolve <see cref="Windows"/> to an absolute path, or null if unset or the file is
        /// missing. Guards against path traversal escaping the install folder.
        /// </summary>
        public string? ResolveWindowsExe(string installFolder)
        {
            if (string.IsNullOrEmpty(Windows)) return null;
            string full = Path.GetFullPath(Path.Combine(installFolder, Windows!));
            string root = Path.GetFullPath(installFolder);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(full) ? full : null;
        }
    }
}
