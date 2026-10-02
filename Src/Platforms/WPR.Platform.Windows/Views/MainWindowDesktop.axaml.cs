using WPR.Shell;
using WPR.Engine.Notifications;
using Avalonia.Controls;
using System;
using WPR.Common;
using WPR.Models;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;

using Newtonsoft.Json;

namespace WPR.Platform.Windows.Views
{
    public partial class MainWindowDesktop : Window
    {
        private MainViewNavigator _Navigator;

        /// <summary>
        /// Whether a title recorded as Silverlight is really a Silverlight/XNA mixed-mode app,
        /// and so belongs on the XNA host. Any failure answers false, which lands the title on
        /// the Silverlight host — the behaviour it had before this existed.
        /// </summary>
        /// <summary>
        /// Queues a WPR Hub crash report for a Silverlight UI app, which runs in the Avalonia host
        /// rather than through ApplicationLaunch (whose own filter covers every XNA and
        /// mixed-mode title). Always returns false, so it can sit in an exception filter.
        /// </summary>
        private static bool ReportSilverlightCrash(WPR.Models.Application app, Exception ex)
        {
            try
            {
                if (WPR.Engine.Online.OnlineBackend.Crashes is { } reporter)
                {
                    var report = WPR.Engine.Online.CrashReport.FromException(ex, WPR.Engine.Online.CrashReport.Sources.GameRun);
                    report.TitleId = app.ProductId;
                    report.TitleName = app.Name;
                    report.InstallFolder = System.IO.Path.Combine(
                        Configuration.Current!.DataPath(WPR.Models.Application.DataStoreFolder), app.ProductId!);
                    report.RuntimePath = WPR.Engine.Online.CrashReport.RuntimePaths.SilverlightAvalonia;
                    reporter.Report(report);
                }
            }
            catch (Exception reportEx)
            {
                Log.Warn(LogCategory.AppList, $"Could not queue a crash report: {reportEx.Message}");
            }
            return false;
        }

        /// <summary>
        /// The logging window's stop button: record the request and close whichever game is running.
        /// A game that has hung does not come back from that, and the launch awaits it, so if it has
        /// not gone within a few seconds the report is sent from here instead.
        /// </summary>
        private static void StopLoggingRun(WPR.Models.Application app, LoggingRunWindow window)
        {
            WPR.Shell.DiagnosticRun.MarkStopRequested();
            WPR.ApplicationLaunch.RequestExit();
            SilverlightLauncher.RequestExit();
            UnityPortLauncher.RequestExit();

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (WPR.ApplicationLaunch.CurrentGame == null) return; // closed normally; the launch path reports
                if (WPR.Shell.DiagnosticRun.Take(app.ProductId!) is not { } run) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                    window.ShowNote("The game did not close. Sending its log anyway; close WPR to end the game."));
                await Dispatcher.UIThread.InvokeAsync(() => SendLoggingRunAsync(run, "stopped by the player; the game did not close (hung)"));
            });
        }

        /// <summary>
        /// "How did it run?" after a normal run, now and again (the rules are
        /// <see cref="WPR.Shell.GameRatingPrompt"/>'s). The rating is sent as soon as it is picked.
        /// </summary>
        private static async Task MaybeAskRatingAsync(WPR.Models.Application app, string name, TimeSpan played, string runtimePath)
        {
            if (!WPR.Shell.GameRatingPrompt.ShouldAsk(app.ProductId, played)) return;

            var window = new RateGameWindow(name);
            WPR.Shell.GameRatingPrompt.MarkAsked(app.ProductId!);
            await window.ShowDialog(MessageBoxUtils.MainWindow);
            if (window.Declined) { WPR.Shell.GameRatingPrompt.Decline(app.ProductId!); return; }
            if (window.Chosen == null) return; // postponed: asked again next time this game is finished

            try
            {
                await WPR.Shell.GameRatingPrompt.SubmitAsync(app.ProductId!, window.Chosen, "windows",
                    Environment.OSVersion.Version.ToString(), runtimePath,
                    WPR.Engine.Graphics.GraphicsCapabilitiesStore.Current?.Backend);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, "[wpr-rating] could not send: " + ex.Message);
                await MessageBoxUtils.ShowSelectableErrorAsync("Rating not sent", "Your rating could not be sent to WPR Hub: " + ex.Message);
            }
        }

        /// <summary>Send a finished logging run's log and show the report code (or why it failed).</summary>
        private static async Task SendLoggingRunAsync(WPR.Shell.DiagnosticRun.Run run, string outcome, string? runtimePath = null)
        {
            string title, body;
            try
            {
                string code = await WPR.Shell.DiagnosticRun.SendAsync(run, outcome, null,
                    new System.Collections.Generic.Dictionary<string, string> { ["os"] = Environment.OSVersion.VersionString },
                    runtimePath, WPR.Engine.Graphics.GraphicsCapabilitiesStore.Current?.Backend);
                title = "Report sent";
                body = $"{code}\n\nThe log from this run of {run.Title} ({outcome}) was sent to WPR Hub. Quote the code above if you open a GitHub issue about this game.";
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, "[wpr-diag] could not send the logging run: " + ex);
                title = "Report not sent";
                body = $"The run ended ({outcome}), but its log could not be sent: {ex.Message}";
            }
            await MessageBoxUtils.ShowSelectableErrorAsync(title, body);
        }

        private static bool IsMixedMode(WPR.Models.Application app)
        {
            try
            {
                string installFolder = System.IO.Path.Combine(
                    Configuration.Current!.DataPath(WPR.Models.Application.DataStoreFolder),
                    app.ProductId!);
                return WPR.MixedModeDetection.IsMixedMode(
                    installFolder, app.Assembly, msg => Log.Info(LogCategory.AppList, msg));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"mixed-mode probe threw: {ex.Message}");
                return false;
            }
        }

        public MainWindowDesktop()
        {
            InitializeComponent();

            // Title is set here, not in XAML: it used to be a hardcoded "WPR 0.0.18-alpha" that
            // drifted out of step with the About page. Both now read the assembly version.
            Title = AppVersion.TitleText;

            // Ask any running game to exit when the main window closes so its threads
            // (FNA render/audio loops are non-background) don't keep the process alive.
            Closing += (_, _) =>
            {
                WPR.ApplicationLaunch.RequestExit();
                SilverlightLauncher.RequestExit();
                UnityPortLauncher.RequestExit();
            };

//RnD
            MessageBoxUtils.MainWindow = this;
            ServicesSetup.Start();

            // WPR Hub: send whatever an earlier session queued (crash reports, a game package the
            // hub asked for, scores played while signed out). Background; never throws.
            _ = WPR.Engine.Online.OnlineBackend.FlushAsync();

            // ...and bring down what the account earned on other devices (sign-in does this too).
            WPR.Shell.HubSetup.RestoreProgressInBackground();

            // "Online" on WPR Hub while this window is open and someone is signed in; playing X
            // while a game runs (PlaytimeTracker tells it). Offline when the window closes.
            WPR.Engine.Online.OnlineBackend.Presence?.Start();
            Closing += (_, _) =>
            {
                try { WPR.Engine.Online.OnlineBackend.Presence?.GoOfflineAsync().Wait(TimeSpan.FromSeconds(3)); }
                catch (Exception) { /* the hub times the session out anyway */ }
            };

            ApplicationLaunchRequest.Incoming += async (sender, args) =>
            {
                Hide();

                // "Play with logging": the verbose log for this run, a topmost stop button, and the
                // run's log sent as a report when it ends (after the launch below returns).
                LoggingRunWindow? loggingWindow = null;
                string loggingTitle = WPR.HardcodedAchievementCatalogue.GameName(args.Target.ProductId) ?? args.Target.Name ?? "game";
                if (args.Diagnostic && !string.IsNullOrEmpty(args.Target.ProductId))
                {
                    WPR.Shell.DiagnosticRun.Begin(args.Target.ProductId, loggingTitle);
                    WPR.Common.WprHostEnvironment.DiagnosticRun = true;
                    loggingWindow = new LoggingRunWindow(loggingTitle);
                    loggingWindow.StopRequested += () => StopLoggingRun(args.Target, loggingWindow);
                    loggingWindow.Show();
                }
                else if (!string.IsNullOrEmpty(args.Target.ProductId))
                {
                    // A record from a logging run that never finished must not turn this run into a report.
                    WPR.Shell.DiagnosticRun.Take(args.Target.ProductId);
                }

                try
                {
                    if (NotificationBackend.Manager != null)
                        _ = NotificationBackend.Manager.ShowNotification(new Notification()
                        {
                            Title = WPR.Shell.Resources.LaunchingInProcess,
                            // Prefer the curated catalogue name (e.g. "Risk") over the
                            // manifest title (e.g. "Risky", or an unresolved @AppResLib
                            // resource), matching how the game window title is set in
                            // ApplicationLaunch. Falls back to the manifest name when no
                            // catalogue ships for the product.
                            Body = WPR.HardcodedAchievementCatalogue.GameName(args.Target.ProductId) ?? args.Target.Name!,
                            // Game icon goes into the avatar slot — the manager circle-crops it.
                            ImagePath = Configuration.Current!.DataPath(args.Target.IconPath),
                            AttributionText = "Windows Phone Reimplementation",
                        }, expirationTime: DateTime.Now + TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ex] ShowNotification ex.: " + ex.Message, "; StackTrace: " + ex.StackTrace);
                }
                
                bool runOk = true;
                DateTime launchedAt = DateTime.UtcNow;
                string runtimePath = WPR.Online.Hub.Client.RuntimePaths.XnaFna;

                var test = JsonConvert.SerializeObject(args.Target);
                Debug.WriteLine("[i] " + test);

                string ErrorMessage = "";
                string StackTrace = "";

                try
                {
                    // Externally-built native ports (Unity rebuilds, etc.) are launched as a
                    // standalone process rather than hosted in-process. Detected by a
                    // wpr-port.json in the install folder; returns false for normal titles so
                    // they fall through to the Silverlight / XNA hosts below.
                    if (await UnityPortLauncher.TryLaunchAsync(args.Target))
                    {
                        runtimePath = WPR.Online.Hub.Client.RuntimePaths.UnitySwap;
                    }
                    else
                    {
                        // A Silverlight/XNA MIXED-MODE title declares RuntimeType="Silverlight"
                        // in its manifest and is recorded as ApplicationType.Silverlight, but it
                        // has no Silverlight UI to host: its page is empty and the game draws
                        // into it through a SharedGraphicsDeviceManager. Sending one to the
                        // Silverlight host fails at the first navigation, which is what all ten
                        // of them did here until 2026-09-23.
                        //
                        // Android never had this bug because it has no Silverlight host at all —
                        // every title goes to FnaGameHost and ApplicationLaunch's own mixed-mode
                        // gate picks MixedModeGame. This is the desktop's equivalent of that
                        // gate, and it deliberately asks the SAME detector rather than a second
                        // rule, so the two heads cannot disagree about what a game is.
                        if (args.Target.ApplicationType == ApplicationType.Silverlight &&
                            !IsMixedMode(args.Target))
                        {
                            runtimePath = WPR.Online.Hub.Client.RuntimePaths.SilverlightAvalonia;
                            try
                            {
                                await SilverlightLauncher.LaunchAsync(args.Target);
                            }
                            catch (Exception ex) when (ReportSilverlightCrash(args.Target, ex))
                            {
                                throw; // unreachable: the filter only records
                            }
                        }
                        else
                        {
                            await XnaLauncher.LaunchAsync(args.Target);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(LogCategory.AppList, $"Game run error: \n{ex}");

                    Debug.WriteLine($"[ex] Game run error: \n{ex}");
                    Debug.WriteLine($"Error message: \n{ex.Message}");

                    StackTrace = ex.ToString();
                    ErrorMessage = ex.Message;
                    
                    //RnD
                    runOk = false;
                }

                Show();

                // The game is gone, so this is the moment to send what it left behind: a crash
                // report (queued by ApplicationLaunch, or above for the Silverlight host) and any
                // leaderboard scores it posted.
                _ = WPR.Engine.Online.OnlineBackend.FlushAsync();

                if (loggingWindow != null)
                {
                    WPR.Common.WprHostEnvironment.DiagnosticRun = false;
                    loggingWindow.Close();
                    // Null when the stop button already sent it because the game would not close.
                    if (WPR.Shell.DiagnosticRun.Take(args.Target.ProductId!) is { } run)
                    {
                        string outcome = run.StopRequested ? "stopped by the player"
                            : runOk ? "the game exited normally" : "the game crashed: " + ErrorMessage;
                        await SendLoggingRunAsync(run, outcome, runtimePath);
                    }
                }
                else if (runOk)
                {
                    await MaybeAskRatingAsync(args.Target, loggingTitle, DateTime.UtcNow - launchedAt, runtimePath);
                }

                if (!runOk)
                {
                    string body =
                        WPR.Shell.Resources.ExceptionRunApp + Environment.NewLine + Environment.NewLine +
                        "Message:" + Environment.NewLine + ErrorMessage + Environment.NewLine + Environment.NewLine +
                        "Stack trace:" + Environment.NewLine + StackTrace;

                    await MessageBoxUtils.ShowSelectableErrorAsync(
                        title: WPR.Shell.Resources.AppRunError,
                        body: body);
                }
            };


            _Navigator = new MainViewNavigator();
            _Navigator.SetupNavigation(this.Get<TabControl>("navigationControl"), 
                this.Get<TransitioningContentControl>("contentControl"));
        }
    }
}
