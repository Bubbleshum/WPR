using WPR.Shell;
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

        /// <param name="diagnostic">"Play with logging": the verbose log for this run, a "stop and send
        /// report" button over the game, and the report sent when it ends (see <see cref="WPR.Shell.DiagnosticRun"/>).</param>
        public static void Launch(Activity host, WprApplication app, bool diagnostic = false)
        {
            // A record left by an earlier logging run that never came back (the launcher died) must
            // not turn this one into a report.
            if (!diagnostic) WPR.Shell.DiagnosticRun.Take(app.ProductId ?? "");

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
                host, app.Name ?? "game", WPR.Shell.Resources.LaunchingInProcess, indeterminate: true);

            Task.Run(() =>
            {
                try
                {
                    // A WP8 native title is ARM code: the IL patcher has nothing to do there.
                    if (app.PatchedVersion < ApplicationPatcher.Version &&
                        app.ApplicationType != WPR.Models.ApplicationType.ModernNative)
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

                        LastLaunched = app;
                        LastLaunchedAt = DateTime.UtcNow;
                        // The :game process heartbeats "playing X" now; two voices would flip-flop.
                        WPR.Engine.Online.OnlineBackend.Presence?.Suspend();
                        Intent launchIntent = new Intent(host, typeof(GameActivity));
                        launchIntent.PutExtra(GameActivity.TargetApplicationDataName,
                            JsonConvert.SerializeObject(app));
                        if (diagnostic)
                        {
                            WPR.Shell.DiagnosticRun.Begin(app.ProductId!, app.Name ?? app.ProductId!);
                            launchIntent.PutExtra(GameActivity.DiagnosticRunDataName, true);
                        }
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
        /// <param name="onErrorAcknowledged">Invoked once the error dialog has gone, however it
        /// went. For a host with a page behind it there is nothing to do and this stays null;
        /// <see cref="GameShortcutActivity"/> passes its own <c>Finish</c>, because it has
        /// nothing to show once the dialog is dismissed.</param>
        public static void HandleGameResult(Activity host, Result resultCode, Intent? data,
            Action? onErrorAcknowledged = null)
        {
            // Patching runs relative to the process current directory; GameActivity is a
            // different process, but the launcher's CWD can still have been moved by a
            // re-patch on the way in. Put it back before anything else uses a relative path.
            try { Directory.SetCurrentDirectory(WprStartup.PatchAssembliesDirectory(host)); }
            catch (Exception) { /* the folder is recreated on next patch; not fatal */ }

            WprApplication? ranApp = LastLaunched;
            LastLaunched = null;
            WPR.Engine.Online.OnlineBackend.Presence?.Resume();

            // A logging run is reported however it ended. When Android recreated this process
            // meanwhile, ranApp is gone but the run's own record still names the game.
            WPR.Shell.DiagnosticRun.Run? logging =
                WPR.Shell.DiagnosticRun.Take(ranApp?.ProductId ?? WPR.Shell.DiagnosticRun.Current()?.ProductId ?? "");

            if (resultCode == Result.Ok)
            {
                FlushOnline();
                if (logging != null)
                {
                    _ = SendLoggingRunAsync(host, logging,
                        logging.StopRequested ? "stopped by the player" : "the game exited normally", null, onErrorAcknowledged);
                }
                else if (ranApp != null && host is not GameShortcutActivity)
                {
                    // Not over the shortcut trampoline: it finishes as soon as the game hands back.
                    MaybeAskRating(host, ranApp, DateTime.UtcNow - LastLaunchedAt);
                }
                return;
            }

            string? errorText = data?.GetStringExtra(GameActivity.ErrorDataName);
            if (string.IsNullOrWhiteSpace(errorText))
            {
                errorText = "The game process exited unexpectedly (native crash or force-close). Check logcat for details.";

                // A managed crash was already queued by ApplicationLaunch inside the :game process.
                // This one had no managed exception at all - SIGSEGV, abort, the OOM killer, or a
                // force-close - so only this process can report it.
                ReportNativeCrash(ranApp);
            }

            FlushOnline();

            Log.Error(LogCategory.AppList, $"Game run error: {errorText}");
            global::Android.Util.Log.Error("WPR", $"Game run error: {errorText}");

            try
            {
                string logPath = Path.Combine(
                    host.GetExternalFilesDir(null)!.AbsolutePath, "last_game_error.txt");
                File.WriteAllText(logPath, errorText);
            }
            catch (Exception) { /* diagnostics only */ }

            if (logging != null)
            {
                // One dialog, not two: the report's, which carries the error as well.
                _ = SendLoggingRunAsync(host, logging, "the game crashed", GraphicsDriverHint() + errorText, onErrorAcknowledged);
                return;
            }

            string dialogMessage = errorText.Length > 3500
                ? errorText.Substring(0, 3500) + "\n…(truncated)"
                : errorText;

            // Hint FIRST. A managed failure dumps a stack trace long enough to fill several
            // screens of a scrolling dialog, and anything appended after it is never read.
            ShowError(host, GraphicsDriverHint() + dialogMessage, onErrorAcknowledged);
        }

        /// <summary>
        /// The game handed to <c>GameActivity</c> most recently, so <see cref="HandleGameResult"/>
        /// knows what a dead game process was running. Launcher-process state: if Android
        /// recreated this process in between, it is null and the crash goes unattributed rather
        /// than being filed against the wrong game.
        /// </summary>
        private static WprApplication? LastLaunched;

        /// <summary>When <see cref="LastLaunched"/> was handed over, for how long it ran (the rating prompt's minimum).</summary>
        private static DateTime LastLaunchedAt;

        private static void ReportNativeCrash(WprApplication? app)
        {
            if (app == null || WPR.Engine.Online.OnlineBackend.Crashes is not { } reporter) return;

            reporter.Report(new WPR.Engine.Online.CrashReport
            {
                Source = WPR.Engine.Online.CrashReport.Sources.NativeCrash,
                TitleId = app.ProductId,
                TitleName = app.Name,
                InstallFolder = Path.Combine(Configuration.Current!.DataPath(WprApplication.DataStoreFolder), app.ProductId!),
                GraphicsBackend = WPR.Engine.Graphics.GraphicsDriverPreference.ResolveDriverName(),
                // No managed exception to name. A fixed type is what lets the hub group these by
                // game and driver rather than filing each one as unique.
                ExceptionType = "NativeCrash",
                ExceptionMessage = "The game process exited without a managed exception (native crash, abort, or killed by the system).",
            });
        }

        /// <summary>
        /// Sends what the game left behind: its crash report, if any, and the scores it posted.
        /// Run from the launcher because the :game process is killed as soon as it finishes.
        /// </summary>
        private static void FlushOnline() => _ = WPR.Engine.Online.OnlineBackend.FlushAsync();

        /// <summary>
        /// "How did it run?", now and again (the rules are <see cref="WPR.Shell.GameRatingPrompt"/>'s):
        /// the hub's six ratings as a list, "ask me later" and "don't ask about this game". Tapping a rating sends it at once.
        /// </summary>
        private static void MaybeAskRating(Activity host, WprApplication app, TimeSpan played)
        {
            if (!WPR.Shell.GameRatingPrompt.ShouldAsk(app.ProductId, played)) return;

            host.RunOnUiThread(() =>
            {
                if (host.IsFinishing || host.IsDestroyed) return;

                var ratings = WPR.Online.Hub.Client.CompatibilityRatings.All;
                string[] items = ratings.Select(r => $"{r.Label.ToLowerInvariant()} — {r.Meaning.ToLowerInvariant()}").ToArray();
                string name = WPR.HardcodedAchievementCatalogue.GameName(app.ProductId) ?? app.Name ?? "this game";

                AlertDialog dialog = new AlertDialog.Builder(host)!
                    .SetTitle($"how did {name} run?")!
                    .SetItems(items, (_, e) => _ = SubmitRatingAsync(host, app, ratings[e.Which].Key, ratings[e.Which].Label))!
                    // Postponing is the default for any way out that is not an answer: Back, a tap
                    // outside, or "ask me later" all leave the game to be asked about next time.
                    .SetNeutralButton("ask me later", (IDialogInterfaceOnClickListener?)null)!
                    .SetNegativeButton("don't ask about this game", (_, _) => WPR.Shell.GameRatingPrompt.Decline(app.ProductId!))!
                    .Create()!;
                WPR.Shell.GameRatingPrompt.MarkAsked(app.ProductId!);
                dialog.Show();
            });
        }

        private static async Task SubmitRatingAsync(Activity host, WprApplication app, string rating, string label)
        {
            string message;
            try
            {
                await WPR.Shell.GameRatingPrompt.SubmitAsync(app.ProductId!, rating, "android",
                    global::Android.OS.Build.VERSION.Release, WPR.Online.Hub.Client.RuntimePaths.XnaFna,
                    WPR.Engine.Graphics.GraphicsCapabilitiesStore.ReadLastMeasured()?.Backend);
                message = "thanks. your rating (" + label.ToLowerInvariant() + ") is on WPR Hub.";
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, "[wpr-rating] could not send: " + ex.Message);
                message = "could not send your rating: " + ex.Message;
            }
            host.RunOnUiThread(() =>
            {
                if (!host.IsDestroyed) global::Android.Widget.Toast.MakeText(host, message, global::Android.Widget.ToastLength.Long)!.Show();
            });
        }

        /// <summary>
        /// Send a finished logging run's log (WPR's session log for the run, plus this app's logcat
        /// for the same window) and say how it went. Pressing "play with logging" is the consent, so
        /// this sends with server logging off, like the old "send a report" button did.
        /// </summary>
        private static async Task SendLoggingRunAsync(Activity host, WPR.Shell.DiagnosticRun.Run run, string outcome,
            string? errorText, Action? onDismissed)
        {
            WpProgressDialog progress = WpProgressDialog.Show(host, run.Title, "sending the log from this run…", indeterminate: true);
            string message;
            string? code = null;
            try
            {
                TimeSpan window = run.Window;
                string logcat = await Task.Run(() => OwnLogcat.Read(window));
                code = await WPR.Shell.DiagnosticRun.SendAsync(run, outcome,
                    "===== logcat (this app, same window) =====\n" + logcat,
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["device"] = global::Android.OS.Build.Manufacturer + " " + global::Android.OS.Build.Model,
                        ["android"] = global::Android.OS.Build.VERSION.Release + " (API " + (int)global::Android.OS.Build.VERSION.SdkInt + ")",
                        ["graphics"] = WPR.Engine.Graphics.GraphicsDriverPreference.ResolveDriverName() ?? "default",
                    },
                    runtimePath: WPR.Online.Hub.Client.RuntimePaths.XnaFna,
                    graphicsBackend: WPR.Engine.Graphics.GraphicsCapabilitiesStore.ReadLastMeasured()?.Backend);
                message = $"the log from this run of {run.Title} ({outcome}) was sent to WPR Hub.\n\nyour report code is {code}. quote it if you open a GitHub issue about this game.";
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, "[wpr-diag] could not send the logging run: " + ex);
                message = $"the run ended ({outcome}), but its log could not be sent: {ex.Message}";
            }
            finally
            {
                progress.Dismiss();
            }

            if (!string.IsNullOrWhiteSpace(errorText))
                message += "\n\n" + (errorText!.Length > 2000 ? errorText.Substring(0, 2000) + "\n…(truncated)" : errorText);

            host.RunOnUiThread(() =>
            {
                if (host.IsFinishing || host.IsDestroyed) { onDismissed?.Invoke(); return; }

                var builder = new AlertDialog.Builder(host)!
                    .SetTitle(code != null ? "report sent" : "report not sent")!
                    .SetMessage(message)!
                    .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!;
                if (code != null)
                {
                    builder.SetNeutralButton("copy code", (_, _) =>
                    {
                        var clipboard = (ClipboardManager?)host.GetSystemService(Context.ClipboardService);
                        if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText("WPR report code", code);
                    });
                }
                AlertDialog dialog = builder.Create()!;
                if (onDismissed != null) dialog.DismissEvent += (_, _) => onDismissed();
                dialog.Show();
            });
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

        /// <summary>
        /// The one line that turns a graphics-driver failure from unrecoverable into a two-tap fix,
        /// shown above every failed run's detail.
        ///
        /// <para>Unconditional rather than matched against a recognised exception type, because the
        /// worst case carries no exception at all: a driver that dies inside
        /// <c>FNA3D_CreateDevice</c>, or natively below it, reaches this method as "the game process
        /// exited unexpectedly" with nothing to match on. Naming the driver that was actually used
        /// also puts it in the screenshot people attach to a bug report, which is the fact that was
        /// missing from every "black screen then crash" report so far. The advice half is phrased
        /// conditionally so it does not claim a cause for a failure that has nothing to do with
        /// graphics.</para>
        ///
        /// <para>Says nothing when the platform declared no driver — that is a process which never
        /// composed one, and a wrong claim about the setup is worse than no claim.</para>
        /// </summary>
        private static string GraphicsDriverHint()
        {
            if (!WPR.Engine.Graphics.GraphicsDriverPreference.HasPreference) return "";

            string driver = WPR.Engine.Graphics.GraphicsDriverPreference.ResolveDriverName() ?? "automatic";

            return "graphics driver: " + driver
                + " — if this game never starts on this device, try the other one under"
                + " settings → graphics.\n\n";
        }

        private static void ShowError(Activity host, string message, Action? onDismissed = null)
        {
            host.RunOnUiThread(() =>
            {
                if (host.IsFinishing || host.IsDestroyed) return;

                AlertDialog dialog = new AlertDialog.Builder(host)!
                    .SetTitle(WPR.Shell.Resources.AppRunError)!
                    .SetMessage(message)!
                    .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                    .Create()!;

                // Dismissal rather than the OK button: Back and a tap outside close this dialog
                // without the button ever firing, and a caller that finishes itself from here
                // would be stranded on a blank screen by either.
                if (onDismissed != null) dialog.DismissEvent += (_, _) => onDismissed();

                dialog.Show();
            });
        }
    }
}
