using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Android.App;
using Android.Content;

using Newtonsoft.Json;

using WPR.Common;

using WprApplication = WPR.Models.Application;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Everything between "the user tapped a game" and <c>GameActivity</c> being on screen:
    /// bringing a stale install up to the current patcher version, handing the game off
    /// across the process boundary, and reporting a failed run.
    ///
    /// <para>Written as statics taking the host activity so that any page which lists games
    /// can launch one. The result comes back through the host's
    /// <c>OnActivityResult</c> — see <see cref="RequestGame"/> — because
    /// <c>GameActivity</c> lives in its own OS process and can only report failure by
    /// setting an activity result.</para>
    /// </summary>
    internal static class GameLauncher
    {
        /// <summary>Request code the host must route back into <see cref="HandleGameResult"/>.</summary>
        public const int RequestGame = 4201;

        public static void Launch(Activity host, WprApplication app)
        {
            global::Android.Util.Log.Info("WPR", $"Launch requested: {app.Name} (PatchedVersion={app.PatchedVersion})");

            // Externally-built native ports (Unity rebuilds, etc.) are not patched WP8
            // assemblies hosted in GameActivity — they are their own native binary. Detected
            // by a wpr-port.json in the install folder; route to the port path and skip
            // patching entirely.
            string installFolder = Path.Combine(
                Configuration.Current!.DataPath(WprApplication.DataStoreFolder),
                app.ProductId!);

            UnityPortManifest? portManifest = UnityPortManifest.TryLoad(installFolder);
            if (portManifest != null)
            {
                LaunchUnityPort(host, app, portManifest, installFolder);
                return;
            }

            WpProgressDialog progress = WpProgressDialog.Show(
                host, app.Name ?? "game", WPR.Platform.Android.Properties.Resources.LaunchingInProcess, indeterminate: true);

            Task.Run(() =>
            {
                try
                {
                    if (app.PatchedVersion < ApplicationPatcher.Version)
                    {
                        progress.SetStage("updating patched assemblies…");
                        WprStartup.SetupDllPatchForCecil(host);

                        var patcher = new ApplicationPatcher();
                        patcher.Patch(installFolder, _ => { }, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(LogCategory.AppList, $"Failed to prepare game: {ex}");
                    progress.Dismiss();
                    ShowError(host, ex.Message);
                    return;
                }

                host.RunOnUiThread(() =>
                {
                    try
                    {
                        progress.Dismiss();

                        if (app.PatchedVersion < ApplicationPatcher.Version)
                        {
                            app.PatchedVersion = ApplicationPatcher.Version;
                            var tracked = WPR.Models.ApplicationContext.Current.Applications?
                                .FirstOrDefault(a => a.Id == app.Id);
                            if (tracked != null)
                            {
                                tracked.PatchedVersion = ApplicationPatcher.Version;
                                WPR.Models.ApplicationContext.Current.SaveChanges();
                            }
                        }

                        Intent launchIntent = new Intent(host, typeof(GameActivity));
                        launchIntent.PutExtra(GameActivity.TargetApplicationDataName,
                            JsonConvert.SerializeObject(app));
                        host.StartActivityForResult(launchIntent, RequestGame);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(LogCategory.AppList, $"Failed to start GameActivity: {ex}");
                        ShowError(host, ex.ToString());
                    }
                });
            });
        }

        /// <summary>
        /// Route <c>OnActivityResult</c> for <see cref="RequestGame"/> here. A non-OK result
        /// means the game process died; the reason (if it managed to set one) travels in the
        /// intent extra, and is also written to the external files dir so it survives the
        /// dialog being dismissed.
        /// </summary>
        public static void HandleGameResult(Activity host, Result resultCode, Intent? data)
        {
            // Patching runs relative to the process current directory; GameActivity is a
            // different process, but the launcher's CWD can still have been moved by a
            // re-patch on the way in. Put it back before anything else uses a relative path.
            try { Directory.SetCurrentDirectory(WprStartup.PatchAssembliesDirectory(host)); }
            catch (Exception) { /* the folder is recreated on next patch; not fatal */ }

            if (resultCode == Result.Ok) return;

            string? errorText = data?.GetStringExtra(GameActivity.ErrorDataName);
            if (string.IsNullOrWhiteSpace(errorText))
            {
                errorText = "The game process exited unexpectedly (native crash or force-close). Check logcat for details.";
            }

            Log.Error(LogCategory.AppList, $"Game run error: {errorText}");
            global::Android.Util.Log.Error("WPR", $"Game run error: {errorText}");

            try
            {
                string logPath = Path.Combine(
                    host.GetExternalFilesDir(null)!.AbsolutePath, "last_game_error.txt");
                File.WriteAllText(logPath, errorText);
            }
            catch (Exception) { /* diagnostics only */ }

            string dialogMessage = errorText.Length > 3500
                ? errorText.Substring(0, 3500) + "\n…(truncated)"
                : errorText;

            ShowError(host, dialogMessage);
        }

        /// <summary>
        /// Launch an externally-built native port (see <see cref="WPR.UnityPortManifest"/>).
        /// Two shapes are supported:
        /// <list type="number">
        ///   <item><b>Unity-as-a-Library embed</b> — the Unity build's activity is compiled
        ///     into this APK via a bound <c>unityLibrary</c> AAR; we start it by class name,
        ///     which means it lights up automatically once that AAR is present.</item>
        ///   <item><b>Separate installed APK</b> — launched by package.</item>
        /// </list>
        /// </summary>
        private static void LaunchUnityPort(Activity host, WprApplication app, UnityPortManifest manifest, string installFolder)
        {
            app.ApplicationType = WPR.Models.ApplicationType.UnityPort;

            var android = manifest.Android;
            if (android == null)
            {
                ShowPortError(host, app, $"This port has no Android build defined in {UnityPortManifest.FileName}.");
                return;
            }

            // (1) Embedded Unity-as-a-Library activity, resolved by name so this compiles
            //     before any Unity AAR is bound into the app.
            if (!string.IsNullOrEmpty(android.Activity))
            {
                try
                {
                    var cls = Java.Lang.Class.ForName(android.Activity);
                    host.StartActivity(new Intent(host, cls));
                    return;
                }
                catch (Java.Lang.ClassNotFoundException)
                {
                    global::Android.Util.Log.Warn("WPR",
                        $"Unity port activity '{android.Activity}' is not in this APK — the Unity library is not embedded yet.");
                    // fall through to package / error
                }
            }

            // (2) Separate installed package.
            if (!string.IsNullOrEmpty(android.Package))
            {
                Intent? launch = host.PackageManager!.GetLaunchIntentForPackage(android.Package);
                if (launch != null)
                {
                    host.StartActivity(launch);
                    return;
                }

                if (!string.IsNullOrEmpty(android.Apk) &&
                    File.Exists(Path.Combine(installFolder, android.Apk)))
                {
                    ShowPortError(host, app,
                        $"The port package '{android.Package}' is not installed. Install the bundled APK " +
                        $"at '{Path.Combine(installFolder, android.Apk)}' and relaunch.");
                    return;
                }

                ShowPortError(host, app, $"The port package '{android.Package}' is not installed on this device.");
                return;
            }

            ShowPortError(host, app,
                "This Unity port is not embedded yet. Build the Unity project for Android and either bind its " +
                "library into WPR (Unity-as-a-Library) or install its APK. See Plans/Unity_WP8_Feasibility.md.");
        }

        private static void ShowPortError(Activity host, WprApplication app, string message)
        {
            Log.Warn(LogCategory.AppList, $"Unity port '{app.Name}': {message}");
            ShowError(host, message);
        }

        private static void ShowError(Activity host, string message)
        {
            host.RunOnUiThread(() =>
            {
                if (host.IsFinishing || host.IsDestroyed) return;

                new AlertDialog.Builder(host)!
                    .SetTitle(WPR.Platform.Android.Properties.Resources.AppRunError)!
                    .SetMessage(message)!
                    .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                    .Show();
            });
        }
    }
}
