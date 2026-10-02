using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// The "stop and send report" button of a logging run: a small always-on-top strip at the top
    /// of the screen the game is on, the desktop twin of the button <c>GameActivity</c> draws over a
    /// game on Android. The game has its own SDL (or Silverlight) window, so this cannot sit inside
    /// it; a separate topmost window is the one thing that stays reachable over it.
    /// </summary>
    /// <remarks>
    /// Two clicks, like the phone: the first arms it for three seconds. It does not end the game
    /// itself; <see cref="StopRequested"/> is raised and the host does that, because only the host
    /// knows which of the three launchers is running.
    /// </remarks>
    internal sealed class LoggingRunWindow : Window
    {
        private readonly Button _button;
        private readonly DispatcherTimer _disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly string _label;
        private bool _armed;

        public event Action? StopRequested;

        public LoggingRunWindow(string title)
        {
            Title = "WPR logging run";
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 20));
            WindowStartupLocation = WindowStartupLocation.Manual;

            _label = "● Logging " + title + "  ·  Stop & send report";
            _button = new Button
            {
                Content = _label,
                Padding = new Thickness(14, 8),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _button.Click += (_, _) => OnClick();
            Content = _button;

            _disarm.Tick += (_, _) => Disarm();
            Opened += (_, _) => PlaceAtTopCentre();
        }

        private void OnClick()
        {
            if (!_armed)
            {
                _armed = true;
                _button.Content = "Click again to close the game and send its log";
                Background = new SolidColorBrush(Color.FromArgb(240, 180, 30, 30));
                _disarm.Start();
                return;
            }

            _disarm.Stop();
            _armed = false;
            _button.IsEnabled = false;
            _button.Content = "Stopping the game…";
            StopRequested?.Invoke();
        }

        private void Disarm()
        {
            _disarm.Stop();
            if (!_armed) return;
            _armed = false;
            _button.Content = _label;
            Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 20));
        }

        /// <summary>Say something while the game is still going (it did not close in time).</summary>
        public void ShowNote(string text)
        {
            _button.IsEnabled = false;
            _button.Content = text;
        }

        private void PlaceAtTopCentre()
        {
            Avalonia.Platform.Screen? screen = Screens.Primary;
            if (screen == null) return;
            PixelRect area = screen.WorkingArea;
            double scale = screen.Scaling;
            int width = (int)(Bounds.Width * scale);
            Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (int)(8 * scale));
        }
    }
}
