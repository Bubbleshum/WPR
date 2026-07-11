using Avalonia.Controls;
using System;
using WPR.Common;
using WPR.Models;
using System.Diagnostics;

using Newtonsoft.Json;

namespace WPR.UI.Views
{
    public partial class MainWindowDesktop : Window
    {
        private MainViewNavigator _Navigator;

        public MainWindowDesktop()
        {
            InitializeComponent();

            // Ask any running game to exit when the main window closes so its threads
            // (FNA render/audio loops are non-background) don't keep the process alive.
            Closing += (_, _) =>
            {
                WPR.ApplicationLaunch.RequestExit();
                SilverlightLauncher.RequestExit();
                UnityPortLauncher.RequestExit();
            };

//RnD
#if (true)//!__MOBILE__
#if !__MOBILE__
            MessageBoxUtils.MainWindow = this;
#endif
            ServicesSetup.Start();

            ApplicationLaunchRequest.Incoming += async (sender, args) =>
            {
                Hide();

                try
                {
                    if (NativeUI.NotificationManager != null)
                        _ = NativeUI.NotificationManager.ShowNotification(new DesktopNotifications.Notification()
                        {
                            Title = Properties.Resources.LaunchingInProcess,
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
                    if (!await UnityPortLauncher.TryLaunchAsync(args.Target))
                    {
                        if (args.Target.ApplicationType == ApplicationType.Silverlight)
                        {
                            await SilverlightLauncher.LaunchAsync(args.Target);
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

                if (!runOk)
                {
                    string body =
                        Properties.Resources.ExceptionRunApp + Environment.NewLine + Environment.NewLine +
                        "Message:" + Environment.NewLine + ErrorMessage + Environment.NewLine + Environment.NewLine +
                        "Stack trace:" + Environment.NewLine + StackTrace;

                    await MessageBoxUtils.ShowSelectableErrorAsync(
                        title: Properties.Resources.AppRunError,
                        body: body);
                }
            };
#endif


            _Navigator = new MainViewNavigator();
            _Navigator.SetupNavigation(this.Get<TabControl>("navigationControl"), 
                this.Get<TransitioningContentControl>("contentControl"));
        }
    }
}
