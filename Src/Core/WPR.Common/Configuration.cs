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
            public String GamerTag;
            // Absolute path to the user-selected gamer picture (PNG/JPG). Consumed by
            // Microsoft.Xna.Framework.GamerServices.GamerProfile.GetGamerPicture(), which is
            // what WP7 titles like Fruit Ninja call via Texture2D.FromStream. Null/missing
            // file → GetGamerPicture returns Stream.Null and the game falls back to no avatar.
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
        };

        private const string ConfigurationFilePath = "config.json";

        private string PrivateDataFolderPath;
        private ConfigurationPrivate? _ConfPrivate;

        public string? DataStorePath
        {
            get => _ConfPrivate!.DataStorePath;
            set => _ConfPrivate!.DataStorePath = value;
        }

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
