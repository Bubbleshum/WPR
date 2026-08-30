using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using MessageBox.Avalonia;

namespace WPR.Platform.Windows
{
    public static class MessageBoxUtils
    {
        public static Window MainWindow { get; set; }

        public static Task<MessageBox.Avalonia.Enums.ButtonResult> GetMessageDialogResult(string title,
            string text, MessageBox.Avalonia.Enums.ButtonEnum buttons = MessageBox.Avalonia.Enums.ButtonEnum.Ok,
            MessageBox.Avalonia.Enums.Icon icon = MessageBox.Avalonia.Enums.Icon.Info, IEnumerable<string> ?buttonTexts = null,
            bool modalOnWindow = true, bool dispatchMain = false)
        {
            Func<Task<MessageBox.Avalonia.Enums.ButtonResult>> returnTaskFunc = () =>
            {
                try
                {
                    var msgBox = MessageBoxManager.GetMessageBoxStandardWindow(
                        title: title,
                        text: text,
                        icon: icon,
                        @enum: buttons,
                        windowStartupLocation: WindowStartupLocation.CenterScreen);
                    
                    return modalOnWindow ? msgBox.ShowDialog(MainWindow) : msgBox.Show();
                }
                catch (System.MissingMethodException)
                {
                    var tcs = new TaskCompletionSource<MessageBox.Avalonia.Enums.ButtonResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    
                    var w = new Window
                    {
                        Title = title,
                        Width = 420,
                        Height = 200,
                        CanResize = false,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    
                    var panel = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 12,
                        Margin = new Thickness(16)
                    };
                    
                    var content = new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White
                    };
                    
                    var buttonsPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8
                    };
                    
                    void CloseWith(MessageBox.Avalonia.Enums.ButtonResult r)
                    {
                        tcs.TrySetResult(r);
                        w.Close();
                    }
                    
                    void AddButton(string caption, MessageBox.Avalonia.Enums.ButtonResult r)
                    {
                        var b = new Button { Content = caption, MinWidth = 80 };
                        b.Click += (_, __) => CloseWith(r);
                        buttonsPanel.Children.Add(b);
                    }
                    
                    switch (buttons)
                    {
                        case MessageBox.Avalonia.Enums.ButtonEnum.Ok:
                            AddButton("OK", MessageBox.Avalonia.Enums.ButtonResult.Ok);
                            break;
                        case MessageBox.Avalonia.Enums.ButtonEnum.OkCancel:
                            AddButton("Cancel", MessageBox.Avalonia.Enums.ButtonResult.Cancel);
                            AddButton("OK", MessageBox.Avalonia.Enums.ButtonResult.Ok);
                            break;
                        case MessageBox.Avalonia.Enums.ButtonEnum.YesNo:
                            AddButton("No", MessageBox.Avalonia.Enums.ButtonResult.No);
                            AddButton("Yes", MessageBox.Avalonia.Enums.ButtonResult.Yes);
                            break;
                        case MessageBox.Avalonia.Enums.ButtonEnum.YesNoCancel:
                            AddButton("Cancel", MessageBox.Avalonia.Enums.ButtonResult.Cancel);
                            AddButton("No", MessageBox.Avalonia.Enums.ButtonResult.No);
                            AddButton("Yes", MessageBox.Avalonia.Enums.ButtonResult.Yes);
                            break;
                        default:
                            AddButton("OK", MessageBox.Avalonia.Enums.ButtonResult.Ok);
                            break;
                    }
                    
                    panel.Children.Add(content);
                    panel.Children.Add(buttonsPanel);
                    w.Content = panel;
                    
                    if (modalOnWindow && MainWindow != null)
                    {
                        _ = w.ShowDialog(MainWindow);
                    }
                    else
                    {
                        w.Show();
                    }
                    
                    return tcs.Task;
                }
            };
            
            if (dispatchMain)
            {
                return Dispatcher.UIThread.InvokeAsync(returnTaskFunc);
            }

            return returnTaskFunc();
        }

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

        public static Task<string> GetInputResult(string title, string description, string defaultText, bool isModal = true, bool dispatchMain = false)
        {
            Func<Task<string>> returnTaskFunc = async () =>
            {
                try
                {
                    var msgBox = MessageBoxManager.GetMessageBoxInputWindow(
                        new MessageBox.Avalonia.DTO.MessageBoxInputParams()
                        {
                            ContentTitle = title,
                            ContentMessage = description,
                            InputDefaultValue = defaultText,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen
                        }
                    );
                    
                    var result = await (isModal ? msgBox.ShowDialog(MainWindow) : msgBox.Show());
                    return result.Message;
                }
                catch (System.MissingMethodException)
                {
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    
                    var w = new Window
                    {
                        Title = title,
                        Width = 420,
                        Height = 220,
                        CanResize = false,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    
                    var panel = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 10,
                        Margin = new Thickness(16)
                    };
                    
                    var desc = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
                    var input = new TextBox { Text = defaultText };
                    
                    var buttons = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8
                    };
                    
                    var btnCancel = new Button { Content = "Cancel", MinWidth = 80 };
                    btnCancel.Click += (_, __) => { tcs.TrySetResult(defaultText); w.Close(); };
                    
                    var btnOk = new Button { Content = "OK", MinWidth = 80 };
                    btnOk.Click += (_, __) => { tcs.TrySetResult(input.Text ?? defaultText); w.Close(); };
                    
                    buttons.Children.Add(btnCancel);
                    buttons.Children.Add(btnOk);
                    
                    panel.Children.Add(desc);
                    panel.Children.Add(input);
                    panel.Children.Add(buttons);
                    w.Content = panel;
                    
                    if (isModal && MainWindow != null)
                    {
                        _ = w.ShowDialog(MainWindow);
                    }
                    else
                    {
                        w.Show();
                    }
                    
                    return await tcs.Task;
                }
            };
            
            if (dispatchMain)
            {
                return Dispatcher.UIThread.InvokeAsync(returnTaskFunc);
            }

            return returnTaskFunc();
        }
    }
}
