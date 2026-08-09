using System;
using System.IO;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Bitmap image leaf. <see cref="Source"/> is typed <see cref="ImageSource"/> so
    /// patched user IL (which calls <c>set_Source(System.Windows.Media.ImageSource)</c>)
    /// resolves cleanly. The XAML loader converts string attributes via
    /// <see cref="XamlTypeConverter"/>; code can pass <c>BitmapImage</c> /
    /// <c>WriteableBitmap</c> from <c>WPR.WindowsCompability</c> (both derive from
    /// <see cref="ImageSource"/>). Rendering caches the loaded bitmap on this instance.
    /// </summary>
    public class Image : FrameworkElement
    {
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(Image),
                new PropertyMetadata((object?)null, OnSourceChanged));

        public static readonly DependencyProperty StretchProperty =
            DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(Image),
                new PropertyMetadata(Stretch.Uniform));

        public ImageSource? Source
        {
            get => (ImageSource?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public Stretch Stretch
        {
            get => (Stretch)GetValue(StretchProperty)!;
            set => SetValue(StretchProperty, value);
        }

        // Silverlight raises ImageOpened once the source decodes, and ImageFailed
        // if decode fails. Our renderer doesn't surface these events yet — they're
        // declared so user code that subscribes (Minesweeper does, on MainPage)
        // can wire its handlers without a MissingMethodException at JIT time.
#pragma warning disable CS0067
        public event RoutedEventHandler? ImageOpened;
        public event EventHandler<ExceptionRoutedEventArgs>? ImageFailed;
#pragma warning restore CS0067

        // Lazily-resolved native bitmap; the renderer reads this.
        private AvBitmap? _avaloniaBitmap;

        internal AvBitmap? GetAvaloniaBitmap()
        {
            if (_avaloniaBitmap != null) return _avaloniaBitmap;

            ImageSource? src = Source;
            if (src == null) return null;

            if (src.NativeBitmap != null) return _avaloniaBitmap = src.NativeBitmap;

            string? path = src.Path;
            if (path != null)
            {
                _avaloniaBitmap = TryLoadBitmap(path);
                if (_avaloniaBitmap == null && !_logged404)
                {
                    _logged404 = true;
                    string installFolder = HostContext.CurrentInstallFolder ?? "<not set>";
                    Console.WriteLine(
                        $"[Image] failed to resolve '{path}'. Tried: '{path}', " +
                        $"'{System.IO.Path.Combine(installFolder, path.TrimStart('/','\\'))}'.");
                }
            }
            return _avaloniaBitmap;
        }

        // One-shot diagnostic guard: only log the first time a given Image's
        // Source fails to resolve. Avoids spamming the console with the same
        // miss every ~33ms when the animation timer is ticking.
        private bool _logged404;

        /// <summary>
        /// Resolves an image path (which may be absolute or app-relative) to an Avalonia
        /// Bitmap. Tries the path as-is first, then under the install folder
        /// (<see cref="HostContext.CurrentInstallFolder"/>) so XAML <c>Source="Resources/foo.png"</c>
        /// works regardless of the process's current working directory.
        /// </summary>
        internal static AvBitmap? TryLoadBitmap(string path)
        {
            try
            {
                if (File.Exists(path))
                    return new AvBitmap(path);
            }
            catch { }

            string? installFolder = HostContext.CurrentInstallFolder;
            if (!string.IsNullOrEmpty(installFolder))
            {
                try
                {
                    string combined = System.IO.Path.Combine(installFolder, path.TrimStart('/', '\\'));
                    if (File.Exists(combined))
                        return new AvBitmap(combined);
                }
                catch { }
            }
            return null;
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Image img)
            {
                img._avaloniaBitmap = null;
                img.InvalidateMeasure();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            AvBitmap? bmp = GetAvaloniaBitmap();
            if (bmp == null) return Size.Empty;

            double naturalW = bmp.Size.Width;
            double naturalH = bmp.Size.Height;

            // If parent provides finite bounds and stretch is set, the parent will give us
            // the slot — return natural for now and let arrange handle final fit.
            if (Stretch == Stretch.None) return new Size(naturalW, naturalH);

            // Honor finite available; for infinite, return natural.
            double w = double.IsInfinity(availableSize.Width) ? naturalW : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? naturalH : availableSize.Height;
            return new Size(Math.Min(w, naturalW), Math.Min(h, naturalH));
        }
    }
}
