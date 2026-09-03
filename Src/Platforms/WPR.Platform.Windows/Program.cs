using Avalonia;
using WPR.Engine.Notifications;
using Avalonia.ReactiveUI;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using System;
using System.IO;
using System.Runtime.InteropServices;

using WPR.WindowsCompability;
using System.Linq;

using WPR.Common;
using WPR.Notifications.WindowsToast;

namespace WPR.Platform.Windows
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            Configuration.Current = new Configuration(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "WPR"));

            // Resolve bundled data against the executable's own directory, NOT the process
            // working directory. The build's "Copy pre-made database" target drops Database\
            // next to the .exe; AppContext.BaseDirectory always points there regardless of
            // what CWD the app was launched with (Directory.GetCurrentDirectory() is not
            // guaranteed to be the exe dir and would make these copies read the wrong place
            // or throw at startup).
            string bundledDb = Path.Combine(AppContext.BaseDirectory, "Database");

            // (The Database\TrueAchievements JSON maps are no longer deployed — they fed the
            //  TrueAchievements scraper, removed 2026-08-07. Catalogues below are the source of truth.)

            // Hardcoded achievement catalogues (manifest + icon PNGs). Overwrite-copy
            // every launch so curated updates ship with the build.
            string achievementsSrc = Path.Combine(bundledDb, "Achievements");
            if (Directory.Exists(achievementsSrc))
            {
                Filesystem.CopyFilesRecursively(achievementsSrc,
                    Configuration.Current.DataPath("Database\\Achievements"));
            }

            if (!File.Exists(Configuration.Current.DataPath("Database\\achievements.db")))
            {
                File.Copy(Path.Combine(bundledDb, "achievements.db"), Configuration.Current.DataPath("Database\\achievements.db"));
            }

            if (!File.Exists(Configuration.Current.DataPath("Database\\applications.db")))
            {
                File.Copy(Path.Combine(bundledDb, "applications.db"), Configuration.Current.DataPath("Database\\applications.db"));
            }

            // Headless library maintenance (opt-in via CLI). Runs the real install
            // pipeline over the whole library, then exits before the UI starts:
            //   --repatch-installed : re-patch every installed game with the current patcher
            //   --reinstall-all     : the above, plus fresh-install every library XAP not
            //                         yet installed
            // Useful after a patcher change touches many already-installed games, where
            // clicking "reinstall" per game in the UI is impractical. Requires the WPR UI
            // to be closed (installed DLLs must not be locked by a running game).
            if (args.Contains("--reinstall-all") || args.Contains("--repatch-installed"))
            {
                bool includeNew = args.Contains("--reinstall-all");
                BatchReinstall.RunAsync(includeNew).GetAwaiter().GetResult();
                Environment.Exit(0);
            }

            // Reconcile installed games against their hardcoded catalogues: add new /
            // update changed achievements, never reset unlock progress. Non-fatal.
            try { WPR.XnaAchievementSeeder.ReconcileCatalogueGamesAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Startup, $"Startup achievement reconcile failed (non-fatal): {ex.Message}");
            }

            InitializeNotifications();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            // Backstop: FNA spawns non-background threads (audio, render) for any game we launched.
            // Even after every WPR window closes, those threads keep the CLR alive. Force a clean
            // process exit so closing the WPR window actually shuts WPR down.
            Environment.Exit(0);
        }

        // Branding shown by Windows in the toast notification chrome. Also doubles as
        // the AppUserModelID and as the Start Menu shortcut filename, so it has to
        // be a string that's valid in all three contexts.
        private const string WindowsAppDisplayName = "Windows Phone Runner";

        /// <summary>
        /// Construct the platform notification manager and hand it to <see cref="NotificationBackend"/>.
        /// Lives in the desktop head (rather than WPR.Common) so the Windows toast
        /// implementation ships with the platform it targets.
        /// </summary>
        private static void InitializeNotifications()
        {
            // Only Windows has a real toast backend in this build. The net8.0 (non-Windows)
            // desktop leg compiles against the null implementation; leave the manager unset.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                NotificationBackend.SetManager(null);
                return;
            }

            // Write a 1×1 transparent .ico so the toast chrome's icon slot renders
            // empty rather than the .exe's blank/default icon placeholder. Windows
            // toast XML doesn't expose a "hide app icon" knob — the only way to
            // suppress the visual is to make the underlying icon transparent.
            string? iconPath = TryWriteBlankIcon();

            var ctx = WindowsApplicationContext.FromCurrentProcess(
                customName: WindowsAppDisplayName,
                iconPath: iconPath);
            NotificationBackend.SetManager(new WindowsNotificationManager(ctx));

            // Remove the stale "WPR.UI.Desktop.lnk" from older builds that used the
            // .exe filename as the app name. Without this, the Start Menu / search
            // still shows the old entry alongside the new one.
            //
            // The 2026-08-29 exe rename (WPR.UI.Desktop.exe -> WPR.Platform.Windows.exe)
            // does NOT add a second legacy name here: FromCurrentProcess is always
            // called with customName, so both the AUMID and the .lnk filename come from
            // WindowsAppDisplayName and have never tracked the exe filename.
            TryRemoveLegacyShortcut("WPR.UI.Desktop");
        }

        /// <summary>
        /// Write a minimal 1×1 fully-transparent ICO to <c>%LOCALAPPDATA%\WPR\blank.ico</c>
        /// and return its path. Used as the Start Menu shortcut's <c>IconLocation</c> so the
        /// toast notification's chrome glyph renders empty rather than showing the
        /// default-Avalonia placeholder bundled into the .exe.
        /// 70 bytes total: 6-byte ICONDIR + 16-byte ICONDIRENTRY + 40-byte BITMAPINFOHEADER
        /// + 4-byte transparent BGRA pixel + 4-byte AND mask.
        /// Returns null on any failure (caller falls back to no icon override).
        /// </summary>
        private static string? TryWriteBlankIcon()
        {
            try
            {
                string icoPath = Configuration.Current?.DataPath("blank.ico")
                                 ?? Path.Combine(Path.GetTempPath(), "wpr-blank.ico");
                Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
                if (File.Exists(icoPath) && new FileInfo(icoPath).Length == 70)
                    return icoPath;

                byte[] ico = new byte[]
                {
                    // ICONDIR
                    0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
                    // ICONDIRENTRY: w=1, h=1, colors=0, _, planes=1, bpp=32, bytes=48, offset=22
                    0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x20, 0x00,
                    0x30, 0x00, 0x00, 0x00, 0x16, 0x00, 0x00, 0x00,
                    // BITMAPINFOHEADER (size=40, w=1, h=2 (image+mask doubled), planes=1,
                    // bpp=32, rest zeros)
                    0x28, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
                    0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x20, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    // XOR pixel: BGRA 0,0,0,0 (fully transparent)
                    0x00, 0x00, 0x00, 0x00,
                    // AND mask: 1 row, 4 bytes (4-byte aligned), all zero (show alpha)
                    0x00, 0x00, 0x00, 0x00,
                };

                File.WriteAllBytes(icoPath, ico);
                return icoPath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Best-effort removal of an obsolete Start Menu shortcut written by an earlier
        /// build under a different name. Swallow all errors — the shortcut may not
        /// exist, or the user may have moved it.
        /// </summary>
        private static void TryRemoveLegacyShortcut(string oldAppName)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string lnk = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs", $"{oldAppName}.lnk");
                if (File.Exists(lnk)) File.Delete(lnk);
            }
            catch { /* best-effort */ }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                        .UsePlatformDetect()
                        .WithIcons(container => container
                            .Register<FontAwesomeIconProvider>())
                        .LogToTrace()
                        .UseReactiveUI();
        }
    }
}
