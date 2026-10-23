using System;
using System.IO;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Bitmap image leaf. <see cref="Source"/> is typed <see cref="ImageSource"/> so
    /// patched user IL (which calls <c>set_Source(System.Windows.Media.ImageSource)</c>)
    /// resolves cleanly. The XAML loader converts string attributes via
    /// <see cref="XamlTypeConverter"/>; code can pass <c>BitmapImage</c> /
    /// <c>WriteableBitmap</c> from <c>WPR.WindowsCompability</c> (both derive from
    /// <see cref="ImageSource"/>). <see cref="SilverlightImageDecoder"/> decodes and caches it.
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

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Image img)
            {
                img.InvalidateMeasure();
            }
        }

        /// <summary>
        /// The image's natural pixel size, from <see cref="SilverlightImageDecoder"/> - the same
        /// decoder <c>SoftwareVisualRasteriser</c> paints from, so measure and paint agree on the
        /// image, and it resolves <c>.g.resources</c> bundle entries as well as files.
        /// </summary>
        private bool TryGetNaturalSize(out double width, out double height)
        {
            width = height = 0;

            if (SilverlightImageDecoder.TryGetPixels(Source, out _, out int dw, out int dh)
                && dw > 0 && dh > 0)
            {
                width = dw;
                height = dh;
                return true;
            }

            return false;
        }

        internal override Size WprMeasure(Size availableSize)
        {
            if (!TryGetNaturalSize(out double naturalW, out double naturalH)) return Size.Empty;

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
