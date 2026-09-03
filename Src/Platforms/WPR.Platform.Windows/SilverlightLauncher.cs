using WPR.Input.Keyboard;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WPR.Platform.Windows.Input;
using WPR.Common;
using WPR;
using SLFrameView = Microsoft.Phone.Controls.PhoneApplicationFrameView;
using AvTextBlock = Avalonia.Controls.TextBlock;
using WPRApp = WPR.WindowsCompability.Application;
using WPRModel = WPR.Models.Application;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// Boots an installed Silverlight XAP and shows it in an Avalonia window.
    /// Returns when the user closes the window.
    /// </summary>
    public static class SilverlightLauncher
    {
        /// <summary>
        /// Window currently hosting a running Silverlight app, or null if none.
        /// Used by the host so it can close the game when the main window closes.
        /// </summary>
        private static Window? _CurrentWindow;

        /// <summary>
        /// Asks the active Silverlight game window (if any) to close. Returns true if one was open.
        /// Must be called on the UI thread.
        /// </summary>
        public static bool RequestExit()
        {
            Window? w = _CurrentWindow;
            if (w == null) return false;
            try { w.Close(); }
            catch { /* best-effort: window may already be tearing down */ }
            return true;
        }

        public static async Task LaunchAsync(WPRModel app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (string.IsNullOrEmpty(app.Assembly))
                throw new InvalidOperationException("Application has no assembly recorded; cannot launch.");
            if (string.IsNullOrEmpty(app.EntryPoint))
                throw new InvalidOperationException("Application has no entry-point type recorded; cannot launch.");

            // Locate the install folder (matches XNA path's CurrentProductFolder convention).
            WPRApp.Current.ProductId = app.ProductId;
            WPR.SilverlightCompability.HostContext.CurrentProductId = app.ProductId;
            // Neutral ambient holder — what GamerServices reads (see WprHostEnvironment).
            WPR.Common.WprHostEnvironment.CurrentProductId = app.ProductId;
            string installFolder = Path.Combine(
                Configuration.Current!.DataPath(WPRModel.DataStoreFolder),
                app.ProductId!);
            WPR.SilverlightCompability.HostContext.CurrentInstallFolder = installFolder;

            // GameMaker Studio fast-path: hybrid Silverlight + WinRT WP apps that bundle a
            // .win file are running GameMaker's runtime, not real Silverlight UI. Drive them
            // through YoYo's official Runner.exe instead — vastly more compatible than trying
            // to interpret the bytecode ourselves.
            if (GameMakerLauncher.LooksLikeGameMakerApp(installFolder))
            {
                Log.Info(LogCategory.AppLaunch,
                    $"Detected GameMaker Studio app (game.win present); routing through Runner.exe");
                var proc = GameMakerLauncher.Launch(installFolder, app.ProductId ?? "", app.Name ?? "");
                if (proc != null)
                {
                    // Wait for the Runner to exit; ignore the Silverlight host.
                    try { await Task.Run(() => proc.WaitForExit()); }
                    catch { /* user closed window etc. */ }
                    return;
                }
                Log.Warn(LogCategory.AppLaunch,
                    "GameMaker fast-path failed to spawn Runner.exe; falling back to Silverlight host");
            }

            // Boot off the UI thread so the App ctor's reflection / resource reads don't tie up
            // Avalonia's render loop. Result is consumed back on UI thread to build the window.
            SilverlightAppHost.HostResult result = await Task.Run(() =>
                SilverlightAppHost.Boot(installFolder, app.Assembly!, app.EntryPoint!));

            if (result.RootVisual == null)
                throw new InvalidOperationException(
                    "Silverlight app booted but Application.Current.RootVisual was never set. " +
                    "The app's App.xaml.cs is expected to assign a PhoneApplicationFrame (or other UIElement) to RootVisual.");

            await ShowAndAwaitCloseAsync(app, result);
        }

        private static Task ShowAndAwaitCloseAsync(WPRModel app, SilverlightAppHost.HostResult host)
        {
            // Window height = phone surface (800) + hardware-button strip (56).
            // The strip sits below the phone surface like the real WP7 bezel.
            var window = new Window
            {
                Title = $"{app.Name} — {app.Author ?? "?"}",
                Width = 480,
                Height = 800 + PhoneHardwareButtons.StripHeight,
            };

            // Wrap the booted RootVisual. PhoneApplicationFrameView only knows how to host
            // a PhoneApplicationFrame today; if the user's RootVisual is something else, fall
            // back to a clear error message in the window content.
            if (host.RootFrame != null)
            {
                var frameView = new SLFrameView(host.RootFrame);
                var bezel = new PhoneHardwareButtons(
                    onBack: () =>
                    {
                        // WP7 contract: page-level handler runs first; if it cancels,
                        // stop. Otherwise pop the back-stack. At the root WP7 would
                        // exit the app — on the desktop host we deliberately do NOT
                        // close the window (the user closes via the X), so a back
                        // press at the root page is silently dropped.
                        host.RootFrame.HandleBackKey();
                    },
                    onStart: () =>
                    {
                        // Real WP7 Start jumps to the home screen with the game
                        // suspended. We have no home screen, and the user has asked
                        // that bezel buttons never close the window. No-op for now —
                        // could later be wired to a "screenshot" or "pause" action.
                    },
                    onSearch: () =>
                    {
                        // WP7 search opens Bing search via the OS. Nothing to map this
                        // to on the desktop; intentionally a no-op for now.
                    });

                var dock = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(bezel, Dock.Bottom);
                dock.Children.Add(bezel);

                // Overlay the tilt indicator on top of the frame view via a Grid so it sits
                // in the same cell as the game's render surface. Toggled lazily by config —
                // a user that never enables it pays nothing.
                if (Configuration.Current?.TiltOverlayEnabled == true)
                {
                    var stack = new Grid();
                    stack.Children.Add(frameView);
                    stack.Children.Add(new TiltOverlay
                    {
                        IsHitTestVisible = false,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    });
                    dock.Children.Add(stack);
                }
                else
                {
                    dock.Children.Add(frameView);
                }

                window.Content = dock;
            }
            else
            {
                window.Content = new AvTextBlock
                {
                    Text =
                        "The app's RootVisual is " +
                        (host.RootVisual?.GetType().FullName ?? "<null>") +
                        " — only PhoneApplicationFrame is supported as a root in 1.5. " +
                        "The app started but cannot be displayed in this build.",
                    Margin = new Avalonia.Thickness(16),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                };
            }

            // Wire the keyboard → accelerometer bridge at the window level so a focused child
            // (a text input for instance) doesn't steal the keystrokes. Tunneling = "preview"
            // — fires before the focused control sees the event, so tilt keys always work.
            KeyboardTiltBindings.ApplyConfigurationToHost();
            window.AddHandler(InputElement.KeyDownEvent, OnTiltKeyDown, RoutingStrategies.Tunnel);
            window.AddHandler(InputElement.KeyUpEvent,   OnTiltKeyUp,   RoutingStrategies.Tunnel);

            // If the user alt-tabs away mid-game we'll never see the corresponding KeyUp,
            // so reset the simulator on focus loss to avoid a stuck tilt.
            window.Deactivated += (_, _) => KeyboardAccelerometerHost.ResetAll();

            var tcs = new TaskCompletionSource<object?>();
            window.Closed += (s, e) =>
            {
                _CurrentWindow = null;
                window.RemoveHandler(InputElement.KeyDownEvent, OnTiltKeyDown);
                window.RemoveHandler(InputElement.KeyUpEvent,   OnTiltKeyUp);
                KeyboardAccelerometerHost.ResetAll();
                // Drop user-assembly references, then unload the ALC so the .dll file lock
                // releases before the user tries to reinstall. Anything that holds a managed
                // reference to types in the user assembly will pin the ALC and prevent unload.
                try
                {
                    window.Content = null;
                    WPRApp.Current.RootVisual = null;
                    WPRApp.ResetCurrent();
                    WPR.SilverlightCompability.HostContext.UserAssembly = null;
                    WPR.SilverlightCompability.HostContext.CurrentProductId = null;
                    WPR.SilverlightCompability.HostContext.CurrentInstallFolder = null;
                    host.LoadContext?.Unload();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch { /* unload is best-effort */ }
                tcs.TrySetResult(null);
            };
            _CurrentWindow = window;
            window.Show();
            return tcs.Task;
        }

        private static void OnTiltKeyDown(object? sender, KeyEventArgs e)
        {
            // OS key-repeat fires KeyDown multiple times while the key is held; that's fine
            // because NotifyTiltKey just sets the down-state flag — repeated "true" calls
            // are idempotent.
            var dir = KeyboardTiltBindings.ResolveKeyName(e.Key.ToString());
            if (dir.HasValue)
            {
                KeyboardAccelerometerHost.NotifyTiltKey(dir.Value, true);
            }
        }

        private static void OnTiltKeyUp(object? sender, KeyEventArgs e)
        {
            var dir = KeyboardTiltBindings.ResolveKeyName(e.Key.ToString());
            if (dir.HasValue)
            {
                KeyboardAccelerometerHost.NotifyTiltKey(dir.Value, false);
            }
        }
    }
}
