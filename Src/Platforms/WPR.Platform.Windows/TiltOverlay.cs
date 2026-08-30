using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Devices.Sensors;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// Lightweight HUD that sits over a running Silverlight game and shows the current
    /// synthesized tilt reading. Renders a crosshair (the green dot tracks X/Y), the
    /// numeric reading, and a tiny banner naming the bound keys. Hit-test-invisible
    /// so it never steals touches from the game underneath.
    /// </summary>
    /// <remarks>
    /// Lives entirely in the host process — independent of the user game's rendering. We
    /// poll <see cref="KeyboardAccelerometerHost.CurrentReading"/> from a DispatcherTimer
    /// rather than subscribing to <c>ReadingTick</c> because that fires on the simulator
    /// timer thread and we'd have to marshal each invalidation anyway.
    /// </remarks>
    public sealed class TiltOverlay : Control
    {
        private DispatcherTimer? _timer;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // Keep the simulator running for the overlay even when the user game hasn't
            // started/subscribed an Accelerometer — the user wants to see the indicator
            // respond regardless.
            KeyboardAccelerometerHost.Acquire();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // 30 fps is enough for a HUD
            _timer.Tick += (_, _) => InvalidateVisual();
            _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _timer?.Stop();
            _timer = null;
            KeyboardAccelerometerHost.Release();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            // Screen-relative reading so the dot tracks the user's key presses directly,
            // even when the game's running landscape and the device-frame reading is rotated.
            var reading = KeyboardAccelerometerHost.CurrentScreenReading.Acceleration;

            // 110px crosshair anchored top-right with a 12px margin from the edge.
            const double pad = 12;
            const double size = 110;
            double cx = w - pad - size / 2;
            double cy = pad + size / 2;
            var ringBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
            var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1);
            var crosshair = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1);
            context.DrawEllipse(ringBrush, ringPen, new Point(cx, cy), size / 2, size / 2);
            context.DrawLine(crosshair, new Point(cx - size / 2 + 4, cy), new Point(cx + size / 2 - 4, cy));
            context.DrawLine(crosshair, new Point(cx, cy - size / 2 + 4), new Point(cx, cy + size / 2 - 4));

            // Tilt dot: map ±1g → ±(size/2 - 8). Y-up in user space → screen-y flips.
            double radius = size / 2 - 8;
            double dotX = cx + Math.Clamp(reading.X, -1f, 1f) * radius;
            double dotY = cy - Math.Clamp(reading.Y, -1f, 1f) * radius;
            var dotBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x40, 0xD0, 0x70));
            context.DrawEllipse(dotBrush, null, new Point(dotX, dotY), 6, 6);

            // Numeric reading + key hint, justified under the dial.
            string text =
                $"X {reading.X,5:F2}\n" +
                $"Y {reading.Y,5:F2}\n" +
                $"Z {reading.Z,5:F2}";
            var typeface = new Typeface("Consolas");
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                11,
                new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
            context.DrawText(ft, new Point(cx - size / 2 + 6, cy + size / 2 + 4));
        }
    }
}
