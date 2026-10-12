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
            switch (WpChrome.Classify(element))
            {
                case WpChrome.Kind.Panorama:
                    DrawPanoramaLike(ctx, element, bounds, isPivot: false);
                    return;
                case WpChrome.Kind.Pivot:
                    DrawPanoramaLike(ctx, element, bounds, isPivot: true);
                    return;
                case WpChrome.Kind.PanoramaItem:
                    DrawPanoramaItemLike(ctx, element, bounds, isPivotItem: false);
                    return;
                case WpChrome.Kind.PivotItem:
                    DrawPanoramaItemLike(ctx, element, bounds, isPivotItem: true);
                    return;
                case WpChrome.Kind.ProgressBar:
                    DrawProgressBar(ctx, element, bounds);
                    return;
                // The toolkit's ToggleSwitch is a heavily-templated Control that shows a Header
                // above a sliding On/Off track. We don't apply its template, so without this we'd
                // see only its default Content ("Off") as bare text.
                case WpChrome.Kind.ToggleSwitch:
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
                case Button btn when WpChrome.UsesDefaultChrome(btn):
                    DrawButton(ctx, btn, bounds);
                    break;

                // Before the general ContentControl case it derives from. Since ScrollViewer began
                // arranging its content at full extent (so it has something to scroll through),
                // this renderer has to show a window onto it like the CPU rasteriser does —
                // otherwise a long list draws its whole extent straight over everything below it.
                case ScrollViewer scroller:
                    PaintBrush(ctx, scroller.Background, bounds);
                    if (scroller.Presenter != null)
                    {
                        using (ctx.PushClip(bounds))
                        {
                            var scrolled = new global::Avalonia.Rect(
                                bounds.X - scroller.HorizontalOffset,
                                bounds.Y - scroller.VerticalOffset,
                                bounds.Width,
                                bounds.Height);
                            Render(ctx, scroller.Presenter, OffsetTo(scroller.Presenter.ArrangedRect, scrolled));
                        }
                    }
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
        /// Renders a Panorama or Pivot. Layout, state and parallax come from
        /// <see cref="WpChrome.ArrangePanorama"/>, which the CPU rasteriser uses too:
        ///  - <see cref="PanoramaState.CurrentIndex"/> picks the current item; the pointer
        ///    pipeline mutates it on flick, and <see cref="PanoramaState.DragOffset"/> shifts the
        ///    strip mid-drag.
        ///  - A panorama's title scrolls at half the items' speed (parallax) and its neighbours
        ///    peek in; a pivot has a small title and a strip of item headers instead.
        /// </summary>
        private static void DrawPanoramaLike(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds, bool isPivot)
        {
            // Background priority: the control's own brush if it paints anything; for a
            // panorama, the Bing daily wallpaper real WP7 panorama apps showed through; then a
            // dark grey that separates it from the black page.
            if (element is FrameworkElement basePanel && !WpChrome.IsEffectivelyTransparent(basePanel.Background))
            {
                AvBrush? bg = ConvertBrush(basePanel.Background);
                if (bg != null) ctx.FillRectangle(bg, bounds);
            }
            else if (!isPivot)
            {
                var bingBmp = BingWallpaper.Bitmap;
                if (bingBmp != null)
                {
                    var src = new global::Avalonia.Rect(0, 0, bingBmp.Size.Width, bingBmp.Size.Height);
                    var dst = UniformFit(src.Width, src.Height, bounds, fillFully: true);
                    using (ctx.PushClip(bounds))
                        ctx.DrawImage(bingBmp, src, dst);
                }
                else
                {
                    ctx.FillRectangle(ToAvBrush(WpChrome.PanoramaFallback), bounds);
                }
            }

            WpChrome.PanoramaLayout? layout = WpChrome.ArrangePanorama(
                element, bounds.Width, bounds.Height, isPivot, AvTextHeight);
            if (layout == null) return;

            var fg = ToAvBrush(WpChrome.Foreground);
            using (ctx.PushClip(bounds))
            {
                if (layout.Title != null)
                {
                    DrawChromeText(ctx, layout.Title, layout.TitleFontSize, fg,
                        bounds.X + layout.TitleX, bounds.Y + layout.TitleY, light: !isPivot);
                }

                if (layout.Headers.Count > 0)
                {
                    var subtle = ToAvBrush(WpChrome.Subtle);
                    foreach ((string text, double x, bool current) in layout.Headers)
                    {
                        DrawChromeText(ctx, text, layout.HeaderFontSize, current ? fg : subtle,
                            bounds.X + x, bounds.Y + layout.HeaderY, light: true);
                    }
                }

                // ArrangedRect is RELATIVE to the parent (the hit tester and OffsetTo expect
                // that); Render gets the absolute draw rect.
                foreach (WpChrome.Slot slot in layout.Slots)
                    Render(ctx, slot.Child, OffsetTo(slot.Rect, bounds));
            }
        }

        /// <summary>Paints the WP toolkit progress bar: the five chasing dots when
        /// indeterminate, a 4px accent bar proportional to Value otherwise. Geometry and the
        /// animation clock come from <see cref="WpChrome"/>; the Avalonia view's timer
        /// re-enters this at ~30Hz while it is visible so the dots move.</summary>
        private static void DrawProgressBar(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {
            AvBrush dotBrush = ToAvBrush(WpChrome.ProgressColor(element));
            double centerY = bounds.Y + bounds.Height / 2;

            if (!WpChrome.IsIndeterminate(element))
            {
                double frac = WpChrome.ProgressFraction(element);
                if (frac > 0)
                {
                    ctx.FillRectangle(dotBrush, new global::Avalonia.Rect(
                        bounds.X, centerY - WpChrome.ProgressBarThickness / 2,
                        bounds.Width * frac, WpChrome.ProgressBarThickness));
                }
                return;
            }

            double d = WpChrome.ProgressDotDiameter;
            foreach (double x in WpChrome.ProgressDots(bounds.Width, WpChrome.ProgressClock()))
                ctx.FillRectangle(dotBrush, new global::Avalonia.Rect(bounds.X + x, centerY - d / 2, d, d));
        }

        /// <summary>
        /// Paint a WP7 button: optional Background, a foreground outline (3px, or the button's
        /// own BorderThickness/BorderBrush), the Content centred in a padded interior, and when
        /// pressed a foreground fill with the content drawn in the background colour.
        /// </summary>
        private static void DrawButton(DrawingContext ctx, Button btn, global::Avalonia.Rect bounds)
        {
            bool pressed = WpChrome.IsPressed(btn);
            if (pressed)
            {
                ctx.FillRectangle(ToAvBrush(WpChrome.Foreground), bounds);
            }
            else if (!WpChrome.IsEffectivelyTransparent(btn.Background))
            {
                AvBrush? bgBrush = ConvertBrush(btn.Background);
                if (bgBrush != null) ctx.FillRectangle(bgBrush, bounds);
            }

            AvBrush borderBrush = ConvertBrush(btn.BorderBrush) ?? ToAvBrush(WpChrome.Foreground);
            double t = WpChrome.ButtonBorderWidth(btn);
            // Inset by half the pen so the stroke lies inside the slot, as the CPU path's does.
            var strokeRect = new global::Avalonia.Rect(
                bounds.X + t / 2, bounds.Y + t / 2,
                Math.Max(0, bounds.Width - t), Math.Max(0, bounds.Height - t));
            ctx.DrawRectangle(null, new global::Avalonia.Media.Pen(borderBrush, t), strokeRect);

            Rect? presRel = WpChrome.ArrangeButtonPresenter(btn, bounds.Width, bounds.Height);
            if (presRel == null || btn.Presenter == null) return;

            Color? saved = _pressedContentForeground;
            _pressedContentForeground = pressed ? WpChrome.Background : saved;
            try { Render(ctx, btn.Presenter, OffsetTo(presRel.Value, bounds)); }
            finally { _pressedContentForeground = saved; }
        }

        [ThreadStatic]
        private static Color? _pressedContentForeground;

        /// <summary>
        /// Paint a WP7 ToggleSwitch: header text above a chunky On/Off track, thumb, and the
        /// state word to the right. Geometry from <see cref="WpChrome.LayoutToggleSwitch"/>.
        /// </summary>
        private static void DrawToggleSwitch(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds)
        {
            WpChrome.ToggleLayout l = WpChrome.LayoutToggleSwitch(element, AvTextHeight);
            AvBrush fg = ToAvBrush(WpChrome.Foreground);

            if (l.Header != null)
                DrawChromeText(ctx, l.Header, WpChrome.ToggleHeaderFontSize, fg, bounds.X, bounds.Y + l.HeaderY, light: false);

            var track = OffsetTo(l.Track, bounds);
            if (l.IsOn)
            {
                ctx.FillRectangle(ToAvBrush(WpChrome.Accent), track);
            }
            else
            {
                double s = WpChrome.ToggleTrackStrokeOff;
                var inset = new global::Avalonia.Rect(track.X + s / 2, track.Y + s / 2, track.Width - s, track.Height - s);
                ctx.DrawRectangle(null, new global::Avalonia.Media.Pen(fg, s), inset);
            }

            ctx.FillRectangle(fg, OffsetTo(l.Thumb, bounds));
            DrawChromeText(ctx, l.StateText, WpChrome.ToggleStateFontSize, fg,
                bounds.X + l.StateX, bounds.Y + l.StateY, light: false);
        }

        /// <summary>
        /// Renders a PanoramaItem or PivotItem: a header (a pivot draws its items' headers
        /// itself) above the content, which is shown through a scrolled, clipped viewport —
        /// real WP7 wraps it in a ScrollViewer through the toolkit template.
        /// </summary>
        private static void DrawPanoramaItemLike(DrawingContext ctx, UIElement element, global::Avalonia.Rect bounds, bool isPivotItem)
        {
            WpChrome.PanoramaItemLayout l = WpChrome.ArrangePanoramaItem(
                element, bounds.Width, bounds.Height, isPivotItem, AvTextHeight);

            if (l.Header != null)
            {
                DrawChromeText(ctx, l.Header, l.HeaderFontSize, ToAvBrush(WpChrome.Foreground),
                    bounds.X + l.HeaderX, bounds.Y + l.HeaderY, light: true);
            }

            if (l.Presenter == null) return;

            var viewport = OffsetTo(l.Viewport, bounds);
            using var clip = ctx.PushClip(viewport);
            var scrolled = new global::Avalonia.Rect(bounds.X, bounds.Y - l.ScrollY, bounds.Width, bounds.Height);
            Render(ctx, l.Presenter, OffsetTo(l.Presenter.ArrangedRect, scrolled));
        }

        private static readonly Func<string, double, double> AvTextHeight = (text, size) =>
            new AvFormattedText(text, CultureInfo.CurrentUICulture, AvFlowDirection.LeftToRight,
                new AvTypeface("Segoe WP Light", default, global::Avalonia.Media.FontWeight.Light),
                size, null).Height;

        private static void DrawChromeText(DrawingContext ctx, string text, double size, AvBrush brush, double x, double y, bool light)
        {
            var typeface = light
                ? new AvTypeface("Segoe WP Light", default, global::Avalonia.Media.FontWeight.Light)
                : new AvTypeface("Segoe WP", default, global::Avalonia.Media.FontWeight.Normal);
            var ft = new AvFormattedText(text, CultureInfo.CurrentUICulture, AvFlowDirection.LeftToRight, typeface, size, brush);
            ctx.DrawText(ft, new global::Avalonia.Point(x, y));
        }

        private static AvBrush ToAvBrush(Color c)
            => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B));

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
            if (effectiveFg == null && _pressedContentForeground is Color pressedFg)
                effectiveFg = new SolidColorBrush(pressedFg);
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
            // Gradients, so this renderer agrees with the CPU rasteriser rather than falling
            // through to "unsupported" and painting nothing where the other one paints a fade.
            // Avalonia's own brushes take the same relative 0..1 geometry Silverlight uses.
            if (brush is GradientBrush gradient)
            {
                var stops = new global::Avalonia.Media.GradientStops();
                foreach (GradientStop stop in gradient.GradientStops)
                {
                    if (stop == null) continue;
                    Color sc = stop.Color;
                    stops.Add(new global::Avalonia.Media.GradientStop(
                        global::Avalonia.Media.Color.FromArgb(sc.A, sc.R, sc.G, sc.B),
                        stop.Offset));
                }

                if (stops.Count == 0) return null;

                var spread = gradient.SpreadMethod switch
                {
                    GradientSpreadMethod.Reflect => global::Avalonia.Media.GradientSpreadMethod.Reflect,
                    GradientSpreadMethod.Repeat => global::Avalonia.Media.GradientSpreadMethod.Repeat,
                    _ => global::Avalonia.Media.GradientSpreadMethod.Pad,
                };

                if (gradient is LinearGradientBrush linear)
                {
                    return new global::Avalonia.Media.LinearGradientBrush
                    {
                        StartPoint = new global::Avalonia.RelativePoint(
                            linear.StartPoint.X, linear.StartPoint.Y,
                            global::Avalonia.RelativeUnit.Relative),
                        EndPoint = new global::Avalonia.RelativePoint(
                            linear.EndPoint.X, linear.EndPoint.Y,
                            global::Avalonia.RelativeUnit.Relative),
                        SpreadMethod = spread,
                        GradientStops = stops,
                        Opacity = gradient.Opacity,
                    };
                }

                if (gradient is RadialGradientBrush radial)
                {
                    return new global::Avalonia.Media.RadialGradientBrush
                    {
                        Center = new global::Avalonia.RelativePoint(
                            radial.Center.X, radial.Center.Y, global::Avalonia.RelativeUnit.Relative),
                        GradientOrigin = new global::Avalonia.RelativePoint(
                            radial.GradientOrigin.X, radial.GradientOrigin.Y,
                            global::Avalonia.RelativeUnit.Relative),
                        SpreadMethod = spread,
                        GradientStops = stops,
                        Opacity = gradient.Opacity,
                    };
                }

                return null;
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
