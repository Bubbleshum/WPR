using System;
using System.IO;
using Newtonsoft.Json;

namespace WPR.Common
{
    public class Configuration
    {
        private class ConfigurationPrivate
        {
            public string DataStorePath;
            // No longer read: the gamertag is the WPR Hub one or the guest name (EffectiveGamerTag).
            // Kept so old config.json files still load.
            public String GamerTag;
            // The WPR Hub gamerpic the hub module saved, while signed in (EffectiveGamerPicturePath).
            // Consumed by GamerProfile.GetGamerPicture(), which is what WP7 titles like Fruit Ninja
            // call via Texture2D.FromStream. There is no longer a local picture to choose.
            public string? GamerPicturePath;
            public string? RegistrationToken;
            public string? UserEmail;
            public bool IsRegistered;
            public string? GameLibraryPath;
            // WP7 system accent color, persisted as "#AARRGGBB" hex (with leading '#').
            // Null = use the WP7 default "Cyan" (#FF1BA1E2). Consumed by
            // WPR.SilverlightCompability.PhoneTheme on launch.
            public string? AccentColor;

            // Keyboard → accelerometer simulation. Persisted as enum-name strings to keep
            // the JSON readable; values map to Microsoft.Xna.Framework.Input.Keys for the
            // XNA host and are translated to Avalonia.Input.Key for the Silverlight host.
            // Null = use the default WASD layout.
            public string? TiltKeyLeft;
            public string? TiltKeyRight;
            public string? TiltKeyForward;
            public string? TiltKeyBackward;
            // Per-axis peak acceleration in g-units when a direction key is held. Default
            // 0.7 ≈ sin(45°). Use null to fall back to the default.
            public double? TiltSensitivity;
            // When true, an on-screen tilt indicator overlays the running game.
            public bool TiltOverlayEnabled;
            // Master switch for keyboard accelerometer simulation. Default = true.
            public bool? TiltSimulationEnabled;
            // Keyboard key bound to the WP7 hardware Back button. Same enum-name convention as
            // the tilt keys above. Null = the default, Escape.
            //
            // Note the phone's own Back keycode (SDLK_AC_BACK, and Android's system Back via
            // WprPhoneBackButton) is NOT configurable and never passes through here — that is a
            // hardware button, not a preference. This binding only names the desktop stand-in.
            public string? BackKey;

            // Master switch for game vibration, across every game and every vibration path —
            // the WP7 handset motor (Microsoft.Devices.VibrateController) and XNA gamepad
            // rumble (GamePad.SetVibration). Default = true.
            //
            // Nullable so that a config.json written before this setting existed reads as "on"
            // rather than as an explicit "off"; same reason TiltSimulationEnabled is nullable
            // and TiltOverlayEnabled (which defaults off) is not.
            public bool? VibrationEnabled;

            // Which FNA3D graphics driver games are launched with, as the driver's own name
            // ("Vulkan" / "OpenGL"). Null = whatever the platform declares for itself, which is
            // what every existing config.json says and what the vast majority of devices should
            // keep — this is an escape hatch, not a preference.
            //
            // A string rather than an enum because WPR.Common must not reference
            // WPR.Engine.Graphics; the head that consumes it maps the name onto GraphicsDriver.
            public string? GraphicsDriver;

            // WPR Hub (the online backend). All three are read live by WPR.Online.Hub, so a change
            // applies without restarting WPR.
            //
            // HubUrl: null = the official hub (DefaultHubUrl).
            public string? HubUrl;
            // The device token from hub sign-in. Null = signed out; leaderboard scores still queue
            // locally and go up the first time a token appears.
            public string? HubAccessToken;
            // The hub username that token belongs to, for display only (the hub is the truth).
            public string? HubUsername;
            // Automatic crash reports, and uploading any game package the hub has never seen
            // (at install and after a crash). Nullable so an absent key reads as the default,
            // ON - the TiltSimulationEnabled precedent. Named ...Enabled rather than
            // ServerLogging because a build of 2026-09-27 wrote "ServerLogging": false into
            // config.json as its off-by-default, and that must not read as a choice.
            public bool? ServerLoggingEnabled;
            // Appear offline to WPR Hub friends. Null (absent) = no.
            public bool? HubAppearOffline;
            // Minutes between "how did it run?" questions. Null (absent) = the default (60);
            // 0 = never ask; -1 = after every game.
            public int? RatingPromptCooldownMinutes;
            // Look for a newer WPR release on GitHub and announce it (WPR.Shell.AppUpdates).
            // Null (absent) = on, the TiltSimulationEnabled precedent.
            public bool? UpdateCheckEnabled;
            // The gamertag games see while signed out of WPR Hub: "player" and six digits, made
            // once per install (see EffectiveGamerTag).
            public string? GuestGamerTag;
        };

        private const string ConfigurationFilePath = "config.json";

        private string PrivateDataFolderPath;
        private ConfigurationPrivate? _ConfPrivate;

        public string? DataStorePath
        {
            get => _ConfPrivate!.DataStorePath;
            set => _ConfPrivate!.DataStorePath = value;
        }

        /// <summary>No longer read; see <see cref="EffectiveGamerTag"/>.</summary>
        public string? GamerTag
        {
            get => _ConfPrivate!.GamerTag;
            set => _ConfPrivate!.GamerTag = value;
        }

        public string? GamerPicturePath
        {
            get => _ConfPrivate!.GamerPicturePath;
            set => _ConfPrivate!.GamerPicturePath = string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>Signed in to WPR Hub: a device token is held.</summary>
        public bool IsHubSignedIn => !string.IsNullOrEmpty(HubAccessToken);

        /// <summary>
        /// The gamertag games see. Signed in it is the WPR Hub gamertag, chosen at sign-up; signed
        /// out it is this install's <see cref="GuestGamerTag"/>. There is no gamertag setting: the
        /// old <see cref="GamerTag"/> value is no longer read.
        /// </summary>
        public string EffectiveGamerTag =>
            IsHubSignedIn && !string.IsNullOrWhiteSpace(HubUsername) ? HubUsername! : GuestGamerTag;

        /// <summary>
        /// The gamer picture games see: the WPR Hub gamerpic when signed in (saved by the hub
        /// module into <see cref="GamerPicturePath"/>), and null signed out, which makes
        /// <c>GamerProfile.GetGamerPicture</c> hand back the bundled default.
        /// </summary>
        public string? EffectiveGamerPicturePath => IsHubSignedIn ? GamerPicturePath : null;

        /// <summary>
        /// "player" and six digits, e.g. <c>player482913</c>, made once per install and kept.
        /// WPR Hub never lets a real account take a "player" + digits name (and its own
        /// generated names were four digits), so this can never be mistaken for a real player.
        /// It stays on the device: scores made while signed out are sent under the account at
        /// the next sign-in, never under this name.
        /// </summary>
        public string GuestGamerTag
        {
            get
            {
                if (string.IsNullOrEmpty(_ConfPrivate!.GuestGamerTag))
                {
                    _ConfPrivate.GuestGamerTag = "player" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000);
                }
                return _ConfPrivate.GuestGamerTag!;
            }
        }

        public string? RegistrationToken
        {
            get => _ConfPrivate!.RegistrationToken;
            set => _ConfPrivate!.RegistrationToken = value;
        }

        public string? UserEmail
        {
            get => _ConfPrivate!.UserEmail;
            set => _ConfPrivate!.UserEmail = value;
        }

        public bool IsRegistered
        {
            get => _ConfPrivate!.IsRegistered;
            set => _ConfPrivate!.IsRegistered = value;
        }

        public string? GameLibraryPath
        {
            get => _ConfPrivate!.GameLibraryPath;
            set
            {
                if (_ConfPrivate!.GameLibraryPath == value) return;
                _ConfPrivate!.GameLibraryPath = value;
                GameLibraryPathChanged?.Invoke(this, value);
            }
        }

        public string? AccentColor
        {
            get => _ConfPrivate!.AccentColor;
            set => _ConfPrivate!.AccentColor = value;
        }

        // Default WASD layout — A/D tilt left/right, W/S tilt the top edge away/toward
        // the user. Values are Microsoft.Xna.Framework.Input.Keys enum names.
        public const string DefaultTiltKeyLeft = "A";
        public const string DefaultTiltKeyRight = "D";
        public const string DefaultTiltKeyForward = "W";
        public const string DefaultTiltKeyBackward = "S";
        public const double DefaultTiltSensitivity = 0.7;
        /// <summary>Desktop stand-in for the WP7 hardware Back button. "Escape" preserves the
        /// behaviour that was hardcoded in SDL2_FNAPlatform.PollEvents until 2026-09-03.</summary>
        public const string DefaultBackKey = "Escape";

        public string TiltKeyLeft
        {
            get => _ConfPrivate!.TiltKeyLeft ?? DefaultTiltKeyLeft;
            set => _ConfPrivate!.TiltKeyLeft = string.IsNullOrEmpty(value) ? null : value;
        }
        public string TiltKeyRight
        {
            get => _ConfPrivate!.TiltKeyRight ?? DefaultTiltKeyRight;
            set => _ConfPrivate!.TiltKeyRight = string.IsNullOrEmpty(value) ? null : value;
        }
        public string TiltKeyForward
        {
            get => _ConfPrivate!.TiltKeyForward ?? DefaultTiltKeyForward;
            set => _ConfPrivate!.TiltKeyForward = string.IsNullOrEmpty(value) ? null : value;
        }
        public string TiltKeyBackward
        {
            get => _ConfPrivate!.TiltKeyBackward ?? DefaultTiltKeyBackward;
            set => _ConfPrivate!.TiltKeyBackward = string.IsNullOrEmpty(value) ? null : value;
        }
        public double TiltSensitivity
        {
            get => _ConfPrivate!.TiltSensitivity ?? DefaultTiltSensitivity;
            set => _ConfPrivate!.TiltSensitivity = value;
        }
        public bool TiltOverlayEnabled
        {
            get => _ConfPrivate!.TiltOverlayEnabled;
            set => _ConfPrivate!.TiltOverlayEnabled = value;
        }
        public bool TiltSimulationEnabled
        {
            get => _ConfPrivate!.TiltSimulationEnabled ?? true;
            set => _ConfPrivate!.TiltSimulationEnabled = value;
        }
        public string BackKey
        {
            get => _ConfPrivate!.BackKey ?? DefaultBackKey;
            set => _ConfPrivate!.BackKey = string.IsNullOrEmpty(value) ? null : value;
        }
        /// <summary>
        /// Whether games may vibrate the device. Defaults to true. Read through
        /// <c>WPR.Engine.Vibration.VibrationBackend.IsEnabled</c> rather than directly, so both
        /// vibration paths share one answer — see that property.
        /// </summary>
        public bool VibrationEnabled
        {
            get => _ConfPrivate!.VibrationEnabled ?? true;
            set => _ConfPrivate!.VibrationEnabled = value;
        }
        /// <summary>
        /// The FNA3D graphics driver to launch games with, by the driver's own name
        /// (<c>"Vulkan"</c> / <c>"OpenGL"</c>), or <b>null to leave the platform's own choice
        /// alone</b> — which is the default and what every device should stay on unless games
        /// fail to start on it.
        ///
        /// <para>Null is deliberately distinct from any name: a head reads this and only
        /// substitutes its declaration when something is set, so an untouched install behaves
        /// exactly as it did before this setting existed.</para>
        /// </summary>
        public string? GraphicsDriver
        {
            get => _ConfPrivate!.GraphicsDriver;
            set => _ConfPrivate!.GraphicsDriver = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        /// <summary>The official WPR Hub, used unless the player sets another.</summary>
        public const string DefaultHubUrl = "https://wpr.it-stacks.com/";

        /// <summary>
        /// WPR Hub's base URL. Reads as <see cref="DefaultHubUrl"/> when unset; assigning empty (or
        /// the default itself) goes back to following the default, so a future change of official
        /// hub is not pinned by an old config.json.
        /// </summary>
        public string HubUrl
        {
            // Normalised on read too: a build of 2026-09-27 stored whatever was typed, bare hosts included.
            get => NormaliseHubUrl(_ConfPrivate!.HubUrl) ?? DefaultHubUrl;
            set
            {
                string? normalised = NormaliseHubUrl(value);
                _ConfPrivate!.HubUrl = normalised == null || normalised.TrimEnd('/') == DefaultHubUrl.TrimEnd('/') ? null : normalised;
            }
        }

        /// <summary>
        /// <see cref="HubUrl"/> as a URI, or null when it cannot be made into an http(s) one. A
        /// bare host (<c>wpr.it-stacks.com</c>) is read as https: that is what people type into
        /// the box, and rejecting it silently switched every online feature off.
        /// </summary>
        public Uri? HubUri =>
            Uri.TryCreate(NormaliseHubUrl(HubUrl), UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri
                : null;

        /// <summary>Trimmed, with <c>https://</c> added when no scheme was typed. Null for blank.</summary>
        private static string? NormaliseHubUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value!.Trim();
            return trimmed.Contains("://") ? trimmed : "https://" + trimmed.TrimStart('/');
        }

        /// <summary>The WPR Hub device token, or null when signed out.</summary>
        public string? HubAccessToken
        {
            get => _ConfPrivate!.HubAccessToken;
            set => _ConfPrivate!.HubAccessToken = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        /// <summary>The signed-in hub username, for display. Null when signed out.</summary>
        public string? HubUsername
        {
            get => _ConfPrivate!.HubUsername;
            set => _ConfPrivate!.HubUsername = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// Automatic crash reports to WPR Hub, and uploading any game package the hub has no copy
        /// of (at install, and after a crash). <b>On by default</b>; the settings pages turn it off.
        /// </summary>
        public bool ServerLogging
        {
            get => _ConfPrivate!.ServerLoggingEnabled ?? true;
            set => _ConfPrivate!.ServerLoggingEnabled = value;
        }

        /// <summary>Default for <see cref="RatingPromptCooldownMinutes"/>: one question an hour.</summary>
        public const int DefaultRatingPromptCooldownMinutes = 60;

        /// <summary>
        /// How long WPR waits after asking "how did it run?" before it asks about another game, in
        /// minutes; <b>0 means never ask, -1 after every game</b>. Chosen on both settings pages. Stored as null while it is
        /// the default, so a change of default reaches players who never touched it.
        /// </summary>
        public int RatingPromptCooldownMinutes
        {
            get => _ConfPrivate!.RatingPromptCooldownMinutes is { } m && m >= -1 ? m : DefaultRatingPromptCooldownMinutes;
            set => _ConfPrivate!.RatingPromptCooldownMinutes = value == DefaultRatingPromptCooldownMinutes ? null : Math.Max(-1, value);
        }

        /// <summary>
        /// Appear offline to WPR Hub friends: they see the player as offline, last seen when they were
        /// last visibly online. Read live by the presence heartbeat. Off by default.
        /// </summary>
        public bool HubAppearOffline
        {
            get => _ConfPrivate!.HubAppearOffline == true;
            set => _ConfPrivate!.HubAppearOffline = value ? true : null;
        }

        /// <summary>
        /// Whether WPR looks for a newer release on GitHub and tells the player about it. On by
        /// default; stored as null when on, so a config.json written before this existed reads as on.
        /// </summary>
        public bool UpdateCheckEnabled
        {
            get => _ConfPrivate!.UpdateCheckEnabled != false;
            set => _ConfPrivate!.UpdateCheckEnabled = value ? null : false;
        }

        public static event EventHandler<string?>? GameLibraryPathChanged;

        public static Configuration? Current { get; set; }

        private string ConfigurationFilePathFull => Path.Combine(PrivateDataFolderPath, ConfigurationFilePath);

        public void RestoreDefaultDataStoragePath()
        {
            DataStorePath = PrivateDataFolderPath;
        }

        public Configuration(string PrivateDataFolder)
        {
            PrivateDataFolderPath = PrivateDataFolder;

            try
            {
                var seralizer = new JsonSerializer();
                _ConfPrivate = JsonConvert.DeserializeObject<ConfigurationPrivate>(File.ReadAllText(ConfigurationFilePathFull));
            } catch (Exception ex)
            {
                Log.Error(LogCategory.Common, $"Failed to load configuration file with error: {ex}");
                _ConfPrivate = new ConfigurationPrivate();
            }

            if (DataStorePath == null)
            {
                DataStorePath = PrivateDataFolder;
            }

            // Fix the guest gamertag on first load and write it straight away, so the launcher and
            // Android's :game process (each with its own copy of this object) read the same one.
            if (string.IsNullOrEmpty(_ConfPrivate.GuestGamerTag))
            {
                _ = GuestGamerTag;
                try { Save(); }
                catch (Exception ex) { Log.Warn(LogCategory.Common, $"Could not save the guest gamertag: {ex.Message}"); }
            }
        }

        ~Configuration()
        {
            Save();
        }

        public string DataPath(string path)
        {
            return Path.Combine(DataStorePath!, path);
        }
        public void Save()
        {
            File.WriteAllText(ConfigurationFilePathFull, JsonConvert.SerializeObject(_ConfPrivate));
        }
    }
}
