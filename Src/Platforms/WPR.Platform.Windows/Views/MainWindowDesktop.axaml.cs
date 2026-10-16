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
        /// The logging window's stop button: record the request and close whichever game is running.
        /// A game that has hung does not come back from that, and the launch awaits it, so if it has
        /// not gone within a few seconds the report is sent from here instead.
        /// </summary>
        private static void StopLoggingRun(WPR.Models.Application app, LoggingRunWindow window)
        {
            WPR.Shell.DiagnosticRun.MarkStopRequested();
            WPR.ApplicationLaunch.RequestExit();
            GameMakerLauncher.RequestExit();
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
                GameMakerLauncher.RequestExit();
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
                    // Externally-built native ports (Unity rebuilds, etc.) and GameMaker exports
                    // are launched as standalone processes rather than hosted in-process. Each
                    // answers false for a title that is not one of its own (and GameMaker also
                    // when Runner.exe is unavailable), which falls through to ApplicationLaunch.
                    // That hosts every remaining kind, Silverlight included: SilverlightGame is
                    // the one Silverlight host on both heads since 2026-10-13 (the Avalonia host
                    // was deleted in Stage D of Plans/SILVERLIGHT-XNA-CONVERGENCE.md).
                    string installFolder = System.IO.Path.Combine(
                        Configuration.Current!.DataPath(WPR.Models.Application.DataStoreFolder),
                        args.Target.ProductId!);
                    if (await UnityPortLauncher.TryLaunchAsync(args.Target))
                    {
                        runtimePath = WPR.Online.Hub.Client.RuntimePaths.UnitySwap;
                    }
                    // A WP8 GameMaker export installs as ModernNative (its WinPhoneRunnerAppComponent
                    // is a Direct3D/XAML component), so it is offered to Runner.exe too; without one
                    // it falls through to the WP8 host, which runs the ARM runner itself.
                    else if (args.Target.ApplicationType is ApplicationType.Silverlight or ApplicationType.ModernNative &&
                             await GameMakerLauncher.TryLaunchAsync(installFolder, args.Target.ProductId ?? "", args.Target.Name ?? ""))
                    {
                        runtimePath = WPR.Online.Hub.Client.RuntimePaths.Other; // the hub has no GameMaker path
                    }
                    else
                    {
                        await XnaLauncher.LaunchAsync(args.Target);
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
                // report (queued by ApplicationLaunch) and any
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
