using System;
using System.IO;
using System.Reflection;
using WPR.Common;
using WPR.Engine.Online;
using WPR.Online.Hub;

namespace WPR.Shell
{
    /// <summary>
    /// Builds the WPR Hub module over <see cref="Configuration"/>, for both heads' platform
    /// descriptors. Shared because the settings are: only the platform name and how a game's
    /// package is found again differ, and those are the arguments.
    /// </summary>
    public static class HubSetup
    {
        /// <summary>
        /// Null when there is no configuration yet. Deliberately not a throw: this runs inside a
        /// platform descriptor, and PlatformComposition is two-phase, so a throw here would leave
        /// the whole platform uncomposed - no audio, no sensors - over an optional feature.
        /// </summary>
        public static HubOnline? TryCreate(string platform, IGamePackageSource? packages, IOnlineLocalStore? local)
        {
            if (Current != null) return Current;
            if (Configuration.Current is not { } config) return null;

            return Current = new HubOnline(new HubSettings
            {
                // Read live, so a changed setting applies without recomposing.
                BaseUri = () => Configuration.Current?.HubUri,
                AccessToken = () => Configuration.Current?.HubAccessToken,
                ServerLogging = () => Configuration.Current?.ServerLogging == true,
                AppearOffline = () => Configuration.Current?.HubAppearOffline == true,
                // In the data store beside AppData, so it follows a moved data folder and is
                // shared by Android's launcher and :game processes.
                DataDirectory = config.DataPath("Online"),
                WprVersion = Version(),
                Platform = platform,
                Packages = packages,
                Local = local,
                SignedOut = () =>
                {
                    if (Configuration.Current is { } current)
                    {
                        // Signed out, games go back to the guest gamertag and the default
                        // gamerpic (Configuration.EffectiveGamerTag / EffectiveGamerPicturePath).
                        current.HubAccessToken = null;
                        current.HubUsername = null;
                        current.GamerPicturePath = null;
                        current.Save();
                    }
                },
                SignedIn = (token, username) =>
                {
                    if (Configuration.Current is { } current)
                    {
                        // The hub gamertag is the in-game gamertag: games read it through
                        // Configuration.EffectiveGamerTag, so a rename on the hub follows through
                        // on the next refresh. There is no local gamertag to keep in step.
                        current.HubAccessToken = token;
                        current.HubUsername = username;
                        current.Save();
                    }
                },
                RecentLog = SessionLog.ReadRecent,
                // The hub gamerpic is the in-game gamer picture (GamerProfile.GetGamerPicture). There
                // is no local picture to protect any more, so it always follows the hub.
                GamerpicChanged = (path, explicitChoice) =>
                {
                    if (Configuration.Current is not { } current || current.GamerPicturePath == path) return;
                    current.GamerPicturePath = path;
                    current.Save();
                },
                // Both: Log writes to stdout (logcat on Android, discarded by the desktop WinExe),
                // while Trace reaches the per-game wpr_game_debug.log during a game run.
                Log = message =>
                {
                    WPR.Common.Log.Info(LogCategory.AppList, message);
                    System.Diagnostics.Trace.WriteLine(message);
                },
            });
        }

        /// <summary>
        /// The one instance for this process, once composed - what the settings pages reach sign-in
        /// through. Null before the platform is composed.
        /// </summary>
        public static HubOnline? Current { get; private set; }

        /// <summary>
        /// The signed-in account's gamerpic as the hub module last saved it, or null when there is
        /// none on disk. For the gamer card, which must draw without waiting on the network.
        /// </summary>
        public static string? SavedGamerpicFile()
        {
            Configuration? config = Configuration.Current;
            if (config == null || string.IsNullOrEmpty(config.HubAccessToken)) return null;
            string path = Path.Combine(config.DataPath("Online"), "gamerpic.png");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// The name the hub shows on its approval page and in the player's device list, so they
        /// can tell their devices apart: "WPR 0.1.05 on Windows (DESKTOP-AB12)".
        /// </summary>
        public static string ClientName(string device) => $"WPR {Version()} on {device}";

        /// <summary>The product version the UI shows ($(WprVersion) in Src/Directory.Build.props).</summary>
        /// <summary>This build's WPR version, as sent to the hub (e.g. <c>0.1.05</c>).</summary>
        public static string Version()
        {
            Assembly? entry = Assembly.GetEntryAssembly();
            string? informational = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                // Drop the "+<commit>" build metadata the SDK appends: the hub keys "fixed in" on it.
                int plus = informational!.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }
            return entry?.GetName().Version?.ToString() ?? "unknown";
        }
    }
}
