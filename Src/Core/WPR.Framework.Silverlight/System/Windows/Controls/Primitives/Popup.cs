using System;
using System.Collections.Generic;
using System.Threading;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.Primitives.Popup</c>. In Silverlight this
    /// hosts a single <see cref="Child"/> <c>UIElement</c> that floats above the
    /// visual tree, controlled by <see cref="IsOpen"/>. <see cref="Opened"/> and
    /// <see cref="Closed"/> fire when <c>IsOpen</c> transitions. The renderer
    /// special-cases Popup: it skips the Popup during normal-tree paint, then
    /// — after the page renders — walks any open popups and paints their Child
    /// at full page bounds so the popup overlays the entire viewport (the
    /// Silverlight behaviour). Sizing of the Child is driven by Popup's own
    /// Measure/Arrange overrides below.
    /// </summary>
    [ContentProperty(nameof(Child))]
    public class Popup : FrameworkElement
    {
        /// <summary>
        /// Measure the Child so it gets a DesiredSize, but report
        /// <see cref="Size.Empty"/> as the Popup's own desired size — Popup
        /// must not consume space in the parent's layout (it floats above).
        /// Without this, the Child never participates in measure and its
        /// later ArrangedRect is (0,0,0,0); the renderer then has nothing
        /// to paint and the splash overlay vanishes.
        /// </summary>
        protected override Size MeasureCore(Size availableSize)
        {
            if (Child is UIElement c)
            {
                // Give the child whatever the parent gave us; the child will
                // honour its own Width/Height/HorizontalAlignment normally.
                c.Measure(availableSize);
            }
            return Size.Empty;
        }

        /// <summary>
        /// Arrange the Child at (0,0,desiredW,desiredH) in the Popup's own
        /// coordinate space. The renderer paints the Popup at full page
        /// bounds (480×800 logical), so the Child's relative ArrangedRect
        /// effectively becomes its position on screen modulo Horizontal/
        /// VerticalOffset.
        /// </summary>
        protected override void ArrangeCore(Rect finalRect)
        {
            if (Child is UIElement c)
            {
                Size ds = c.DesiredSize;
                // Default to a 480×800 slot if the child didn't return a finite
                // desired size — the typical splash-popup case where the Grid
                // child has explicit Width/Height already returns a real size.
                double w = (ds.Width  > 0 && !double.IsInfinity(ds.Width))  ? ds.Width  : finalRect.Width;
                double h = (ds.Height > 0 && !double.IsInfinity(ds.Height)) ? ds.Height : finalRect.Height;
                c.Arrange(new Rect(0, 0, w, h));
            }
        }

        public static readonly DependencyProperty ChildProperty =
            DependencyProperty.Register(nameof(Child), typeof(UIElement), typeof(Popup),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(Popup),
                new PropertyMetadata((object)false, OnIsOpenChanged));

        public static readonly DependencyProperty HorizontalOffsetProperty =
            DependencyProperty.Register(nameof(HorizontalOffset), typeof(double), typeof(Popup),
                new PropertyMetadata((object)0.0));

        public static readonly DependencyProperty VerticalOffsetProperty =
            DependencyProperty.Register(nameof(VerticalOffset), typeof(double), typeof(Popup),
                new PropertyMetadata((object)0.0));

        public UIElement? Child
        {
            get => (UIElement?)GetValue(ChildProperty);
            set => SetValue(ChildProperty, value);
        }

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty)!;
            set => SetValue(IsOpenProperty, value);
        }

        public double HorizontalOffset
        {
            get => (double)GetValue(HorizontalOffsetProperty)!;
            set => SetValue(HorizontalOffsetProperty, value);
        }

        public double VerticalOffset
        {
            get => (double)GetValue(VerticalOffsetProperty)!;
            set => SetValue(VerticalOffsetProperty, value);
        }

        public event EventHandler? Opened;
        public event EventHandler? Closed;

        /// <summary>
        /// Minimum on-screen time once a popup opens, before close-by-IsOpen will
        /// actually disappear it visually. Real WP7 shows the splash for as long
        /// as the launch BackgroundWorker takes; on our desktop host the BW often
        /// completes within milliseconds (shimmed XNA Live APIs throw, BW catches
        /// → Completed fires instantly). Without a floor, the splash blinks past
        /// faster than a frame.
        /// </summary>
        public static TimeSpan MinimumDisplayDuration { get; set; } = TimeSpan.FromMilliseconds(1500);

        // Wall-clock instant the popup most recently became visible. UTC ticks.
        private long _shownAtTicks;
        // True once a deferred-close repaint has been scheduled, so back-to-back
        // IsOpen flips don't queue redundant timers.
        private bool _pendingRepaintScheduled;

        /// <summary>
        /// The renderer's view of "is this popup visible?". Differs from
        /// <see cref="IsOpen"/> by enforcing <see cref="MinimumDisplayDuration"/>:
        /// even after user code sets <c>IsOpen=false</c>, this stays true until
        /// the floor elapses. The user-visible <see cref="IsOpen"/> property is
        /// left alone so that gating checks like
        /// <c>if (popup.IsOpen) return true;</c> in the game's tap guards observe
        /// the close immediately (the guards SHOULD release as soon as the user
        /// code intent has fired, not when the visual transition completes).
        /// </summary>
        internal bool IsEffectivelyOpen
        {
            get
            {
                if (IsOpen) return true;
                if (_shownAtTicks == 0) return false;
                TimeSpan elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _shownAtTicks);
                return elapsed < MinimumDisplayDuration;
            }
        }

        // ---------------------------------------------------------------------------------
        // Open-popup registry
        // ---------------------------------------------------------------------------------

        private static readonly object OpenLock = new object();
        private static readonly List<Popup> Open = new List<Popup>();
        private static long _nextOpenSequence;

        /// <summary>
        /// When this popup most recently opened, as a process-wide sequence number. Popups are
        /// drawn — and hit-tested in reverse — in this order, which is Silverlight's: the popup
        /// opened last is on top, wherever in the tree it is declared.
        /// </summary>
        internal long OpenSequence { get; private set; }

        /// <summary>
        /// Every popup that is open or still inside its <see cref="MinimumDisplayDuration"/>
        /// floor, in the order they opened.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the only way to find a popup that is in no visual tree</b>, and that
        /// is the common case rather than an edge one: WP7 code routinely writes
        /// <c>new Popup { Child = dialog, IsOpen = true }</c> without ever adding it anywhere,
        /// and the toolkit's <c>ContextMenu</c> and <c>CustomMessageBox</c> are built exactly that
        /// way. A tree walk alone never sees them.</para>
        /// <para>Strong references, because an open popup is on screen and Silverlight holds it
        /// for exactly as long. <see cref="ResetForNewLaunch"/> empties it between games.</para>
        /// </remarks>
        internal static List<Popup> OpenInOrder()
        {
            lock (OpenLock)
            {
                // Prune anything whose floor has run out since it closed; the timer that would
                // have done it may not have fired yet, or (Avalonia gone) may never.
                Open.RemoveAll(static p => !p.IsEffectivelyOpen);
                return new List<Popup>(Open);
            }
        }

        private static void Remember(Popup p)
        {
            lock (OpenLock)
            {
                p.OpenSequence = ++_nextOpenSequence;
                Open.Remove(p);
                Open.Add(p);
            }
        }

        private static void Forget(Popup p)
        {
            lock (OpenLock)
            {
                if (!p.IsEffectivelyOpen) Open.Remove(p);
            }
        }

        /// <summary>Drops every open popup. Called between game launches.</summary>
        public static void ResetForNewLaunch()
        {
            lock (OpenLock)
            {
                Open.Clear();
                _nextOpenSequence = 0;
            }
        }

        /// <summary>
        /// Lays out <see cref="Child"/> against <paramref name="available"/> when it has not been
        /// laid out — a popup created in code and opened without ever being in a tree, or one
        /// whose child was set after the page's layout pass. Touches only the child, never the
        /// popup's own slot, which belongs to its parent's layout.
        /// </summary>
        internal void EnsureChildLaidOut(Size available)
        {
            if (Child is not UIElement c) return;
            Rect r = c.ArrangedRect;
            if (c.IsMeasureValid && c.IsArrangeValid && r.Width > 0 && r.Height > 0) return;

            c.Measure(available);
            Size ds = c.DesiredSize;
            double w = (ds.Width  > 0 && !double.IsInfinity(ds.Width))  ? ds.Width  : available.Width;
            double h = (ds.Height > 0 && !double.IsInfinity(ds.Height)) ? ds.Height : available.Height;
            c.Arrange(new Rect(0, 0, w, h));
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the thread that owns the visual tree: the
        /// synchronisation context captured when the popup closed if there was one (Avalonia's UI
        /// thread), otherwise the game thread of the mixed-mode host. Neither path names Avalonia,
        /// so this assembly's popup no longer needs it to close.
        /// </summary>
        private static void PostToTreeThread(SynchronizationContext? context, Action action)
        {
            void Safe()
            {
                try { action(); } catch { /* a repaint nudge is never worth a crash */ }
            }

            if (context != null)
            {
                try { context.Post(static s => ((Action)s!)(), (Action)Safe); return; }
                catch { /* context gone (window closed); fall through */ }
            }

            // Runs inline when no game loop is registered — the honest fallback, see
            // XnaBackend.PostToGameThread.
            global::WPR.Xna.Rhi.XnaBackend.PostToGameThread(Safe);
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Popup p) return;
            bool now = e.NewValue is bool b && b;
            Console.WriteLine($"[Popup] IsOpen -> {now}  (instance #{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p)})");

            if (now)
            {
                p._shownAtTicks = DateTime.UtcNow.Ticks;
                Remember(p);
                p.InvalidateMeasure();
                try { p.Opened?.Invoke(p, EventArgs.Empty); } catch { }
                return;
            }

            // IsOpen → false. If the floor hasn't elapsed yet, the renderer's
            // IsEffectivelyOpen will keep it visible — but we have to actively
            // repaint once the floor expires, otherwise nobody invalidates the
            // visual tree and the stale "splash visible" state lingers.
            long shownAt = p._shownAtTicks;
            TimeSpan elapsed = shownAt == 0
                ? TimeSpan.MaxValue
                : TimeSpan.FromTicks(DateTime.UtcNow.Ticks - shownAt);
            TimeSpan floor = MinimumDisplayDuration;

            if (elapsed >= floor)
            {
                p._shownAtTicks = 0;
                Forget(p);
                p.InvalidateMeasure();
                try { p.Closed?.Invoke(p, EventArgs.Empty); } catch { }
                return;
            }

            // Floor not met: defer the visual close. Schedule a single timer to
            // fire one InvalidateMeasure when the floor expires; renderer reads
            // IsEffectivelyOpen and stops drawing the splash on that pass.
            Console.WriteLine($"[Popup] minimum display floor ({floor.TotalMilliseconds:0}ms) not yet reached " +
                              $"(open for {elapsed.TotalMilliseconds:0}ms); holding splash until then.");

            if (p._pendingRepaintScheduled)
            {
                try { p.Closed?.Invoke(p, EventArgs.Empty); } catch { }
                return;
            }
            p._pendingRepaintScheduled = true;

            TimeSpan delay = floor - elapsed;
            // Captured HERE, on the thread that closed the popup — the Avalonia UI thread in the
            // desktop Silverlight host, the game thread in the mixed-mode one — because the
            // continuation below runs on the thread pool, and the visual tree is not thread-safe.
            SynchronizationContext? closer = SynchronizationContext.Current;
            _ = System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ =>
                PostToTreeThread(closer, () =>
                {
                    p._pendingRepaintScheduled = false;
                    p._shownAtTicks = 0;
                    Forget(p);
                    p.InvalidateMeasure();
                }));

            try { p.Closed?.Invoke(p, EventArgs.Empty); } catch { }
        }
    }
}
