using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Activity.Result.Contract;
using AndroidX.Activity.Result;
using Avalonia.Android;
using Avalonia;
using Avalonia.ReactiveUI;

using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;

using System.IO;
using Android.OS;
using Newtonsoft.Json;
using WPR.Common;
using WPR.UI;
using Avalonia.Controls.ApplicationLifetimes;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;
using Android.Widget;
using Android.Runtime;

#if !DEBUG
using Xamarin.Android.AssemblyStore;
#else
using System.IO.Compression;
#endif

namespace WPR.UI.Android
{
    internal class GameActivityResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        private MainActivity _Owning;

        public GameActivityResultCallback(MainActivity activity)
        {
            _Owning = activity;
        }

        public void OnActivityResult(Java.Lang.Object result)
        {
            //MessageBoxUtils.MainActivity = _Owning;
            Directory.SetCurrentDirectory(_Owning.CurrentDirectoryForMain);

            ActivityResult resultAct = (result as ActivityResult)!;
            if (resultAct.ResultCode != (int)Result.Ok)
            {
                var errorText = resultAct.Data?.GetStringExtra(GameActivity.ErrorDataName);
                if (string.IsNullOrWhiteSpace(errorText))
                {
                    errorText = "The game process exited unexpectedly (native crash or force-close). Check logcat for details.";
                }

                WPR.Common.Log.Error(LogCategory.AppList, $"Game run error: {errorText}");
                global::Android.Util.Log.Error("WPR", $"Game run error: {errorText}");

                try
                {
                    var logPath = System.IO.Path.Combine(
                        _Owning.GetExternalFilesDir(null)!.AbsolutePath,
                        "last_game_error.txt");
                    System.IO.File.WriteAllText(logPath, errorText);
                }
                catch { }

                var dialogMessage = errorText.Length > 3500
                    ? errorText.Substring(0, 3500) + "\n…(truncated)"
                    : errorText;

                _Owning.RunOnUiThread(() =>
                {
                    new AlertDialog.Builder(_Owning)!
                        .SetTitle(Properties.Resources.AppRunError)!
                        .SetMessage(dialogMessage)!
                        .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                        .Show();
                });
            }
        }
    }

    [Activity(Label = "WPR.Android", Theme = "@style/MyTheme.NoActionBar", Icon = "@drawable/icon", MainLauncher = true, LaunchMode = LaunchMode.SingleInstance, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.MainActivity")]
    public class MainActivity : AvaloniaMainActivity<App>
    {
        private ActivityResultLauncher ActivitySpawner;
        private static List<string> CopyAssemblyList = new List<string>
        {
            "FNA"
        };

        public string CurrentDirectoryForMain
        {
            get
            {
                return Path.Combine(GetExternalFilesDir(null)!.AbsolutePath, "PatchAssemblies");
            }
        }

        public MainActivity()
        {
            // Keep constructor minimal; register activity results in OnCreate when Activity is initialized
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
            base.CustomizeAppBuilder(builder)
                .WithIcons(container => container.Register<FontAwesomeIconProvider>())
                .LogToTrace()
                .UseReactiveUI();

        // Because DLLs files are in APK. Monodroid has their own way of extracting and getting these dlls out.
        // But Cecil just read it from stream. It's hard. So we extract a subset of needed DLLs beforehand
        public void SetupDllPatchForCecil()
        {
            string basePath = CurrentDirectoryForMain;
            Directory.CreateDirectory(basePath);

            string? apkPath = Application?.ApplicationInfo?.PublicSourceDir;
            if (apkPath == null)
            {
                WPR.Common.Log.Warn(LogCategory.Android, "Unable to copy DLLs needed for patching! Some games may fail to patch!");
                return;
            }
#if DEBUG

            using (ZipArchive archive = ZipFile.Open(apkPath, ZipArchiveMode.Read))
            {
                foreach (var dll in CopyAssemblyList)
                {
                    ZipArchiveEntry? entry = archive.GetEntry($"assemblies/{dll}.dll");
                    if (entry == null)
                    {
                        WPR.Common.Log.Warn(LogCategory.Android, $"Fail to copy DLL ${dll} to patch assembly folder!");
                    }
                    else
                    {
                        entry.ExtractToFile(Path.Combine(basePath, dll), true);
                    }
                }
            }
#else
            AssemblyStoreExplorer explorer = new AssemblyStoreExplorer(apkPath, keepStoreInMemory: true);
            foreach (var dll in CopyAssemblyList)
            {
                string filename = $"{dll}.dll.comp";
                string filenameAuth = $"{dll}.dll";

                if (explorer.AssembliesByName.ContainsKey(dll))
                {
                    explorer.AssembliesByName[dll].ExtractImage(basePath, filename);
                }
                else
                {
                    WPR.Common.Log.Warn(LogCategory.Android, $"Fail to copy DLL ${dll} to patch assembly folder (entry not found)!");
                    continue;
                }

                bool fileShouldMove = false;

                using (FileStream stream = new FileStream(Path.Combine(basePath, filename), FileMode.Open, FileAccess.Read))
                {
                    if (AssemblyDecompressor.IsCompressed(stream))
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                        
                        using (FileStream streamAuth = new FileStream(Path.Combine(basePath, filenameAuth), FileMode.OpenOrCreate, FileAccess.Write))
                        {
                            if (!AssemblyDecompressor.Work(stream, streamAuth))
                            {
                                WPR.Common.Log.Warn(LogCategory.Android, $"Fail to decompress DLL ${dll} to patch assembly folder (entry not found)!");
                            }
                        }
                    } else
                    {
                        fileShouldMove = true;
                    }
                }

                if (fileShouldMove)
                {
                    File.Move(Path.Combine(basePath, filename), Path.Combine(basePath, filenameAuth));
                } else
                {
                    File.Delete(Path.Combine(basePath, filename));
                }
            }
#endif

            Directory.SetCurrentDirectory(basePath);
        }

        public void SetupConfigurationAndDatabase()
        {
            Configuration.Current = new Configuration(GetExternalFilesDir(null)!.AbsolutePath);

            //Filesystem.CopyFolderFromAssets(Assets!, "Database/TrueAchievements", Configuration.Current.DataPath("Database/TrueAchievements"));

            //if (!File.Exists(Configuration.Current.DataPath("Database/achievements.db")))
            //{
            //    Filesystem.CopyFileFromAssets(Assets!, "Database/achievements.db", Configuration.Current.DataPath("Database/achievements.db"));
            //}

            var databaseDir = Configuration.Current.DataPath("Database");
            Directory.CreateDirectory(databaseDir);

            var dbPath = Path.Combine(databaseDir, "applications.db");
            if (!File.Exists(dbPath))
            {
                Filesystem.CopyFileFromAssets(Assets!, "Database/applications.db", dbPath);
            }

            var achievementsPath = Path.Combine(databaseDir, "achievements.db");
            if (!File.Exists(achievementsPath))
            {
                Filesystem.CopyFileFromAssets(Assets!, "Database/achievements.db", achievementsPath);
            }

            Filesystem.CopyFolderFromAssets(Assets!, "Database/TrueAchievements",
                Path.Combine(databaseDir, "TrueAchievements"));

            // Hardcoded achievement catalogues (manifest + icon PNGs), one folder
            // per product. Recursive copy handles the per-product subfolders.
            Filesystem.CopyFolderFromAssets(Assets!, "Database/Achievements",
                Path.Combine(databaseDir, "Achievements"));

            // Reconcile installed games against their catalogues (non-destructive;
            // never resets unlock progress). Non-fatal.
            try { WPR.XnaAchievementSeeder.ReconcileCatalogueGamesAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                WPR.Common.Log.Warn(LogCategory.Startup, $"Startup achievement reconcile failed (non-fatal): {ex.Message}");
            }
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            SetupConfigurationAndDatabase();

            base.OnCreate(savedInstanceState);

            MessageBoxUtils.MainActivity = this;
            ServicesSetup.Start();
            // Android toast notifications are staged but not wired up yet. The
            // implementation lives in Notifications/AndroidNotificationManager.cs;
            // enable it by uncommenting the line below.
            // NativeUI.NotificationManager = new global::DesktopNotifications.Android.AndroidNotificationManager(this);
            SetupDllPatchForCecil();

            ActivitySpawner = RegisterForActivityResult(new ActivityResultContracts.StartActivityForResult(),
                new GameActivityResultCallback(this));

            ApplicationLaunchRequest.Incoming += (sender, args) =>
            {
                RunOnUiThread(() => LaunchGame(args.Target));
            };

            EnsureMainViewAttached();

            global::Android.Util.Log.Info("WPR", "MainActivity OnCreate completed");
        }

        void LaunchGame(Models.Application app)
        {
            global::Android.Util.Log.Info("WPR", $"Launch requested: {app.Name} (PatchedVersion={app.PatchedVersion})");

            // Externally-built native ports (Unity rebuilds, etc.) aren't patched WP8 assemblies
            // hosted in GameActivity — they're their own native binary. Detected by a
            // wpr-port.json in the install folder; route to the port path and skip patching.
            var installFolder = Path.Combine(
                Configuration.Current!.DataPath(Models.Application.DataStoreFolder),
                app.ProductId!);
            var portManifest = UnityPortManifest.TryLoad(installFolder);
            if (portManifest != null)
            {
                LaunchUnityPort(app, portManifest, installFolder);
                return;
            }

            var progress = new ProgressDialog(this);
            progress.SetMessage(Properties.Resources.LaunchingInProcess);
            progress.SetCancelable(false);
            progress.Show();

            Task.Run(() =>
            {
                try
                {
                    if (app.PatchedVersion < ApplicationPatcher.Version)
                    {
                        SetupDllPatchForCecil();
                        var folder = Path.Combine(
                            Configuration.Current!.DataPath(Models.Application.DataStoreFolder),
                            app.ProductId!);
                        var patcher = new ApplicationPatcher();
                        patcher.Patch(folder, _ => { }, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    WPR.Common.Log.Error(LogCategory.AppList, $"Failed to prepare game: {ex}");
                    RunOnUiThread(() =>
                    {
                        progress.Dismiss();
                        new AlertDialog.Builder(this)!
                            .SetTitle(Properties.Resources.AppRunError)!
                            .SetMessage(ex.Message)!
                            .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                            .Show();
                    });
                    return;
                }

                RunOnUiThread(() =>
                {
                    try
                    {
                        progress.Dismiss();

                        if (app.PatchedVersion < ApplicationPatcher.Version)
                        {
                            app.PatchedVersion = ApplicationPatcher.Version;
                            var tracked = Models.ApplicationContext.Current.Applications?
                                .FirstOrDefault(a => a.Id == app.Id);
                            if (tracked != null)
                            {
                                tracked.PatchedVersion = ApplicationPatcher.Version;
                                Models.ApplicationContext.Current.SaveChanges();
                            }
                        }

                        var launchIntent = new Intent(this, typeof(GameActivity));
                        launchIntent.PutExtra(GameActivity.TargetApplicationDataName,
                            JsonConvert.SerializeObject(app));
                        ActivitySpawner.Launch(launchIntent);
                    }
                    catch (Exception ex)
                    {
                        WPR.Common.Log.Error(LogCategory.AppList, $"Failed to start GameActivity: {ex}");
                        new AlertDialog.Builder(this)!
                            .SetTitle(Properties.Resources.AppRunError)!
                            .SetMessage(ex.ToString())!
                            .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                            .Show();
                    }
                });
            });
        }

        /// <summary>
        /// Launch an externally-built native port (see <see cref="WPR.UnityPortManifest"/>). Two
        /// shapes are supported:
        /// <list type="number">
        ///   <item><b>Unity-as-a-Library embed</b> — the Unity build's activity (e.g.
        ///     <c>com.unity3d.player.UnityPlayerActivity</c>) is compiled into this APK via a
        ///     bound <c>unityLibrary</c> AAR; we start it directly by class name. This is the
        ///     chosen Android model, but it is inert until a rebuilt port's AAR is added to
        ///     WPR.UI.Android — resolving the activity by name means it lights up automatically
        ///     once that AAR is present. See <c>Docs/Unity_WP8_Feasibility.md</c> for the
        ///     Gradle / binding steps.</item>
        ///   <item><b>Separate installed APK</b> — launched by package via a launch intent.</item>
        /// </list>
        /// </summary>
        void LaunchUnityPort(Models.Application app, WPR.UnityPortManifest manifest, string installFolder)
        {
            app.ApplicationType = Models.ApplicationType.UnityPort;

            var android = manifest.Android;
            if (android == null)
            {
                ShowPortError(app, $"This port has no Android build defined in {WPR.UnityPortManifest.FileName}.");
                return;
            }

            // (1) Embedded Unity-as-a-Library activity, resolved by name so this compiles before
            //     any Unity AAR is bound into the app.
            if (!string.IsNullOrEmpty(android.Activity))
            {
                try
                {
                    var cls = Java.Lang.Class.ForName(android.Activity);
                    StartActivity(new Intent(this, cls));
                    return;
                }
                catch (Java.Lang.ClassNotFoundException)
                {
                    global::Android.Util.Log.Warn("WPR",
                        $"Unity port activity '{android.Activity}' isn't in this APK — the Unity library isn't embedded yet.");
                    // fall through to package / error
                }
            }

            // (2) Separate installed package.
            if (!string.IsNullOrEmpty(android.Package))
            {
                var launch = PackageManager!.GetLaunchIntentForPackage(android.Package);
                if (launch != null)
                {
                    StartActivity(launch);
                    return;
                }

                if (!string.IsNullOrEmpty(android.Apk) &&
                    File.Exists(Path.Combine(installFolder, android.Apk)))
                {
                    // Auto-install via PackageInstaller (needs a FileProvider + REQUEST_INSTALL_PACKAGES)
                    // is future work; for now point the user at the bundled APK.
                    ShowPortError(app,
                        $"The port package '{android.Package}' isn't installed. Install the bundled APK " +
                        $"at '{Path.Combine(installFolder, android.Apk)}' and relaunch.");
                    return;
                }

                ShowPortError(app, $"The port package '{android.Package}' isn't installed on this device.");
                return;
            }

            ShowPortError(app,
                "This Unity port isn't embedded yet. Build the Unity project for Android and either bind its " +
                "library into WPR (Unity-as-a-Library) or install its APK. See Docs/Unity_WP8_Feasibility.md.");
        }

        void ShowPortError(Models.Application app, string message)
        {
            WPR.Common.Log.Warn(LogCategory.AppList, $"Unity port '{app.Name}': {message}");
            RunOnUiThread(() =>
            {
                new AlertDialog.Builder(this)!
                    .SetTitle(Properties.Resources.AppRunError)!
                    .SetMessage(message)!
                    .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                    .Show();
            });
        }

        void EnsureMainViewAttached()
        {
            try
            {
                var lifetime = Avalonia.Application.Current?.ApplicationLifetime;
                global::Android.Util.Log.Info("WPR", $"ApplicationLifetime = {lifetime?.GetType().FullName ?? "<null>"}");

                if (lifetime is ISingleViewApplicationLifetime singleView && singleView.MainView != null)
                {
                    global::Android.Util.Log.Info("WPR", $"MainView = {singleView.MainView.GetType().FullName}");
                    Content = singleView.MainView;
                }
                else
                {
                    global::Android.Util.Log.Warn("WPR", "MainView was null after framework init");
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("WPR", $"EnsureMainViewAttached failed: {ex}");
            }
        }
    }
}
