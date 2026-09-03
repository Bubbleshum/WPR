using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Media;
using WPR.SilverlightCompability;
using AvBrush = Avalonia.Media.IBrush;
using AvSize = Avalonia.Size;
using Border = WPR.SilverlightCompability.Border;
using Brush = WPR.SilverlightCompability.Brush;
using Button = WPR.SilverlightCompability.Button;
using ContentControl = WPR.SilverlightCompability.ContentControl;
using Panel = WPR.SilverlightCompability.Panel;
// This host adapter moved into the Microsoft.Phone assembly alongside PhoneApplicationFrame/Page.
// It pulls in both Avalonia and WPR.SilverlightCompability, which share several simple type
// names. Alias the shared names to the SL shims so the bare usages below keep resolving to them;
// the Avalonia equivalents are always reached through explicit global::Avalonia.* qualification
// (or the AvSize/AvBrush aliases).
using Point = WPR.SilverlightCompability.Point;
using Rect = WPR.SilverlightCompability.Rect;
using Size = WPR.SilverlightCompability.Size;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Avalonia adapter that hosts a <see cref="PhoneApplicationFrame"/>. The Silverlight
    /// page tree is measured and arranged by Avalonia's layout pass via overrides; rendering
    /// recurses through the Silverlight tree using <see cref="SilverlightRenderer"/>.
    /// </summary>
    public class PhoneApplicationFrameView : Avalonia.Controls.Control
    {
        private readonly PhoneApplicationFrame _frame;
        private PhoneApplicationPage? _currentPage;

        public PhoneApplicationFrameView(PhoneApplicationFrame frame)
        {
            _frame = frame ?? throw new ArgumentNullException(nameof(frame));
            _frame.Navigated += OnFrameNavigated;
            _currentPage = frame.Content;
            HookPage(_currentPage);

            // The initial page is assigned as Frame.Content before any Navigate
            // call, so Frame.DoNavigate never runs for it — fire Loaded here so
            // its Loaded handlers (background work, splash dismissal) run on
            // first display. Frame.DoNavigate handles subsequent pages.
            FrameworkElement.RaiseLoadedTree(_currentPage);

            // Repaint when the Bing wallpaper arrives. Posted via the Avalonia
            // dispatcher so the InvalidateVisual call happens on the UI thread.
            BingWallpaper.Ready += OnBingWallpaperReady;
        }

        // ~30Hz animation tick — drives indeterminate-progress-bar paint cycles
        // (and any future ticker-driven visuals) while there's an open popup or
        // similar animating overlay. Cheap when nothing's animating; the timer
        // only runs while there's at least one IsEffectivelyOpen popup in tree.
        private global::Avalonia.Threading.DispatcherTimer? _animTimer;

        protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _animTimer ??= new global::Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33),
            };
            _animTimer.Tick += OnAnimTick;
            _animTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _frame.Navigated -= OnFrameNavigated;
            UnhookPage(_currentPage);
            BingWallpaper.Ready -= OnBingWallpaperReady;
            if (_animTimer != null)
            {
                _animTimer.Tick -= OnAnimTick;
                _animTimer.Stop();
            }
        }

        private void OnAnimTick(object? sender, EventArgs e)
        {
            // Only repaint while there's actually something animating — open
            // popups (which can host the WP toolkit's PerformanceProgressBar)
            // are the main case. Otherwise leave the surface untouched so we
            // don't burn power on a static page.
            if (_currentPage != null && HasAnimatingVisual(_currentPage))
                InvalidateVisual();
        }

        /// <summary>
        /// True if anything in <paramref name="root"/>'s logical tree is currently
        /// driving its own animation — today that means: any popup that's
        /// effectively open (splash overlays), since their content typically
        /// includes a <c>PerformanceProgressBar</c> drawn by our renderer.
        /// </summary>
        private static bool HasAnimatingVisual(UIElement root)
        {
            var q = new System.Collections.Generic.Queue<UIElement>();
            q.Enqueue(root);
            while (q.Count > 0)
            {
                UIElement el = q.Dequeue();
                if (el is Popup pop && pop.IsEffectivelyOpen) return true;
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
                    case Popup pp:
                        if (pp.Child != null) q.Enqueue(pp.Child);
                        break;
                }
            }
            return false;
        }

        private void OnBingWallpaperReady()
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        }

        private void HookPage(PhoneApplicationPage? page)
        {
            if (page != null) page.MeasureInvalidatedEvent += OnPageInvalidated;
        }

        private void UnhookPage(PhoneApplicationPage? page)
        {
            if (page != null) page.MeasureInvalidatedEvent -= OnPageInvalidated;
        }

        private void OnPageInvalidated(object? sender, EventArgs e)
        {
            // Invalidations can originate from any thread — BackgroundWorker's
            // RunWorkerCompleted typically runs on the captured SynchronizationContext
            // (UI thread when one's installed) but DispatcherTimer ticks and other
            // shimmed event sources can fire on threadpool threads. Avalonia's
            // InvalidateMeasure / InvalidateVisual aren't thread-safe, so marshal.
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                InvalidateMeasure();
                InvalidateVisual();
            });
        }

        public PhoneApplicationFrame Frame => _frame;
        public PhoneApplicationPage? CurrentPage => _currentPage;

        private void OnFrameNavigated(object? sender, NavigationEventArgs e)
        {
            UnhookPage(_currentPage);
            _currentPage = _frame.Content;
            HookPage(_currentPage);
            InvalidateMeasure();
            InvalidateVisual();
        }

        // The phone's logical viewport. WP7 portrait apps lay out against 480×800 DIPs;
        // every Width / Margin / FontSize in user XAML is in those units. To fill the
        // host window without distortion we always measure/arrange the page at this
        // size and apply a uniform scale + centering transform at render time so the
        // result fits the actual control bounds. Pointer hits get de-scaled the same way.
        private const double LogicalPhoneWidth  = 480;
        private const double LogicalPhoneHeight = 800;

        protected override AvSize MeasureOverride(AvSize availableSize)
        {
            if (_currentPage == null) return new AvSize(0, 0);

            // Measure the page at the fixed logical size — independent of window size.
            _currentPage.Measure(new Size(LogicalPhoneWidth, LogicalPhoneHeight));

            // We're happy to fill the available room; return finite extents.
            double w = double.IsInfinity(availableSize.Width)  ? LogicalPhoneWidth  : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? LogicalPhoneHeight : availableSize.Height;
            return new AvSize(w, h);
        }

        protected override AvSize ArrangeOverride(AvSize finalSize)
        {
            if (_currentPage != null)
            {
                _currentPage.Arrange(new Rect(0, 0, LogicalPhoneWidth, LogicalPhoneHeight));
            }
            return finalSize;
        }

        /// <summary>
        /// Compute the (scale, offsetX, offsetY) needed to map the 480×800 logical
        /// viewport into our actual control bounds: uniform scale to fit, centered
        /// with letterbox bars. Returns scale=1 when bounds are degenerate (no
        /// safe transform yet).
        /// </summary>
        private (double scale, double offsetX, double offsetY) GetViewportTransform()
        {
            double bw = Bounds.Width, bh = Bounds.Height;
            if (bw <= 0 || bh <= 0) return (1, 0, 0);
            double scale = Math.Min(bw / LogicalPhoneWidth, bh / LogicalPhoneHeight);
            double offsetX = (bw - LogicalPhoneWidth * scale) / 2;
            double offsetY = (bh - LogicalPhoneHeight * scale) / 2;
            return (scale, offsetX, offsetY);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_currentPage == null) return;

            var (scale, offsetX, offsetY) = GetViewportTransform();

            // Push the centering translation, then the scale. Avalonia composes
            // these in matrix order: a point in logical space (lx, ly) maps to
            // (offsetX + scale*lx, offsetY + scale*ly) on the control.
            using var t1 = context.PushTransform(global::Avalonia.Matrix.CreateTranslation(offsetX, offsetY));
            using var t2 = context.PushTransform(global::Avalonia.Matrix.CreateScale(scale, scale));

            var bounds = new global::Avalonia.Rect(0, 0, LogicalPhoneWidth, LogicalPhoneHeight);
            SilverlightRenderer.RenderPage(context, _currentPage, bounds);
        }

        /// <summary>Convert a pointer position from control coords → logical phone coords.</summary>
        private Point ToLogical(global::Avalonia.Point pos)
        {
            var (scale, offsetX, offsetY) = GetViewportTransform();
            if (scale <= 0) return new Point(pos.X, pos.Y);
            return new Point((pos.X - offsetX) / scale, (pos.Y - offsetY) / scale);
        }

        // Ongoing pointer interaction (null when no pointer is down).
        private PointerInteraction? _interaction;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_currentPage == null) return;

            var logical = ToLogical(e.GetPosition(this));
            var chain = HitTester.HitTest(_currentPage, logical.X, logical.Y);
            _interaction = new PointerInteraction
            {
                StartPos = logical,
                LastPos = logical,
                StartTime = DateTime.UtcNow,
                LastMoveTime = DateTime.UtcNow,
                HitChain = chain,
            };
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_interaction == null) return;

            var pos = ToLogical(e.GetPosition(this));
            double dx = pos.X - _interaction.LastPos.X;
            double dy = pos.Y - _interaction.LastPos.Y;

            // Total horizontal travel from the start determines "is this a drag?".
            double totalDx = pos.X - _interaction.StartPos.X;
            double totalDy = pos.Y - _interaction.StartPos.Y;
            if (!_interaction.IsDragging
                && (Math.Abs(totalDx) > PointerInteraction.TapSlop
                    || Math.Abs(totalDy) > PointerInteraction.TapSlop))
            {
                _interaction.IsDragging = true;
            }

            // Drag dispatch:
            //   - Horizontal-dominant → panorama paging (advance/retreat items)
            //   - Vertical-dominant → scroll the current PanoramaItem's content
            //     (WP7 toolkit wraps panorama-item content in a ScrollViewer via
            //     its default template; we don't apply templates so the content
            //     overflows visually — this gives users a way to reach it).
            if (_interaction.IsDragging && Math.Abs(totalDx) > Math.Abs(totalDy))
            {
                var panorama = FindAncestorByTypeName(_interaction.HitChain, "Microsoft.Phone.Controls.Panorama");
                if (panorama != null)
                {
                    var state = PanoramaStateTable.GetOrCreate(panorama);
                    state.IsDragging = true;
                    state.DragOffset = totalDx;
                    InvalidateVisual();
                }
            }
            else if (_interaction.IsDragging && Math.Abs(totalDy) > Math.Abs(totalDx))
            {
                var panoramaItem = FindAncestorByTypeName(_interaction.HitChain, "Microsoft.Phone.Controls.PanoramaItem");
                if (panoramaItem != null)
                {
                    var scroll = PanoramaItemScrollTable.GetOrCreate(panoramaItem);
                    // Drag-down (positive dy) reveals content above the current
                    // viewport, so subtract dy from ScrollY. Renderer clamps to
                    // a sensible range once it knows the content height.
                    scroll.ScrollY -= dy;
                    if (scroll.ScrollY < 0) scroll.ScrollY = 0;
                    InvalidateVisual();
                }
            }

            _interaction.LastPos = pos;
            _interaction.LastMoveTime = DateTime.UtcNow;
            _ = dx; // suppress unused-var warning if we add use-cases later
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var interaction = _interaction;
            _interaction = null;
            if (interaction == null || _currentPage == null) return;
            e.Pointer.Capture(null);

            var pos = ToLogical(e.GetPosition(this));
            double dx = pos.X - interaction.StartPos.X;
            double dy = pos.Y - interaction.StartPos.Y;
            double dtSec = Math.Max(0.001, (DateTime.UtcNow - interaction.StartTime).TotalSeconds);
            double vx = dx / dtSec;
            double vy = dy / dtSec;

            // Panorama page commit: did the drag/flick cross the half-page or
            // velocity threshold? If yes, advance/retreat the panorama's current
            // index; either way clear the live drag offset.
            var panorama = FindAncestorByTypeName(interaction.HitChain, "Microsoft.Phone.Controls.Panorama");
            if (interaction.IsDragging && Math.Abs(dx) > Math.Abs(dy) && panorama != null
                && panorama is Panel pPanel)
            {
                var state = PanoramaStateTable.GetOrCreate(panorama);
                int childCount = pPanel.Children.Count;
                bool flick = Math.Abs(vx) > PointerInteraction.FlickVelocity;
                bool halfPage = Math.Abs(dx) > Bounds.Width / 3;
                if ((flick || halfPage) && childCount > 0)
                {
                    int direction = dx < 0 ? +1 : -1;
                    state.Advance(direction, childCount);
                }
                else
                {
                    state.DragOffset = 0;
                    state.IsDragging = false;
                }
                InvalidateVisual();
                return;
            }

            // Not a drag → treat as tap. Dispatch:
            //   1. GestureListener.Tap on the closest hit element that has one
            //      attached (covers <toolkit:GestureListener Tap="..."/>).
            //   2. Failing that, Button.Click — preserves the existing menu
            //      navigation behaviour.
            if (!interaction.IsDragging)
            {
                var chain = interaction.HitChain ?? Array.Empty<UIElement>();
                Console.WriteLine($"[Tap] at logical=({pos.X:F0},{pos.Y:F0}), chain depth={chain.Count}");
                int depth = 0;
                foreach (UIElement el in chain)
                {
                    var listener = Microsoft.Phone.Controls.GestureService.GetGestureListener(el);
                    Console.WriteLine($"   [{depth++}] {el.GetType().Name}  GestureListener={(listener != null ? "yes" : "no")}");
                    if (listener != null)
                    {
                        Console.WriteLine($"[Tap] firing GestureListener.Tap on {el.GetType().Name}");
                        // attachedTo=el (the listener's host, used as `sender`),
                        // origin=el (the topmost hit element).
                        try { listener.RaiseTap(pos, attachedTo: el, origin: el); }
                        catch (Exception tex)
                        {
                            Console.WriteLine($"[Tap] handler threw {tex.GetType().Name}: {tex.Message}");
                            Console.WriteLine(tex.StackTrace);
                        }
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                    if (el is Button btn)
                    {
                        Console.WriteLine($"[Tap] firing Button.Click on {btn.GetType().Name}");
                        try { btn.RaiseClick(); }
                        catch (Exception tex)
                        {
                            Console.WriteLine($"[Tap] handler threw {tex.GetType().Name}: {tex.Message}");
                            Console.WriteLine(tex.StackTrace);
                        }
                        InvalidateMeasure();
                        InvalidateVisual();
                        e.Handled = true;
                        return;
                    }
                }
                Console.WriteLine("[Tap] no handler in chain — discarded");
            }
        }

        /// <summary>
        /// Walks the hit chain (innermost → outermost) looking for the first
        /// element whose runtime type's FullName matches <paramref name="typeName"/>.
        /// Used to find the Panorama ancestor of a hit element without taking
        /// a project reference to <c>Microsoft.Phone.Controls.dll</c>.
        /// </summary>
        private static UIElement? FindAncestorByTypeName(IReadOnlyList<UIElement>? chain, string typeName)
        {
            if (chain == null) return null;
            foreach (UIElement el in chain)
            {
                if (el.GetType().FullName == typeName) return el;
            }
            return null;
        }

        // Kept as the externally-callable conversion entry point for tests.
        internal static AvBrush? ConvertBrush(Brush? brush) => SilverlightRenderer.ConvertBrush(brush);
    }
}
