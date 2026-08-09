using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvBrush = Avalonia.Media.IBrush;
using AvFormattedText = Avalonia.Media.FormattedText;
using AvTypeface = Avalonia.Media.Typeface;
using AvFlowDirection = Avalonia.Media.FlowDirection;
using AvRoundedRect = global::Avalonia.RoundedRect;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Renders a Silverlight visual tree into an Avalonia DrawingContext.
    /// </summary>
    internal static class SilverlightRenderer
    {

        public static void Render(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {
            if (element is FrameworkElement fe && fe.Visibility == Visibility.Collapsed)
                return;

            // Honor Opacity. 0 → invisible (skip); 0<x<1 → push an opacity layer
            // so children blend, restore on the way out.
            double opacity = element.Opacity;
            if (opacity <= 0) return;
            global::Avalonia.Media.DrawingContext.PushedState? opacityLayer = null;
            if (opacity < 1) opacityLayer = ctx.PushOpacity(opacity);

            try { RenderCore(ctx, element, bounds); }
            finally { opacityLayer?.Dispose(); }
        }

        /// <summary>
        /// Render an entire page: the page tree first, then a deferred pass that
        /// overlays any open <see cref="Popup"/>s at full page bounds. Popups in
        /// Silverlight float above the visual tree rather than participating in
        /// their parent's layout, so they need this z-order escape hatch — without
        /// it, a Popup inside a Grid row whose <c>Height="Auto"</c> gets a 0-px
        /// arrangement slot and never appears (Minesweeper's splash overlay is
        /// the canonical case).
        /// </summary>
        public static void RenderPage(DrawingContext ctx, UIElement page, global::Avalonia.Rect pageBounds)
        {
            // Real WP7's PhoneApplicationPage default Background is
            // PhoneBackgroundBrush (black) via the system theme style. User XAML
            // routinely leaves the page Background unset and/or sets the root
            // Grid to Transparent (Minesweeper's AchievementsPage is the
            // canonical case). Without a hard backdrop the previous frame's
            // pixels bleed through transparent regions — most visibly, the
            // panorama's Bing wallpaper showing under a transparent
            // AchievementsPage. Paint the WP7 default here so every page starts
            // from a clean black ground.
            ctx.FillRectangle(
                new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.Black),
                pageBounds);

            Render(ctx, page, pageBounds);

            // Deferred popup pass — paint each open popup's Child at the full
            // page bounds.
            foreach (Popup pop in CollectOpenPopups(page))
            {
                if (pop.Child is UIElement child)
                {
                    var with = new global::Avalonia.Rect(
                        pageBounds.X + pop.HorizontalOffset,
                        pageBounds.Y + pop.VerticalOffset,
                        pageBounds.Width, pageBounds.Height);
                    Render(ctx, child, with);
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<Popup> CollectOpenPopups(UIElement root)
        {
            // BFS the logical tree. Stick to the same hosts FrameworkElement uses
            // for Loaded-event tree walks (Panel.Children / ContentControl.Content /
            // Border.Child / Popup.Child). Anything else (custom controls without
            // exposed children) is invisible to us anyway.
            // We use IsEffectivelyOpen (not IsOpen) so the minimum-display-time
            // floor in Popup is honoured — a popup whose IsOpen just flipped to
            // false but hasn't been visible long enough still paints.
            var q = new System.Collections.Generic.Queue<UIElement>();
            q.Enqueue(root);
            while (q.Count > 0)
            {
                UIElement el = q.Dequeue();
                if (el is Popup p && p.IsEffectivelyOpen) yield return p;
                switch (el)
                {
                    case Panel panel:
                        foreach (UIElement c in panel.Children) q.Enqueue(c);
                        break;
                    case ContentControl cc:
                        if (cc.Content is UIElement ccChild) q.Enqueue(ccChild);
                        if (cc.Presenter != null && !ReferenceEquals(cc.Presenter, cc.Content))
                            q.Enqueue(cc.Presenter);
                        break;
                    case Border b:
                        if (b.Child != null) q.Enqueue(b.Child);
                        break;
                    case Popup pop:
                        if (pop.Child != null) q.Enqueue(pop.Child);
                        break;
                }
            }
        }

        private static void RenderCore(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {

            // Order matters: Border / Popup / Shape match BEFORE the generic
            // ContentControl / Panel cases because Border isn't a ContentControl
            // (its Child is a separate slot), Popup isn't a Panel, and Shape /
            // Rectangle don't host children at all.
            //
            // The WP Toolkit container types (Panorama, Pivot, LongListSelector,
            // PerformanceProgressBar) override Measure/Arrange expecting a
            // ControlTemplate we don't apply, so their children stay parked at
            // (0,0,0,0) and visually collapse. Intercept those before the generic
            // Panel/ContentControl cases and lay them out ourselves.
            string typeName = element.GetType().FullName ?? "";
            if (typeName == "Microsoft.Phone.Controls.Panorama" ||
                typeName == "Microsoft.Phone.Controls.Pivot")
            {
                DrawPanoramaLike(ctx, element, bounds);
                return;
            }
            if (typeName == "Microsoft.Phone.Controls.PanoramaItem" ||
                typeName == "Microsoft.Phone.Controls.PivotItem")
            {
                DrawPanoramaItemLike(ctx, element, bounds);
                return;
            }
            if (typeName == "Microsoft.Phone.Controls.PerformanceProgressBar" ||
                typeName == "System.Windows.Controls.ProgressBar")
            {
                DrawProgressBar(ctx, element, bounds);
                return;
            }
            // The toolkit's ToggleSwitch is a heavily-templated Control that
            // shows a Header above a sliding On/Off track. We don't apply its
            // template, so without intercepting it here we'd see only the
            // toolkit's default Content ("Off") as bare text. Render it
            // ourselves: header + chunky WP7-style track + thumb + state text.
            if (typeName == "Microsoft.Phone.Controls.ToggleSwitch")
            {
                DrawToggleSwitch(ctx, element, bounds);
                return;
            }

            switch (element)
            {
                case Rectangle rect:
                    DrawRectangle(ctx, rect, bounds);
                    break;

                case Shape shape:
                    PaintBrush(ctx, shape.Fill, bounds);
                    break;

                case Border border:
                    DrawBorder(ctx, border, bounds);
                    break;

                case Popup _:
                    // Popups float above the visual tree — handled in a deferred
                    // pass at page-level bounds (see RenderPage / CollectOpenPopups).
                    // Painting inline here would render at the parent's tiny
                    // layout slot (typically 0 px tall) and either be invisible
                    // or clipped against the parent.
                    break;

                // Button comes BEFORE the generic ContentControl case so its
                // WP7 chrome (white border, padded interior, centered text)
                // is applied instead of falling through to bare Content paint.
                case Button btn:
                    DrawButton(ctx, btn, bounds);
                    break;

                // ContentControl / PhoneApplicationPage all share: paint Background, then render Presenter.
                case ContentControl cc:
                    PaintBrush(ctx, cc.Background, bounds);
                    if (cc.Presenter != null)
                    {
                        var childBounds = OffsetTo(cc.Presenter.ArrangedRect, bounds);
                        Render(ctx, cc.Presenter, childBounds);
                    }
                    break;

                // Hybrid Silverlight + WinRT D3D background. Real WP routes this surface to
                // the bound IDrawingSurfaceBackgroundContentProvider (native code in the app's
                // WinRT component). On WPR we expose a managed-renderer plug-in point; if no
                // renderer is registered we paint a status placeholder so the user can see the
                // app launched and that the D3D side is unsupported for this app.
                case DrawingSurfaceBackgroundGrid dsbg:
                    bool rendered = dsbg.AttachedRenderer != null && dsbg.AttachedRenderer.Render(ctx, bounds);
                    if (!rendered)
                        DrawD3DPlaceholder(ctx, bounds, dsbg.AttachedContentProvider != null);
                    foreach (UIElement child in dsbg.Children)
                    {
                        var childBounds = OffsetTo(child.ArrangedRect, bounds);
                        Render(ctx, child, childBounds);
                    }
                    break;

                case Panel panel:
                    PaintBrush(ctx, panel.Background, bounds);
                    foreach (UIElement child in panel.Children)
                    {
                        var childBounds = OffsetTo(child.ArrangedRect, bounds);
                        Render(ctx, child, childBounds);
                    }
                    break;

                case TextBlock tb:
                    DrawText(ctx, tb, bounds);
                    break;

                case Image img:
                    DrawImage(ctx, img, bounds);
                    break;
            }
        }

        /// <summary>
        /// Renders a Panorama or Pivot. Real WP7 shows ONE item fitting full-width
        /// at a time with a peek of the next item (12-24px), big title overhead,
        /// parallax — and swipes horizontally to change pages. We approximate:
        ///  - The Panorama's <see cref="PanoramaState.CurrentIndex"/> picks which
        ///    item is current. Pointer pipeline mutates it on flick.
        ///  - Title is rendered once at the top, scrolled horizontally by index
        ///    so it gets a small parallax-style offset.
        ///  - Current item gets bounds.Width; neighbours peek 24px in/out.
        ///  - <see cref="PanoramaState.DragOffset"/> shifts the whole strip while
        ///    the user is mid-drag so the swipe feels live.
        /// </summary>
        private static void DrawPanoramaLike(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {
            // Background priority:
            //   1. Panel's own Background brush IF effectively visible (alpha > 0).
            //      WP toolkit Panorama style sets Background="Transparent" so the
            //      underlying Bing app shows through; treat that the same as null
            //      and fall through to the Bing path. A non-zero-alpha brush
            //      (game's own colour or image) wins outright.
            //   2. Bing daily wallpaper (real WP7 panorama games show this through
            //      the underlying Bing search app), if we've fetched it.
            //   3. Subtle dark grey so the panorama visually separates from the
            //      (typically black) PhoneApplicationPage backdrop.
            if (element is Panel basePanel)
            {
                AvBrush? bg = ConvertBrush(basePanel.Background);
                bool bgIsVisible = bg != null && !IsEffectivelyTransparent(basePanel.Background);
                if (bgIsVisible)
                {
                    ctx.FillRectangle(bg!, bounds);
                }
                else
                {
                    var bingBmp = BingWallpaper.Bitmap;
                    if (bingBmp != null)
                    {
                        // UniformToFill: cover the bounds, cropping overflow on the
                        // long axis. Matches how the real WP shell paints the
                        // wallpaper edge-to-edge across the phone screen.
                        var src = new global::Avalonia.Rect(0, 0, bingBmp.Size.Width, bingBmp.Size.Height);
                        var dst = UniformFit(src.Width, src.Height, bounds, fillFully: true);
                        ctx.DrawImage(bingBmp, src, dst);
                    }
                    else
                    {
                        ctx.FillRectangle(
                            new global::Avalonia.Media.SolidColorBrush(
                                global::Avalonia.Media.Color.FromRgb(0x1F, 0x1F, 0x1F)),
                            bounds);
                    }
                }
            }

            // Title (string|object) — via reflection so SLC doesn't need to reference Microsoft.Phone.
            object? titleObj = TryReadProperty(element, "Title");
            string? title = titleObj?.ToString();

            if (element is not Panel panel) return;
            int n = panel.Children.Count;
            if (n == 0) return;

            // Resolve per-Panorama state.
            var state = PanoramaStateTable.GetOrCreate(element);
            if (state.CurrentIndex >= n) state.CurrentIndex = n - 1;
            if (state.CurrentIndex < 0)  state.CurrentIndex = 0;

            // Keep the toolkit's Panorama.SelectedItem in sync with the index we
            // actually display. User code (Minesweeper.MainPage.m and friends)
            // gates per-page actions on `panorama.SelectedItem == thisItem`.
            if (element is DependencyObject panoDO)
                PanoramaSelectedItemSync.Set(panoDO, panel.Children[state.CurrentIndex]);

            const double itemGap = 24;
            double itemWidth = bounds.Width;
            // Parallax: title slides left at half the items' speed.
            double dragOffset = state.DragOffset;
            double itemsScroll = state.CurrentIndex * (itemWidth + itemGap) - dragOffset;
            double titleScroll = itemsScroll * 0.5;

            double titleHeight = 0;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var titleBrush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White);
                var typeface = new AvTypeface("Segoe WP Light", default, global::Avalonia.Media.FontWeight.Light);
                var ft = new AvFormattedText(title!, CultureInfo.CurrentUICulture,
                    AvFlowDirection.LeftToRight, typeface, 72.0, titleBrush);
                ctx.DrawText(ft, new global::Avalonia.Point(bounds.X + 12 - titleScroll, bounds.Y));
                titleHeight = ft.Height + 12;
            }

            var contentBounds = new global::Avalonia.Rect(
                bounds.X, bounds.Y + titleHeight,
                bounds.Width, Math.Max(0, bounds.Height - titleHeight));

            // Draw all items at their horizontal slot — most clip off-screen; the
            // current one occupies (0..itemWidth). Children of off-screen items
            // can be visible at the edges (24px peek) which is fine.
            //
            // CRITICAL: ArrangedRect must be RELATIVE to the parent (the Panorama),
            // not absolute. The HitTester and the renderer's OffsetTo cascade both
            // expect relative coords. We pass absolute coords to Render via the
            // bounds parameter (it always carries the absolute draw-target rect),
            // but store relative coords on ArrangedRect for layout/hit-test correctness.
            double parentY = bounds.Y;     // panorama bounds.Y (absolute)
            double parentX = bounds.X;
            for (int i = 0; i < n; i++)
            {
                UIElement child = panel.Children[i];
                if (child is FrameworkElement fec && fec.Visibility == Visibility.Collapsed) continue;

                // Absolute x for the item this frame:
                double slotAbsX = parentX + i * (itemWidth + itemGap) - itemsScroll;
                double slotAbsY = contentBounds.Y;
                // Skip items entirely off-screen.
                if (slotAbsX + itemWidth < bounds.X - 100) continue;
                if (slotAbsX > bounds.Right + 100) continue;

                // Relative offset within the Panorama's frame:
                double relX = slotAbsX - parentX;
                double relY = slotAbsY - parentY;

                child.Measure(new Size(itemWidth, contentBounds.Height));
                child.Arrange(new Rect(relX, relY, itemWidth, contentBounds.Height));
                Render(ctx, child, new global::Avalonia.Rect(slotAbsX, slotAbsY, itemWidth, contentBounds.Height));
            }
        }

        /// <summary>
        /// Paints a WP toolkit-style indeterminate progress bar: 5 small dots
        /// sliding left → right across the slot in a continuous loop, each one
        /// offset slightly behind the previous to give the classic "chase"
        /// effect. The Determinate case (IsIndeterminate=false with a numeric
        /// Value) isn't reached on the Minesweeper splash but is supported as a
        /// solid fill proportional to <c>Value/Maximum</c>.
        ///
        /// Animation phase comes from <see cref="Environment.TickCount"/> — the
        /// FrameView's DispatcherTimer drives ~30Hz InvalidateVisual calls while
        /// a popup is effectively open, so this method is re-entered with a new
        /// phase each tick and the dots actually move.
        /// </summary>
        private static void DrawProgressBar(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {
            bool isIndeterminate = TryReadProperty(element, "IsIndeterminate") is bool ind && ind;

            // Use the WP7 accent brush for dots — falls back to a neutral white
            // if the theme dictionary doesn't have one (shouldn't happen, but
            // be defensive).
            AvBrush dotBrush =
                ResolveResourceBrush("PhoneAccentBrush")
                ?? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White);

            if (!isIndeterminate)
            {
                // Determinate mode: solid bar proportional to Value/Maximum.
                double value = TryReadDouble(element, "Value", 0);
                double maximum = TryReadDouble(element, "Maximum", 100);
                double minimum = TryReadDouble(element, "Minimum", 0);
                double range = Math.Max(1e-6, maximum - minimum);
                double frac = Math.Max(0, Math.Min(1, (value - minimum) / range));
                if (frac > 0)
                {
                    var fillBounds = new global::Avalonia.Rect(
                        bounds.X, bounds.Y + bounds.Height / 2 - 2,
                        bounds.Width * frac, 4);
                    ctx.FillRectangle(dotBrush, fillBounds);
                }
                return;
            }

            // Indeterminate: animate 5 dots looping left → right.
            const int dotCount = 5;
            const double dotDiameter = 4.0;
            const double dotSpacing = 14.0;
            const double periodMs = 2200.0;
            const double staggerMs = 110.0;

            double centerY = bounds.Y + bounds.Height / 2;
            double trackLeft = bounds.X - dotDiameter; // start fully off-screen left
            double trackRight = bounds.Right + dotCount * dotSpacing;
            double trackWidth = trackRight - trackLeft;
            long now = Environment.TickCount;

            for (int i = 0; i < dotCount; i++)
            {
                double t = ((now - i * staggerMs) % periodMs + periodMs) % periodMs / periodMs;
                // Ease-in-out cubic so the dot pauses slightly at each end —
                // approximates the WP7 toolkit's curve.
                double eased = t < 0.5
                    ? 4 * t * t * t
                    : 1 - Math.Pow(-2 * t + 2, 3) / 2;
                double x = trackLeft + eased * trackWidth;
                // Skip dots that have wrapped past the visible bounds.
                if (x < bounds.X - dotDiameter || x > bounds.Right) continue;
                var rect = new global::Avalonia.Rect(x, centerY - dotDiameter / 2,
                                                      dotDiameter, dotDiameter);
                ctx.DrawRectangle(dotBrush, null, rect);
            }
        }

        /// <summary>
        /// Paint a WP7-style button: a 3-DIP solid foreground outline around
        /// the content, optional Background fill, and the Content (text or
        /// nested element) centered inside the padded interior. The toolkit's
        /// real default Style sets BorderBrush=PhoneForegroundBrush (white),
        /// BorderThickness=3, Padding=10,3,10,5, Background=Transparent —
        /// without applying that Style ourselves the button collapses to bare
        /// text, which is what the user sees in HelpOptionsPage today.
        /// </summary>
        private static void DrawButton(DrawingContext ctx, Button btn, global::Avalonia.Rect bounds)
        {
            // Padded inner box where the Content paints; keep the border outside.
            const double pad = 10;
            const double minHeight = 72; // WP7 minimum tap-target for buttons.
            // Background — Transparent by default (no paint), but if user set
            // an explicit colour honour it inside the border.
            AvBrush? bgBrush = ConvertBrush(btn.Background);
            if (bgBrush != null && !IsEffectivelyTransparent(btn.Background))
                ctx.FillRectangle(bgBrush, bounds);

            // Border. ContentControl-based Button has no BorderBrush DP in our
            // shim (Control adds it, but Button branches off via ContentControl
            // for compat with both the System.Windows.Controls.Button and the
            // toolkit's PhoneApplicationFrame template). Hard-code WP7's
            // default: PhoneForegroundBrush, 3 DIP.
            AvBrush borderBrush = ResolveResourceBrush("PhoneForegroundBrush")
                ?? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White);
            var pen = new global::Avalonia.Media.Pen(borderBrush, 3.0);
            ctx.DrawRectangle(null, pen, bounds);

            // Render Content. The Button shim wraps a non-UIElement Content in
            // a TextBlock, so we walk the Presenter. ContentControl's measure
            // didn't account for our injected padding, so re-arrange the
            // presenter inside the padded interior centered both axes.
            UIElement? p = btn.Presenter;
            if (p == null) return;

            var innerBounds = new global::Avalonia.Rect(
                bounds.X + pad,
                bounds.Y + pad,
                Math.Max(0, bounds.Width - pad * 2),
                Math.Max(0, Math.Max(bounds.Height, minHeight) - pad * 2));

            // Center the presenter's natural size inside innerBounds. Measure
            // it against the available width to honour wrapping if the user
            // configured a TextWrapping="Wrap" on a TextBlock content.
            p.Measure(new Size(innerBounds.Width, double.PositiveInfinity));
            var ds = p.DesiredSize;
            double w = Math.Min(ds.Width, innerBounds.Width);
            double h = Math.Min(ds.Height, innerBounds.Height);
            double cx = innerBounds.X + (innerBounds.Width - w) / 2;
            double cy = innerBounds.Y + (innerBounds.Height - h) / 2;
            var presBounds = new global::Avalonia.Rect(cx, cy, w, h);
            // Sync ArrangedRect so child Render reads correct coords.
            p.Arrange(new Rect(presBounds.X - bounds.X, presBounds.Y - bounds.Y, w, h));
            Render(ctx, p, presBounds);
        }

        /// <summary>
        /// Paint a WP7-style ToggleSwitch: header text above a chunky On/Off
        /// pill, with the state word ("On"/"Off") to the right. The toolkit
        /// type is heavily templated; we never apply the template, so without
        /// this path the user just sees the toolkit's default Content text
        /// (typically "Off") with no chrome and no header.
        ///
        /// Reflection-driven so we don't have to take a reference on
        /// Microsoft.Phone.Controls.Toolkit.dll — we read Header, IsChecked,
        /// and SwitchForeground from whatever object the renderer is handed.
        /// </summary>
        private static void DrawToggleSwitch(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {
            object? headerObj = TryReadProperty(element, "Header");
            object? checkedObj = TryReadProperty(element, "IsChecked");
            bool isOn = checkedObj is bool b ? b : false;

            // Accent for the "on" state — falls back to white if the theme
            // dictionary isn't seeded (e.g., very early in launch).
            AvBrush accent = ResolveResourceBrush("PhoneAccentBrush")
                ?? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x1B, 0xA1, 0xE2));
            // SwitchForeground (the thumb) — defaults to white in WP7 default theme.
            AvBrush thumbBrush = ResolveResourceBrush("PhoneForegroundBrush")
                ?? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White);
            AvBrush textBrush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White);

            // 1) Header text at top-left, semi-light 22pt.
            double y = bounds.Y + 4;
            string? header = headerObj?.ToString();
            if (!string.IsNullOrWhiteSpace(header))
            {
                var headerFont = new AvTypeface("Segoe WP", default, global::Avalonia.Media.FontWeight.Normal);
                var headerText = new AvFormattedText(header!, CultureInfo.CurrentUICulture,
                    AvFlowDirection.LeftToRight, headerFont, 22.0, textBrush);
                ctx.DrawText(headerText, new global::Avalonia.Point(bounds.X, y));
                y += headerText.Height + 6;
            }

            // 2) Track + thumb.
            const double trackWidth = 95;
            const double trackHeight = 38;
            const double thumbInset = 4;
            double trackX = bounds.X;
            double trackY = y;
            var trackRect = new global::Avalonia.Rect(trackX, trackY, trackWidth, trackHeight);

            // When On, fill the track with accent; when Off, just outline.
            if (isOn)
            {
                ctx.FillRectangle(accent, trackRect);
            }
            else
            {
                var trackPen = new global::Avalonia.Media.Pen(textBrush, 2);
                ctx.DrawRectangle(null, trackPen, trackRect);
            }

            // Thumb: small rectangle inside, right side when on, left when off.
            double thumbWidth = (trackWidth - thumbInset * 3) / 2; // half-track minus padding
            double thumbX = isOn
                ? trackX + trackWidth - thumbInset - thumbWidth
                : trackX + thumbInset;
            var thumbRect = new global::Avalonia.Rect(
                thumbX,
                trackY + thumbInset,
                thumbWidth,
                trackHeight - thumbInset * 2);
            ctx.FillRectangle(thumbBrush, thumbRect);

            // 3) State word ("On" / "Off") to the right of the track.
            string stateWord = isOn ? "On" : "Off";
            var stateFont = new AvTypeface("Segoe WP", default, global::Avalonia.Media.FontWeight.Normal);
            var stateText = new AvFormattedText(stateWord, CultureInfo.CurrentUICulture,
                AvFlowDirection.LeftToRight, stateFont, 24.0, textBrush);
            double stateX = trackX + trackWidth + 12;
            double stateY = trackY + (trackHeight - stateText.Height) / 2;
            ctx.DrawText(stateText, new global::Avalonia.Point(stateX, stateY));
        }

        /// <summary>Best-effort read of a numeric DP value, returning <paramref name="fallback"/>
        /// on any conversion failure. Used by <see cref="DrawProgressBar"/> for
        /// Value/Minimum/Maximum on the rare determinate-mode bar.</summary>
        private static double TryReadDouble(object element, string propName, double fallback)
        {
            object? v = TryReadProperty(element, propName);
            if (v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        /// <summary>Resolve a theme brush by key from <c>Application.Current.Resources</c>
        /// via the SLC ⇄ WC bridge. Returns the Avalonia <see cref="AvBrush"/> form
        /// (converted by <see cref="ConvertBrush"/>) or null if unset/unsupported.</summary>
        private static AvBrush? ResolveResourceBrush(string key)
        {
            var lookup = XamlReader.ApplicationResourceLookup;
            if (lookup == null) return null;
            object? v = lookup(key);
            return v is Brush b ? ConvertBrush(b) : null;
        }

        /// <summary>
        /// Renders a PanoramaItem or PivotItem: a Header (smaller white) above the
        /// Content. PanoramaItem post-patch is a ContentControl, so Content is its
        /// single child.
        /// </summary>
        private static void DrawPanoramaItemLike(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {
            // Header.
            object? headerObj = TryReadProperty(element, "Header");
            string? header = headerObj?.ToString();
            double headerHeight = 0;
            if (!string.IsNullOrWhiteSpace(header))
            {
                var brush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White);
                var typeface = new AvTypeface("Segoe WP Light", default, global::Avalonia.Media.FontWeight.Light);
                var ft = new AvFormattedText(header!, CultureInfo.CurrentUICulture,
                    AvFlowDirection.LeftToRight, typeface, 56.0, brush);
                ctx.DrawText(ft, new global::Avalonia.Point(bounds.X + 12, bounds.Y));
                headerHeight = ft.Height + 8;
            }

            var contentBounds = new global::Avalonia.Rect(
                bounds.X + 12, bounds.Y + headerHeight,
                Math.Max(0, bounds.Width - 24), Math.Max(0, bounds.Height - headerHeight));

            // Render Content. PanoramaItem post-patch derives from ContentControl,
            // so we read the Presenter (or Content if presenter not built).
            // Same relative-vs-absolute split as DrawPanoramaLike: ArrangedRect
            // is the presenter's relative position WITHIN the PanoramaItem; the
            // Render bounds carry the absolute draw rect.
            //
            // Real WP7 wraps panorama-item content in a ScrollViewer via the
            // toolkit template — we don't apply templates so the content
            // visibly overflows. Apply our own vertical scroll offset (set by
            // the pointer pipeline on vertical drag) and clip to the item's
            // content bounds so overflow doesn't bleed past.
            if (element is ContentControl cc)
            {
                UIElement? presenter = cc.Presenter;
                if (presenter != null)
                {
                    double relX = contentBounds.X - bounds.X;
                    double relY = contentBounds.Y - bounds.Y;

                    // Measure with unbounded height so the content's natural
                    // size — which can exceed contentBounds.Height — is known.
                    // Without this, the presenter measures clamped to the
                    // viewport and we'd think we have nothing to scroll.
                    presenter.Measure(new Size(contentBounds.Width, double.PositiveInfinity));
                    double contentH = Math.Max(contentBounds.Height, presenter.DesiredSize.Height);
                    presenter.Arrange(new Rect(relX, relY, contentBounds.Width, contentH));

                    var scroll = PanoramaItemScrollTable.GetOrCreate(element);
                    double maxScroll = Math.Max(0, contentH - contentBounds.Height);
                    if (scroll.ScrollY < 0) scroll.ScrollY = 0;
                    if (scroll.ScrollY > maxScroll) scroll.ScrollY = maxScroll;

                    // Clip rendering to the visible content bounds so off-screen
                    // children don't paint over neighbouring panorama items or
                    // the page background. Then translate up by ScrollY so the
                    // viewport shows further-down content.
                    using var clip = ctx.PushClip(contentBounds);
                    using var transform = ctx.PushTransform(
                        global::Avalonia.Matrix.CreateTranslation(0, -scroll.ScrollY));
                    Render(ctx, presenter, contentBounds);
                }
            }
        }

        private static object? TryReadProperty(object obj, string name)
        {
            try
            {
                var p = obj.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                object? v = p?.GetValue(obj);
                // Filter out Binding objects sitting in the property because we
                // couldn't resolve the binding's Source/DataContext at parse time.
                // .ToString()'ing them prints "WPR.SilverlightCompability.Binding"
                // which is worse than nothing.
                if (v is Binding) return null;
                return v;
            }
            catch { return null; }
        }

        private static void DrawRectangle(DrawingContext ctx, Rectangle rect, global::Avalonia.Rect bounds)
        {
            AvBrush? fill = ConvertBrush(rect.Fill);
            AvBrush? stroke = ConvertBrush(rect.Stroke);

            if (rect.RadiusX > 0 || rect.RadiusY > 0)
            {
                var rounded = new AvRoundedRect(bounds, rect.RadiusX, rect.RadiusY);
                if (fill != null)
                    ctx.DrawRectangle(fill, null, rounded);
                if (stroke != null && rect.StrokeThickness > 0)
                    ctx.DrawRectangle(null,
                        new global::Avalonia.Media.Pen(stroke, rect.StrokeThickness),
                        rounded);
            }
            else
            {
                if (fill != null) ctx.FillRectangle(fill, bounds);
                if (stroke != null && rect.StrokeThickness > 0)
                {
                    var pen = new global::Avalonia.Media.Pen(stroke, rect.StrokeThickness);
                    ctx.DrawRectangle(null, pen, bounds);
                }
            }
        }

        private static void DrawBorder(DrawingContext ctx, Border border, global::Avalonia.Rect bounds)
        {
            AvBrush? bg = ConvertBrush(border.Background);
            AvBrush? bb = ConvertBrush(border.BorderBrush);

            // Paint background first.
            if (bg != null)
            {
                if (border.CornerRadius.TopLeft > 0 || border.CornerRadius.TopRight > 0
                    || border.CornerRadius.BottomLeft > 0 || border.CornerRadius.BottomRight > 0)
                {
                    var rounded = new AvRoundedRect(bounds,
                        border.CornerRadius.TopLeft, border.CornerRadius.TopRight,
                        border.CornerRadius.BottomRight, border.CornerRadius.BottomLeft);
                    ctx.DrawRectangle(bg, null, rounded);
                }
                else
                {
                    ctx.FillRectangle(bg, bounds);
                }
            }

            // Then the outline. Use uniform thickness from Left for simplicity.
            if (bb != null)
            {
                double t = border.BorderThickness.Left;
                if (t <= 0) t = Math.Max(border.BorderThickness.Top, Math.Max(border.BorderThickness.Right, border.BorderThickness.Bottom));
                if (t > 0)
                {
                    var pen = new global::Avalonia.Media.Pen(bb, t);
                    ctx.DrawRectangle(null, pen, bounds);
                }
            }

            // Then the Child, if any.
            if (border.Child is UIElement child)
            {
                var childBounds = OffsetTo(child.ArrangedRect, bounds);
                Render(ctx, child, childBounds);
            }
        }

        private static void DrawImage(DrawingContext ctx, Image img, global::Avalonia.Rect bounds)
        {
            var bmp = img.GetAvaloniaBitmap();
            if (bmp == null) return;

            var sourceRect = new global::Avalonia.Rect(0, 0, bmp.Size.Width, bmp.Size.Height);

            global::Avalonia.Rect destRect = img.Stretch switch
            {
                Stretch.None => new global::Avalonia.Rect(bounds.X, bounds.Y, bmp.Size.Width, bmp.Size.Height),
                Stretch.Fill => bounds,
                Stretch.Uniform => UniformFit(sourceRect.Width, sourceRect.Height, bounds, fillFully: false),
                Stretch.UniformToFill => UniformFit(sourceRect.Width, sourceRect.Height, bounds, fillFully: true),
                _ => bounds,
            };

            ctx.DrawImage(bmp, sourceRect, destRect);
        }

        private static global::Avalonia.Rect UniformFit(double srcW, double srcH, global::Avalonia.Rect bounds, bool fillFully)
        {
            if (srcW <= 0 || srcH <= 0) return bounds;
            double rx = bounds.Width / srcW;
            double ry = bounds.Height / srcH;
            double scale = fillFully ? System.Math.Max(rx, ry) : System.Math.Min(rx, ry);
            double w = srcW * scale;
            double h = srcH * scale;
            double x = bounds.X + (bounds.Width - w) / 2;
            double y = bounds.Y + (bounds.Height - h) / 2;
            return new global::Avalonia.Rect(x, y, w, h);
        }

        /// <summary>
        /// Paint the default placeholder for an un-implemented WinRT D3D background:
        /// a dark surface with a centered "Direct3D content [no managed renderer]" notice.
        /// </summary>
        private static void DrawD3DPlaceholder(DrawingContext ctx, global::Avalonia.Rect bounds, bool providerWasSet)
        {
            // Solid dark fill — matches WP's default page background tone.
            ctx.FillRectangle(
                new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x18, 0x18, 0x1F)),
                bounds);

            // Diagonal stripe pattern as a "no signal" visual.
            var stripeBrush = new global::Avalonia.Media.SolidColorBrush(
                global::Avalonia.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            const double stripe = 24;
            for (double offset = -bounds.Height; offset < bounds.Width; offset += stripe * 2)
            {
                var poly = new global::Avalonia.Media.PolylineGeometry(
                    new[]
                    {
                        new global::Avalonia.Point(bounds.X + offset, bounds.Y),
                        new global::Avalonia.Point(bounds.X + offset + bounds.Height, bounds.Y + bounds.Height),
                        new global::Avalonia.Point(bounds.X + offset + bounds.Height + stripe, bounds.Y + bounds.Height),
                        new global::Avalonia.Point(bounds.X + offset + stripe, bounds.Y),
                    },
                    isFilled: true);
                ctx.DrawGeometry(stripeBrush, null, poly);
            }

            // Centered status text. Two lines: short title + sub-line that explains.
            string title = providerWasSet
                ? "Direct3D Background"
                : "(no Direct3D content)";
            string subtitle = providerWasSet
                ? "No managed renderer registered for this app."
                : "App did not bind a content provider.";

            var typeface = new AvTypeface("Segoe UI");
            var titleText = new AvFormattedText(title, CultureInfo.CurrentUICulture,
                AvFlowDirection.LeftToRight, typeface, 22.0,
                new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White));
            var subText = new AvFormattedText(subtitle, CultureInfo.CurrentUICulture,
                AvFlowDirection.LeftToRight, typeface, 14.0,
                new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0xAA, 0xAA, 0xAA)));

            double titleX = bounds.X + (bounds.Width - titleText.Width) / 2;
            double titleY = bounds.Y + (bounds.Height - titleText.Height - subText.Height - 8) / 2;
            ctx.DrawText(titleText, new global::Avalonia.Point(titleX, titleY));

            double subX = bounds.X + (bounds.Width - subText.Width) / 2;
            double subY = titleY + titleText.Height + 8;
            ctx.DrawText(subText, new global::Avalonia.Point(subX, subY));
        }

        private static void PaintBrush(DrawingContext ctx, Brush? brush, global::Avalonia.Rect bounds)
        {
            AvBrush? av = ConvertBrush(brush);
            if (av != null) ctx.FillRectangle(av, bounds);
        }

        private static void DrawText(DrawingContext ctx, TextBlock tb, global::Avalonia.Rect bounds)
        {
            string text = tb.Text ?? string.Empty;
            if (text.Length == 0) return;

            // Foreground cascade: TextBlock's own Foreground beats ancestors. If unset,
            // walk up to find the nearest FrameworkElement with a Foreground set. Final
            // fallback is white (WP7 default theme).
            Brush? effectiveFg = tb.Foreground;
            for (var fe = tb.Parent as FrameworkElement; fe != null && effectiveFg == null; fe = fe.Parent as FrameworkElement)
                effectiveFg = fe.Foreground;
            AvBrush foreground = ConvertBrush(effectiveFg)
                                 ?? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Colors.White);

            var typeface = new AvTypeface(tb.FontFamily ?? "Segoe UI");

            var ft = new AvFormattedText(
                text,
                CultureInfo.CurrentUICulture,
                AvFlowDirection.LeftToRight,
                typeface,
                tb.FontSize,
                foreground);

            if (tb.TextWrapping == TextWrapping.Wrap)
                ft.MaxTextWidth = bounds.Width;

            ft.TextAlignment = ConvertAlignment(tb.TextAlignment);

            ctx.DrawText(ft, new global::Avalonia.Point(bounds.X, bounds.Y));
        }

        /// <summary>
        /// True if the brush would paint nothing visible: null, or a fully-transparent
        /// SolidColorBrush (alpha=0 — what <c>Brushes.Transparent</c> / the
        /// <c>"Transparent"</c> XAML literal resolves to). Used by the panorama
        /// background pipeline to know whether to fall through to the Bing
        /// wallpaper fallback. We deliberately don't treat low-but-nonzero alpha
        /// as transparent — a translucent overlay is something the user actively
        /// composed, and skipping it would hide their intent.
        /// </summary>
        private static bool IsEffectivelyTransparent(Brush? brush)
        {
            if (brush == null) return true;
            if (brush is SolidColorBrush scb)
            {
                // Color.A is the per-pixel alpha; Opacity is a separate multiplier.
                // Either being zero kills visibility entirely.
                if (scb.Color.A == 0) return true;
                if (scb.Opacity <= 0) return true;
            }
            return false;
        }

        internal static AvBrush? ConvertBrush(Brush? brush)
        {
            if (brush == null) return null;
            if (brush is SolidColorBrush solid)
            {
                Color c = solid.Color;
                var avColor = global::Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
                return new global::Avalonia.Media.SolidColorBrush(avColor, solid.Opacity);
            }
            if (brush is ImageBrush ib && ib.ImageSource != null)
            {
                // Resolve via the same path Image uses — if the source has a cached native
                // bitmap or a file path we can decode, wrap it in an Avalonia ImageBrush.
                var bmp = TryResolveBitmap(ib.ImageSource);
                if (bmp != null)
                {
                    return new global::Avalonia.Media.ImageBrush(bmp)
                    {
                        Stretch = ConvertStretch(ib.Stretch),
                        Opacity = ib.Opacity,
                    };
                }
                return null;
            }
            return null;
        }

        private static global::Avalonia.Media.Imaging.Bitmap? TryResolveBitmap(ImageSource src)
        {
            if (src.NativeBitmap != null) return src.NativeBitmap;
            string? path = src.Path;
            if (path == null) return null;
            // Reuse Image's resolver so "Resources/foo.png" lookups go through the
            // same install-folder fallback that <Image Source="…"/> elements use.
            return Image.TryLoadBitmap(path);
        }

        private static global::Avalonia.Media.Stretch ConvertStretch(Stretch s) => s switch
        {
            Stretch.None => global::Avalonia.Media.Stretch.None,
            Stretch.Fill => global::Avalonia.Media.Stretch.Fill,
            Stretch.Uniform => global::Avalonia.Media.Stretch.Uniform,
            Stretch.UniformToFill => global::Avalonia.Media.Stretch.UniformToFill,
            _ => global::Avalonia.Media.Stretch.None,
        };

        private static global::Avalonia.Media.TextAlignment ConvertAlignment(TextAlignment a) => a switch
        {
            TextAlignment.Center => global::Avalonia.Media.TextAlignment.Center,
            TextAlignment.Right => global::Avalonia.Media.TextAlignment.Right,
            TextAlignment.Justify => global::Avalonia.Media.TextAlignment.Justify,
            _ => global::Avalonia.Media.TextAlignment.Left,
        };

        private static global::Avalonia.Rect OffsetTo(Rect childArranged, global::Avalonia.Rect parentBounds)
        {
            return new global::Avalonia.Rect(
                parentBounds.X + childArranged.X,
                parentBounds.Y + childArranged.Y,
                childArranged.Width,
                childArranged.Height);
        }
    }
}
