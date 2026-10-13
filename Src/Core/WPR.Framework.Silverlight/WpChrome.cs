using System;
using System.Collections.Generic;
using System.Globalization;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// The WP7 control chrome both renderers draw — what kind of control an element is, where its
    /// parts go, and which theme colours it uses — with no drawing in it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> WPR applies no <c>ControlTemplate</c>s, so the look of a
    /// Panorama, Pivot, ToggleSwitch, progress bar or Button is synthesised by the renderer
    /// (<see cref="SoftwareVisualRasteriser"/>), which takes geometry and state from here and owns
    /// only how to put a rectangle or a string on its surface. (This was split out so the
    /// Avalonia renderer and the rasteriser could not drift; the Avalonia one was deleted on
    /// 2026-10-13.)</para>
    ///
    /// <para><b>Everything is in the element's own LAYOUT units, relative to its top-left.</b>
    /// The Avalonia renderer draws at scale 1; the rasteriser multiplies by whatever scale its
    /// ancestors accumulated. Text heights come in through a delegate because the two renderers
    /// measure with different fonts, and a layout that assumed one renderer's metrics would put
    /// the other's text in the wrong place.</para>
    ///
    /// <para><b>Some of this arranges children</b> (<see cref="ArrangePanorama"/>,
    /// <see cref="ArrangePanoramaItem"/>, <see cref="ArrangeButtonPresenter"/>): the toolkit's own
    /// Measure/Arrange expects a template that never runs, so without it the items stay parked at
    /// (0,0,0,0) and the hit tester can never find them. That was the Avalonia renderer's
    /// behaviour and is kept exactly; doing it from whichever renderer is active is what keeps
    /// touch working on the CPU path too.</para>
    /// </remarks>
    internal static class WpChrome
    {
        internal enum Kind
        {
            None,
            Panorama,
            Pivot,
            PanoramaItem,
            PivotItem,
            ProgressBar,
            ToggleSwitch,
        }

        // ---- WP7 theme values (ThemeResources.xaml; mirrored in PhoneTheme) ----

        public static readonly Color DefaultAccent = Color.FromRgb(0x1B, 0xA1, 0xE2);
        public static readonly Color DefaultForeground = Color.FromRgb(0xFF, 0xFF, 0xFF);
        public static readonly Color DefaultBackground = Color.FromRgb(0x00, 0x00, 0x00);
        public static readonly Color Subtle = Color.FromRgb(0x7F, 0x7F, 0x7F);

        /// <summary>The panorama fallback when neither a brush nor the Bing wallpaper is available.</summary>
        public static readonly Color PanoramaFallback = Color.FromRgb(0x1F, 0x1F, 0x1F);

        public const double PanoramaTitleFontSize = 72.0;        // PhoneFontSizeHuge
        public const double PanoramaHeaderFontSize = 56.0;
        public const double PanoramaItemGap = 24.0;
        public const double PanoramaTitleParallax = 0.5;
        public const double PivotTitleFontSize = 20.0;           // PhoneFontSizeNormal
        public const double PivotHeaderFontSize = 54.667;        // PhoneFontSizeExtraExtraLarge
        public const double PivotHeaderGap = 21.0;
        public const double ItemMargin = 12.0;                   // PhoneMargin

        public const double ButtonBorderThickness = 3.0;         // PhoneBorderThickness
        public const double ButtonPadding = 10.0;
        public const double ButtonMinHeight = 72.0;

        public const double ToggleHeaderFontSize = 22.0;
        public const double ToggleStateFontSize = 24.0;
        public const double ToggleTrackWidth = 95.0;
        public const double ToggleTrackHeight = 38.0;
        public const double ToggleThumbInset = 4.0;
        public const double ToggleTrackStrokeOff = 2.0;

        public const int ProgressDotCount = 5;
        public const double ProgressDotDiameter = 4.0;
        public const double ProgressDotSpacing = 14.0;
        public const double ProgressPeriodMs = 2200.0;
        public const double ProgressStaggerMs = 110.0;
        public const double ProgressBarThickness = 4.0;

        /// <summary>
        /// Frame interval the indeterminate progress animation is quantised to, so the change
        /// signature only moves when a dot could actually have moved.
        /// </summary>
        public const int ProgressFrameMs = 33;

        // ---- Classification ----

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Kind> KindByType = new();

        /// <summary>
        /// Which WP7 control <paramref name="element"/> is, matching its type AND its base types —
        /// games subclass the toolkit controls (Minesweeper's options page is built from
        /// <c>Minesweeper.ToggleSwitch2</c>), and a name test on the concrete type alone drew those
        /// as bare content in both renderers.
        /// </summary>
        public static Kind Classify(UIElement element)
            => KindByType.GetOrAdd(element.GetType(), static t =>
            {
                for (Type? type = t; type != null && type != typeof(object); type = type.BaseType)
                {
                    Kind k = ClassifyName(type.FullName ?? "");
                    if (k != Kind.None) return k;
                }
                return Kind.None;
            });

        private static Kind ClassifyName(string typeName)
        {
            switch (typeName)
            {
                case "Microsoft.Phone.Controls.Panorama": return Kind.Panorama;
                case "Microsoft.Phone.Controls.Pivot": return Kind.Pivot;
                case "Microsoft.Phone.Controls.PanoramaItem": return Kind.PanoramaItem;
                case "Microsoft.Phone.Controls.PivotItem": return Kind.PivotItem;
                // The third name is our own shim's: patched game IL binds
                // WPR.SilverlightCompability.ProgressBar, so matching only the WP7 names would stop
                // recognising it the moment the type existed.
                case "Microsoft.Phone.Controls.PerformanceProgressBar":
                case "System.Windows.Controls.ProgressBar":
                case "WPR.SilverlightCompability.ProgressBar":
                    return Kind.ProgressBar;
                case "Microsoft.Phone.Controls.ToggleSwitch": return Kind.ToggleSwitch;
                default: return Kind.None;
            }
        }

        // ---- Reflection helpers (the toolkit types are not referenced by this assembly) ----

        public static object? TryReadProperty(object obj, string name)
        {
            try
            {
                var p = obj.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                object? v = p?.GetValue(obj);
                // An unresolved Binding sitting in the property prints its type name, which is
                // worse than nothing.
                if (v is Binding) return null;
                return v;
            }
            catch { return null; }
        }

        public static double TryReadDouble(object element, string propName, double fallback)
        {
            object? v = TryReadProperty(element, propName);
            if (v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static string? ReadText(object element, string propName)
        {
            string? s = TryReadProperty(element, propName)?.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        // ---- Theme ----

        /// <summary>A theme brush from <c>Application.Current.Resources</c>, or null.</summary>
        public static Brush? ResourceBrush(string key)
        {
            var lookup = XamlReader.ApplicationResourceLookup;
            if (lookup == null) return null;
            try { return lookup(key) as Brush; }
            catch { return null; }
        }

        /// <summary>A theme colour by brush key, falling back to <paramref name="fallback"/>.</summary>
        public static Color ResourceColor(string brushKey, Color fallback)
            => ResourceBrush(brushKey) is SolidColorBrush s ? s.Color : fallback;

        public static Color Accent => ResourceColor("PhoneAccentBrush", DefaultAccent);

        public static Color Foreground => ResourceColor("PhoneForegroundBrush", DefaultForeground);

        public static Color Background => ResourceColor("PhoneBackgroundBrush", DefaultBackground);

        /// <summary>
        /// True if the brush would paint nothing: null, or a solid colour with zero alpha or zero
        /// opacity. A translucent brush counts as visible — the game composed it on purpose.
        /// </summary>
        public static bool IsEffectivelyTransparent(Brush? brush)
        {
            if (brush == null) return true;
            if (brush.Opacity <= 0) return true;
            if (brush is SolidColorBrush scb && scb.Color.A == 0) return true;
            return false;
        }

        // ---- Panorama / Pivot ----

        internal struct Slot
        {
            public UIElement Child;
            /// <summary>Parent-relative, layout units — also what was written to ArrangedRect.</summary>
            public Rect Rect;
        }

        internal sealed class PanoramaLayout
        {
            public string? Title;
            public double TitleFontSize;
            /// <summary>Title origin, relative to the control.</summary>
            public double TitleX, TitleY;
            public double ContentTop;
            /// <summary>Pivot only: header strip, current item first.</summary>
            public readonly List<(string Text, double X, bool Current)> Headers = new();
            public double HeaderY;
            public double HeaderFontSize;
            public readonly List<Slot> Slots = new();
        }

        /// <summary>
        /// Lays out a Panorama or Pivot: which item is current (from <see cref="PanoramaState"/>,
        /// which the pointer pipeline drives), the title and its parallax, and a slot per visible
        /// item. Children are measured and arranged into those slots.
        /// </summary>
        /// <param name="textHeight">The renderer's own line height for (text, font size).</param>
        public static PanoramaLayout? ArrangePanorama(
            UIElement element, double width, double height, bool isPivot,
            Func<string, double, double> textHeight)
        {
            if (element is not Panel panel) return null;

            var layout = new PanoramaLayout();
            int n = panel.Children.Count;

            layout.Title = ReadText(element, "Title");

            var state = PanoramaStateTable.GetOrCreate(element);
            if (n > 0)
            {
                if (state.CurrentIndex >= n) state.CurrentIndex = n - 1;
                if (state.CurrentIndex < 0) state.CurrentIndex = 0;

                // User code gates per-page actions on `panorama.SelectedItem == thisItem`, so keep
                // the toolkit's SelectedItem on the item actually displayed.
                if (element is DependencyObject panoDO)
                    PanoramaSelectedItemSync.Set(panoDO, panel.Children[state.CurrentIndex]);
            }

            double itemWidth = width;
            double gap = isPivot ? 0 : PanoramaItemGap;
            double itemsScroll = (state.CurrentIndex * (itemWidth + gap)) - state.DragOffset;

            double contentTop;
            if (isPivot)
            {
                // WP7 Pivot: a small title, then a strip of item headers with the current one
                // first in the foreground and the rest in the subtle colour. No parallax, no peek.
                layout.TitleFontSize = PivotTitleFontSize;
                layout.TitleX = ItemMargin;
                layout.TitleY = 17;
                double y = layout.TitleY;
                if (layout.Title != null) y += textHeight(layout.Title, PivotTitleFontSize);

                layout.HeaderY = y;
                layout.HeaderFontSize = PivotHeaderFontSize;
                double hx = ItemMargin - state.DragOffset;
                double headerH = 0;
                for (int k = 0; k < n; k++)
                {
                    int i = (state.CurrentIndex + k) % n;
                    if (panel.Children[i] is FrameworkElement fi && fi.Visibility == Visibility.Collapsed) continue;
                    string? header = ReadText(panel.Children[i], "Header");
                    if (header == null) continue;
                    layout.Headers.Add((header, hx, k == 0));
                    headerH = Math.Max(headerH, textHeight(header, PivotHeaderFontSize));
                    hx += MeasureWidthEstimate(header, PivotHeaderFontSize) + PivotHeaderGap;
                    if (hx > width) break;
                }
                contentTop = y + headerH;
            }
            else
            {
                layout.TitleFontSize = PanoramaTitleFontSize;
                layout.TitleX = ItemMargin - (itemsScroll * PanoramaTitleParallax);
                layout.TitleY = 0;
                contentTop = layout.Title != null
                    ? textHeight(layout.Title, PanoramaTitleFontSize) + 12
                    : 0;
            }

            layout.ContentTop = contentTop;
            double contentHeight = Math.Max(0, height - contentTop);

            for (int i = 0; i < n; i++)
            {
                UIElement child = panel.Children[i];
                if (child is FrameworkElement fec && fec.Visibility == Visibility.Collapsed) continue;

                // A pivot shows only its current item (sliding with the drag); a panorama lays
                // every item out on one strip and lets the neighbours peek in.
                if (isPivot && i != state.CurrentIndex) continue;

                double relX = isPivot ? -state.DragOffset : (i * (itemWidth + gap)) - itemsScroll;
                if (relX + itemWidth < -100) continue;
                if (relX > width + 100) continue;

                var rect = new Rect(relX, contentTop, itemWidth, contentHeight);
                child.Measure(new Size(itemWidth, contentHeight));
                child.Arrange(rect);
                layout.Slots.Add(new Slot { Child = child, Rect = rect });
            }

            return layout;
        }

        /// <summary>
        /// Width estimate for laying out a pivot header strip before the renderer measures it.
        /// Uses the CPU rasteriser's font when one exists (both renderers then space headers
        /// identically), and a Segoe-like average advance otherwise.
        /// </summary>
        private static double MeasureWidthEstimate(string text, double fontSize)
        {
            if (TextRasteriser.IsAvailable)
            {
                TextRasteriser.Measure(text, fontSize, out double w, out _);
                if (w > 0) return w;
            }
            return text.Length * fontSize * 0.5;
        }

        internal sealed class PanoramaItemLayout
        {
            public string? Header;
            public double HeaderFontSize;
            public double HeaderX, HeaderY;
            /// <summary>The viewport the content shows through, relative to the item.</summary>
            public Rect Viewport;
            public UIElement? Presenter;
            public double ScrollY;
        }

        /// <summary>
        /// Lays out a PanoramaItem or PivotItem: its header (a pivot's headers are drawn by the
        /// pivot instead), and its content arranged at full natural height inside a viewport,
        /// scrolled by the vertical offset the pointer pipeline keeps in
        /// <see cref="PanoramaItemScrollTable"/> — real WP7 wraps the content in a ScrollViewer
        /// through the toolkit template.
        /// </summary>
        public static PanoramaItemLayout ArrangePanoramaItem(
            UIElement element, double width, double height, bool isPivotItem,
            Func<string, double, double> textHeight)
        {
            var layout = new PanoramaItemLayout();
            double headerHeight = 0;

            if (!isPivotItem)
            {
                layout.Header = ReadText(element, "Header");
                layout.HeaderFontSize = PanoramaHeaderFontSize;
                layout.HeaderX = ItemMargin;
                layout.HeaderY = 0;
                if (layout.Header != null) headerHeight = textHeight(layout.Header, PanoramaHeaderFontSize) + 8;
            }

            layout.Viewport = new Rect(
                ItemMargin, headerHeight,
                Math.Max(0, width - (ItemMargin * 2)), Math.Max(0, height - headerHeight));

            if (element is ContentControl cc && cc.Presenter != null)
            {
                UIElement presenter = cc.Presenter;
                layout.Presenter = presenter;

                // Unbounded height so the natural size — which can exceed the viewport — is known.
                presenter.Measure(new Size(layout.Viewport.Width, double.PositiveInfinity));
                double contentH = Math.Max(layout.Viewport.Height, presenter.DesiredSize.Height);
                presenter.Arrange(new Rect(layout.Viewport.X, layout.Viewport.Y, layout.Viewport.Width, contentH));

                var scroll = PanoramaItemScrollTable.GetOrCreate(element);
                double maxScroll = Math.Max(0, contentH - layout.Viewport.Height);
                if (scroll.ScrollY < 0) scroll.ScrollY = 0;
                if (scroll.ScrollY > maxScroll) scroll.ScrollY = maxScroll;
                layout.ScrollY = scroll.ScrollY;
            }

            return layout;
        }

        // ---- Progress bar ----

        public static bool IsIndeterminate(UIElement element)
            => TryReadProperty(element, "IsIndeterminate") is bool b && b;

        /// <summary>The filled fraction of a determinate bar, 0..1.</summary>
        public static double ProgressFraction(UIElement element)
        {
            double value = TryReadDouble(element, "Value", 0);
            double maximum = TryReadDouble(element, "Maximum", 100);
            double minimum = TryReadDouble(element, "Minimum", 0);
            double range = Math.Max(1e-6, maximum - minimum);
            return Math.Max(0, Math.Min(1, (value - minimum) / range));
        }

        /// <summary>
        /// The colour of a progress bar's dots or fill: its own Foreground if set, otherwise the
        /// accent — which is the toolkit default for both bars.
        /// </summary>
        public static Color ProgressColor(UIElement element)
        {
            if (element is FrameworkElement fe && fe.Foreground is SolidColorBrush s && s.Color.A != 0)
                return s.Color;
            return Accent;
        }

        /// <summary>
        /// The x positions, relative to the bar, of the indeterminate dots visible at time
        /// <paramref name="nowMs"/>. Five dots chase left to right on an ease-in-out cubic, each a
        /// little behind the previous — the WP7 toolkit's look.
        /// </summary>
        public static List<double> ProgressDots(double width, long nowMs)
        {
            var xs = new List<double>(ProgressDotCount);
            double trackLeft = -ProgressDotDiameter;
            double trackRight = width + (ProgressDotCount * ProgressDotSpacing);
            double trackWidth = trackRight - trackLeft;

            for (int i = 0; i < ProgressDotCount; i++)
            {
                double t = (((nowMs - (i * ProgressStaggerMs)) % ProgressPeriodMs) + ProgressPeriodMs)
                           % ProgressPeriodMs / ProgressPeriodMs;
                double eased = t < 0.5
                    ? 4 * t * t * t
                    : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);
                double x = trackLeft + (eased * trackWidth);
                if (x < -ProgressDotDiameter || x > width) continue;
                xs.Add(x);
            }

            return xs;
        }

        /// <summary>
        /// The animation clock the dots are drawn at, quantised to <see cref="ProgressFrameMs"/>.
        /// One value for both drawing and the change signature, so a repaint always shows a move.
        /// </summary>
        public static long ProgressClock() => Environment.TickCount64 / ProgressFrameMs * ProgressFrameMs;

        // ---- ToggleSwitch ----

        internal sealed class ToggleLayout
        {
            public string? Header;
            public double HeaderY;
            public Rect Track;
            public Rect Thumb;
            public bool IsOn;
            public string StateText = "Off";
            public double StateX;
            public double StateY;
        }

        public static bool IsToggleOn(UIElement element)
            => TryReadProperty(element, "IsChecked") is bool b && b;

        /// <summary>
        /// Header above a chunky track with a thumb, and the state word ("On"/"Off") beside it.
        /// </summary>
        public static ToggleLayout LayoutToggleSwitch(UIElement element, Func<string, double, double> textHeight)
        {
            var l = new ToggleLayout { IsOn = IsToggleOn(element) };
            l.StateText = l.IsOn ? "On" : "Off";

            double y = 4;
            l.Header = ReadText(element, "Header");
            l.HeaderY = y;
            if (l.Header != null) y += textHeight(l.Header, ToggleHeaderFontSize) + 6;

            l.Track = new Rect(0, y, ToggleTrackWidth, ToggleTrackHeight);
            double thumbWidth = (ToggleTrackWidth - (ToggleThumbInset * 3)) / 2;
            double thumbX = l.IsOn ? ToggleTrackWidth - ToggleThumbInset - thumbWidth : ToggleThumbInset;
            l.Thumb = new Rect(thumbX, y + ToggleThumbInset, thumbWidth, ToggleTrackHeight - (ToggleThumbInset * 2));

            l.StateX = ToggleTrackWidth + 12;
            l.StateY = y + ((ToggleTrackHeight - textHeight(l.StateText, ToggleStateFontSize)) / 2);
            return l;
        }

        // ---- Button ----

        /// <summary>
        /// Centres a Button's presenter inside its padded interior and arranges it there. Returns
        /// the presenter's rect relative to the button, or null when there is no presenter.
        /// </summary>
        public static Rect? ArrangeButtonPresenter(ContentControl button, double width, double height)
        {
            UIElement? p = button.Presenter;
            if (p == null) return null;

            double innerX = ButtonPadding;
            double innerY = ButtonPadding;
            double innerW = Math.Max(0, width - (ButtonPadding * 2));
            double innerH = Math.Max(0, Math.Max(height, ButtonMinHeight) - (ButtonPadding * 2));

            p.Measure(new Size(innerW, double.PositiveInfinity));
            Size ds = p.DesiredSize;
            double w = Math.Min(ds.Width, innerW);
            double h = Math.Min(ds.Height, innerH);
            var rect = new Rect(innerX + ((innerW - w) / 2), innerY + ((innerH - h) / 2), w, h);
            p.Arrange(rect);
            return rect;
        }

        /// <summary>
        /// True when the control has no template of its own, i.e. it would look like the WP7
        /// default and the synthesised chrome is right. A templated control is drawn as bare
        /// content instead — WPR applies no templates, and a default-button frame around an image
        /// button is further from the game's look than nothing.
        /// </summary>
        public static bool UsesDefaultChrome(Control control) => control.Template == null;

        /// <summary>
        /// A button's border width: its own <c>BorderThickness</c> when it sets one, otherwise the
        /// theme's <c>PhoneBorderThickness</c> (3), which is what the WP7 button style binds.
        /// </summary>
        public static double ButtonBorderWidth(Control button)
        {
            Thickness t = button.BorderThickness;
            double w = Math.Max(Math.Max(t.Left, t.Right), Math.Max(t.Top, t.Bottom));
            return w > 0 ? w : ButtonBorderThickness;
        }

        /// <summary>
        /// Whether the button is drawn in its pressed state: WP7 fills a pressed button with the
        /// foreground brush and draws its content in the background colour.
        /// </summary>
        public static bool IsPressed(UIElement element)
            => element is ButtonBase b && b.IsPressed;
    }
}
