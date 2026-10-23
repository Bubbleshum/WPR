using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace WPR.Platform.Windows
{
    /// <summary>The buttons a launcher dialog shows.</summary>
    public enum DialogButtons { Ok, OkCancel, YesNo, YesNoCancel }

    /// <summary>What a launcher dialog answered; <see cref="None"/> when it was closed without a button.</summary>
    public enum DialogAnswer { None, Ok, Cancel, Yes, No }

    /// <summary>A launcher dialog's kind. Accepted for the callers' sake; the window draws no icon.</summary>
    public enum DialogIcon { None, Info, Question, Warning, Error, Success }

    /// <summary>
    /// Every dialog the desktop head shows, built in code on plain Avalonia windows.
    /// </summary>
    /// <remarks>
    /// This used to sit on MessageBox.Avalonia 2.2.0, which was built against an older Avalonia than
    /// this head's: its windows cannot be constructed here (<c>MissingMethodException:
    /// AvaloniaObjectExtensions.Bind(IAvaloniaObject, …)</c> from <c>MsBoxStandardWindow.!XamlIlPopulate</c>),
    /// and the fallbacks that caught that never completed their task when the window was closed. That
    /// was every XNA <c>Guide.BeginShowMessageBox</c> on the desktop (WPR Hub group 8bf2d758d13f: the
    /// quit / rate-us / sign-in prompts of Plants vs. Zombies, Doodle Jump, Fruit Ninja, Sonic 4…).
    /// The package is gone; do not bring it back for one dialog.
    /// </remarks>
    public static class MessageBoxUtils
    {
        public static Window MainWindow { get; set; }

        /// <summary>The labels, and the answer for each, that <paramref name="buttons"/> shows.</summary>
        public static (string[] Labels, DialogAnswer[] Answers) Describe(DialogButtons buttons) => buttons switch
        {
            DialogButtons.OkCancel => (new[] { "OK", "Cancel" }, new[] { DialogAnswer.Ok, DialogAnswer.Cancel }),
            DialogButtons.YesNo => (new[] { "Yes", "No" }, new[] { DialogAnswer.Yes, DialogAnswer.No }),
            DialogButtons.YesNoCancel => (new[] { "Yes", "No", "Cancel" },
                new[] { DialogAnswer.Yes, DialogAnswer.No, DialogAnswer.Cancel }),
            _ => (new[] { "OK" }, new[] { DialogAnswer.Ok }),
        };

        /// <summary>Maps the window's answer (button index, or -1 for closed) to a <see cref="DialogAnswer"/>.</summary>
        public static DialogAnswer Resolve(DialogButtons buttons, int chosen)
        {
            var (_, answers) = Describe(buttons);
            return chosen >= 0 && chosen < answers.Length ? answers[chosen] : DialogAnswer.None;
        }

        /// <summary>
        /// A launcher dialog over <see cref="MainWindow"/>: completes with the answer for the button
        /// pressed, or <see cref="DialogAnswer.None"/> when it was closed without one.
        /// </summary>
        public static async Task<DialogAnswer> GetMessageDialogResult(string title,
            string text, DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.Info)
        {
            var (labels, _) = Describe(buttons);
            int chosen = await ShowDialogWindowAsync(title, title, text, labels, 0, topmost: false, owner: MainWindow);
            return Resolve(buttons, chosen);
        }

        /// <summary>
        /// The presenter behind a game's Silverlight <c>MessageBox.Show</c>: a WP-styled window with
        /// one button per label, completing with the index pressed or -1 when it was dismissed (Esc,
        /// closing the window). The shim maps that to a <c>MessageBoxResult</c>.
        /// </summary>
        public static Task<int> ShowGameMessageBoxAsync(string caption, string text, IReadOnlyList<string> labels) =>
            ShowGameMessageBoxAsync(caption, text, labels, 0);

        /// <summary>
        /// As the three-argument overload, with <paramref name="defaultIndex"/> the button Enter
        /// presses. XNA's <c>Guide.BeginShowMessageBox</c> comes here with the game's own labels and
        /// focus button; Guide maps -1 to the null XNA documents for a cancelled box.
        /// </summary>
        /// <remarks>
        /// The game window is SDL's, not Avalonia's, so the dialog cannot be owned by it: it is
        /// topmost instead, and the window that had the foreground (the game) gets it back on close.
        /// </remarks>
        public static Task<int> ShowGameMessageBoxAsync(string caption, string text, IReadOnlyList<string> labels, int defaultIndex) =>
            ShowDialogWindowAsync(string.IsNullOrEmpty(caption) ? "WPR" : caption, caption, text, labels, defaultIndex,
                topmost: true, owner: null);

        /// <summary>
        /// The one dialog window: <paramref name="heading"/> over <paramref name="text"/> and a button
        /// per label, completing with the index pressed or -1. EVERY way out completes it (a button,
        /// Esc, the close button, Alt+F4, WPR shutting down), because callers wait on it, the game
        /// thread included. With no <paramref name="owner"/> the foreground goes back on close to
        /// whatever had it when the dialog opened.
        /// </summary>
        private static Task<int> ShowDialogWindowAsync(string windowTitle, string heading, string text,
            IReadOnlyList<string> labels, int defaultIndex, bool topmost, Window? owner)
        {
            Func<Task<int>> show = () =>
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool owned = owner != null && owner.IsVisible;
                IntPtr previousForeground = owned ? IntPtr.Zero : GetForegroundWindow();

                var w = new Window
                {
                    Title = string.IsNullOrEmpty(windowTitle) ? "WPR" : windowTitle,
                    Width = 480,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    Topmost = topmost,
                    ShowInTaskbar = false,
                    WindowStartupLocation = owned ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                };

                var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 12 };
                if (!string.IsNullOrEmpty(heading))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = heading,
                        FontSize = 22,
                        FontWeight = FontWeight.Light,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                panel.Children.Add(new TextBlock { Text = text ?? string.Empty, TextWrapping = TextWrapping.Wrap });

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                if (defaultIndex < 0 || defaultIndex >= labels.Count) defaultIndex = 0;
                for (int i = 0; i < labels.Count; i++)
                {
                    int index = i;
                    var b = new Button { Content = labels[i] ?? string.Empty, MinWidth = 120, IsDefault = i == defaultIndex };
                    b.Click += (_, _) => { tcs.TrySetResult(index); w.Close(); };
                    buttons.Children.Add(b);
                }
                panel.Children.Add(buttons);
                w.Content = panel;

                w.KeyDown += (_, e) =>
                {
                    if (e.Key == Avalonia.Input.Key.Escape)
                    {
                        e.Handled = true;
                        w.Close();
                    }
                };
                // Closed covers Esc, the title bar's close button, Alt+F4 and WPR shutting down.
                w.Closed += (_, _) =>
                {
                    tcs.TrySetResult(-1);
                    if (previousForeground != IntPtr.Zero)
                    {
                        try { SetForegroundWindow(previousForeground); } catch { /* best effort */ }
                    }
                };

                try
                {
                    if (owned)
                        _ = w.ShowDialog(owner!);
                    else
                        w.Show();
                    w.Activate();
                }
                catch
                {
                    // A window that never opened can never close: answer as dismissed.
                    tcs.TrySetResult(-1);
                    throw;
                }
                return tcs.Task;
            };

            if (Dispatcher.UIThread.CheckAccess())
                return show();

            return Dispatcher.UIThread.InvokeAsync(show);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Shows an error dialog whose body is a read-only, selectable, multi-line TextBox so
        /// the user can copy the text. A "Copy" button copies the full body to the clipboard.
        /// Resizable. Falls back to <see cref="GetMessageDialogResult"/> on Android.
        /// </summary>
        public static Task ShowSelectableErrorAsync(string title, string body)
        {
            Func<Task> returnTaskFunc = () =>
            {
                var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

                var w = new Window
                {
                    Title = title,
                    Width = 720,
                    Height = 480,
                    MinWidth = 360,
                    MinHeight = 240,
                    CanResize = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };

                var grid = new Grid { Margin = new Thickness(16) };
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                var header = new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeight.Bold,
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                Grid.SetRow(header, 0);
                grid.Children.Add(header);

                var errorBox = new TextBox
                {
                    Text = body,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(errorBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(errorBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                Grid.SetRow(errorBox, 1);
                grid.Children.Add(errorBox);

                var buttonsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                };
                Grid.SetRow(buttonsPanel, 2);

                var copyBtn = new Button { Content = "Copy", MinWidth = 80 };
                copyBtn.Click += async (_, __) =>
                {
                    try
                    {
                        var clipboard = TopLevel.GetTopLevel(w)?.Clipboard;
                        if (clipboard != null) await clipboard.SetTextAsync(body);
                        copyBtn.Content = "Copied";
                    }
                    catch { /* clipboard unavailable; surface nothing */ }
                };
                buttonsPanel.Children.Add(copyBtn);

                var okBtn = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
                okBtn.Click += (_, __) => { tcs.TrySetResult(null); w.Close(); };
                buttonsPanel.Children.Add(okBtn);

                grid.Children.Add(buttonsPanel);
                w.Content = grid;
                w.Closed += (_, __) => tcs.TrySetResult(null);

                if (MainWindow != null)
                {
                    _ = w.ShowDialog(MainWindow);
                }
                else
                {
                    w.Show();
                }

                return tcs.Task;
            };

            if (Dispatcher.UIThread.CheckAccess())
                return returnTaskFunc();

            return Dispatcher.UIThread.InvokeAsync(returnTaskFunc);
        }


        /// <summary>
        /// XNA's <c>Guide.BeginShowKeyboardInput</c>: a text box over <paramref name="defaultText"/>,
        /// completing with the text entered, or null when cancelled or closed (Guide then answers the
        /// default text). Topmost and unowned for the same reason as the game message box.
        /// </summary>
        public static Task<string?> GetInputResult(string title, string description, string defaultText)
        {
            Func<Task<string?>> show = () =>
            {
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                IntPtr previousForeground = GetForegroundWindow();

                var w = new Window
                {
                    Title = string.IsNullOrEmpty(title) ? "WPR" : title,
                    Width = 480,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    Topmost = true,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };

                var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 12 };
                if (!string.IsNullOrEmpty(title))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = title,
                        FontSize = 22,
                        FontWeight = FontWeight.Light,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                if (!string.IsNullOrEmpty(description))
                    panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });

                var input = new TextBox { Text = defaultText ?? string.Empty };
                panel.Children.Add(input);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                var btnOk = new Button { Content = "OK", MinWidth = 120, IsDefault = true };
                btnOk.Click += (_, _) => { tcs.TrySetResult(input.Text ?? string.Empty); w.Close(); };
                var btnCancel = new Button { Content = "Cancel", MinWidth = 120, IsCancel = true };
                btnCancel.Click += (_, _) => w.Close();
                buttons.Children.Add(btnOk);
                buttons.Children.Add(btnCancel);
                panel.Children.Add(buttons);
                w.Content = panel;

                // Closed covers Cancel, Esc (IsCancel), the close button, Alt+F4 and shutdown.
                w.Closed += (_, _) =>
                {
                    tcs.TrySetResult(null);
                    if (previousForeground != IntPtr.Zero)
                    {
                        try { SetForegroundWindow(previousForeground); } catch { /* best effort */ }
                    }
                };
                w.Opened += (_, _) => input.Focus();

                try
                {
                    w.Show();
                    w.Activate();
                }
                catch
                {
                    tcs.TrySetResult(null);
                    throw;
                }
                return tcs.Task;
            };

            if (Dispatcher.UIThread.CheckAccess())
                return show();

            return Dispatcher.UIThread.InvokeAsync(show);
        }
    }
}
