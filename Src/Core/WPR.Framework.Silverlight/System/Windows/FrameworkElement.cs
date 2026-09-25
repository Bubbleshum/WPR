using System;

namespace WPR.SilverlightCompability
{
    public class FrameworkElement : UIElement
    {
        public static readonly DependencyProperty WidthProperty =
            DependencyProperty.Register(nameof(Width), typeof(double), typeof(FrameworkElement),
                new PropertyMetadata(double.NaN));

        public static readonly DependencyProperty HeightProperty =
            DependencyProperty.Register(nameof(Height), typeof(double), typeof(FrameworkElement),
                new PropertyMetadata(double.NaN));

        public static readonly DependencyProperty MinWidthProperty =
            DependencyProperty.Register(nameof(MinWidth), typeof(double), typeof(FrameworkElement),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty MaxWidthProperty =
            DependencyProperty.Register(nameof(MaxWidth), typeof(double), typeof(FrameworkElement),
                new PropertyMetadata(double.PositiveInfinity));

        public static readonly DependencyProperty MinHeightProperty =
            DependencyProperty.Register(nameof(MinHeight), typeof(double), typeof(FrameworkElement),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty MaxHeightProperty =
            DependencyProperty.Register(nameof(MaxHeight), typeof(double), typeof(FrameworkElement),
                new PropertyMetadata(double.PositiveInfinity));

        public static readonly DependencyProperty MarginProperty =
            DependencyProperty.Register(nameof(Margin), typeof(Thickness), typeof(FrameworkElement),
                new PropertyMetadata(new Thickness()));

        public static readonly DependencyProperty HorizontalAlignmentProperty =
            DependencyProperty.Register(nameof(HorizontalAlignment), typeof(HorizontalAlignment), typeof(FrameworkElement),
                new PropertyMetadata(HorizontalAlignment.Stretch));

        public static readonly DependencyProperty VerticalAlignmentProperty =
            DependencyProperty.Register(nameof(VerticalAlignment), typeof(VerticalAlignment), typeof(FrameworkElement),
                new PropertyMetadata(VerticalAlignment.Stretch));

        public static readonly DependencyProperty DataContextProperty =
            DependencyProperty.Register(nameof(DataContext), typeof(object), typeof(FrameworkElement),
                new PropertyMetadata((object?)null, OnDataContextChanged));

        public static readonly DependencyProperty TagProperty =
            DependencyProperty.Register(nameof(Tag), typeof(object), typeof(FrameworkElement),
                new PropertyMetadata((object?)null));

        /// <summary>
        /// Canonical Background DP shared across Panel / ContentControl / Border /
        /// Control. Real Silverlight declares Background separately on each of
        /// those types but games freely cast between them and rely on a single
        /// stored value — Minesweeper's <c>LoadPanorama</c> sets the panorama
        /// background via the <c>Control::set_Background</c> IL inherited from
        /// Silverlight's pre-patch Panorama base, while our renderer reads it
        /// back via <c>Panel.Background</c>. Storing under one DP keeps the
        /// two views in sync; the per-class <c>BackgroundProperty</c> fields
        /// are kept as aliases to this one so existing IL field references
        /// (<c>ldsfld Panel::BackgroundProperty</c>, etc.) still resolve.
        /// </summary>
        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(FrameworkElement),
                new PropertyMetadata((object?)null));

        public Brush? Background
        {
            get => (Brush?)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public double Width
        {
            get => (double)GetValue(WidthProperty)!;
            set => SetValue(WidthProperty, value);
        }

        public double Height
        {
            get => (double)GetValue(HeightProperty)!;
            set => SetValue(HeightProperty, value);
        }

        public double MinWidth
        {
            get => (double)GetValue(MinWidthProperty)!;
            set => SetValue(MinWidthProperty, value);
        }

        public double MaxWidth
        {
            get => (double)GetValue(MaxWidthProperty)!;
            set => SetValue(MaxWidthProperty, value);
        }

        public double MinHeight
        {
            get => (double)GetValue(MinHeightProperty)!;
            set => SetValue(MinHeightProperty, value);
        }

        public double MaxHeight
        {
            get => (double)GetValue(MaxHeightProperty)!;
            set => SetValue(MaxHeightProperty, value);
        }

        public Thickness Margin
        {
            get => (Thickness)GetValue(MarginProperty)!;
            set => SetValue(MarginProperty, value);
        }

        public HorizontalAlignment HorizontalAlignment
        {
            get => (HorizontalAlignment)GetValue(HorizontalAlignmentProperty)!;
            set => SetValue(HorizontalAlignmentProperty, value);
        }

        public VerticalAlignment VerticalAlignment
        {
            get => (VerticalAlignment)GetValue(VerticalAlignmentProperty)!;
            set => SetValue(VerticalAlignmentProperty, value);
        }

        public object? DataContext
        {
            get => GetValue(DataContextProperty);
            set => SetValue(DataContextProperty, value);
        }

        public object? Tag
        {
            get => GetValue(TagProperty);
            set => SetValue(TagProperty, value);
        }

        public string? Name { get; set; }

        private System.Collections.Generic.List<BindingExpression>? _bindings;

        /// <summary>
        /// Attaches a one-way binding from the source's path to a target DP on this element.
        /// Replaces any prior binding on the same target property.
        /// </summary>
        public BindingExpressionBase SetBinding(DependencyProperty dp, Binding binding)
        {
            _bindings ??= new System.Collections.Generic.List<BindingExpression>();
            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(GetBindingTargetProp(_bindings[i]), dp))
                {
                    _bindings[i].Detach();
                    _bindings.RemoveAt(i);
                }
            }

            var expr = new BindingExpression(this, dp, binding);
            _bindings.Add(expr);
            expr.Refresh();
            // SL's SetBinding returns a BindingExpression (a BindingExpressionBase
            // subclass). We don't expose our internal BindingExpression at that
            // hierarchy yet, so hand back a fresh BindingExpressionBase wrapper.
            return new BindingExpressionBase();
        }

        // Reflection-free access to the private target-property field of an expression.
        // Avoided by exposing a property on BindingExpression directly.
        private static DependencyProperty GetBindingTargetProp(BindingExpression e) => e.TargetProperty;

        private static void OnDataContextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement fe && fe._bindings != null)
            {
                foreach (var b in fe._bindings) b.Refresh();
            }
            // Propagate down: descendants without an explicit DataContext rely on the inherited one.
            if (d is FrameworkElement self) RefreshDescendantBindings(self);
        }

        /// <summary>
        /// Re-evaluates every binding in the tree rooted at <paramref name="root"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Run once after a XAML document is fully built, because a binding created
        /// during parsing cannot see its own DataContext.</b> <c>SetBinding</c> refreshes
        /// immediately, and at that moment the element is not yet attached to its parent — so
        /// <c>BindingExpression.ResolveSource</c> walks a <c>Parent</c> chain that does not exist
        /// and finds nothing. Nothing corrected it afterwards either: the DataContext was set on
        /// the parent during the PARENT's own attribute pass, before any child existed, so the
        /// change notification that would have refreshed descendants had no descendants to
        /// refresh.</para>
        ///
        /// <para>The result was that every source-less binding in every Silverlight page silently
        /// resolved to nothing. Carcassonne's main menu is the measured case — nine of its fifteen
        /// bindings take their source from the layout root's DataContext, which is where all of its
        /// localised button labels come from, so the menu rendered with every label blank.</para>
        ///
        /// <para>Unlike <see cref="RefreshDescendantBindings"/> this does NOT skip elements that
        /// have a DataContext of their own: those bindings are equally unresolved after parsing,
        /// and a binding whose source is already correct re-resolves to the same value.</para>
        /// </remarks>
        internal static void RefreshBindingsTree(UIElement? root, int depth = 0)
        {
            if (root == null || depth > 64) return;

            if (root is FrameworkElement fe && fe._bindings != null)
            {
                foreach (var b in fe._bindings)
                {
                    try { b.Refresh(); }
                    catch { /* one bad binding must not stop the rest of the page resolving */ }
                }
            }

            switch (root)
            {
                case Panel panel:
                    foreach (UIElement child in panel.Children) RefreshBindingsTree(child, depth + 1);
                    break;

                case Border border:
                    RefreshBindingsTree(border.Child, depth + 1);
                    break;

                case ContentControl content:
                    RefreshBindingsTree(content.Presenter ?? content.Content as UIElement, depth + 1);
                    break;
            }
        }

        private static void RefreshDescendantBindings(UIElement el)
        {
            switch (el)
            {
                case Panel p:
                    foreach (UIElement c in p.Children)
                    {
                        if (c is FrameworkElement cfe && cfe.GetValue(DataContextProperty) == null
                            && cfe._bindings != null)
                        {
                            foreach (var b in cfe._bindings) b.Refresh();
                        }
                        RefreshDescendantBindings(c);
                    }
                    break;
                case ContentControl cc when cc.Presenter != null:
                    if (cc.Presenter is FrameworkElement pfe && pfe.GetValue(DataContextProperty) == null
                        && pfe._bindings != null)
                    {
                        foreach (var b in pfe._bindings) b.Refresh();
                    }
                    RefreshDescendantBindings(cc.Presenter);
                    break;
            }
        }

        /// <summary>
        /// Pre-built name → instance map populated by <see cref="XamlReader.LoadComponent"/>
        /// onto the root component. The user's auto-generated <c>InitializeComponent</c>
        /// calls <see cref="FindName"/> for every <c>x:Name</c> right after LoadComponent
        /// returns; we honour those by consulting this scope first, since walking the
        /// logical tree wouldn't reach into containers our shims don't model fully
        /// (Panorama items, ListBox containers, etc.) — and a null cast there would
        /// clobber the field assignments that the parser's <c>WireFields</c> already
        /// did correctly.
        /// </summary>
        internal System.Collections.Generic.Dictionary<string, object>? _nameScope;

        /// <summary>
        /// Returns the element registered under <paramref name="name"/> in the XAML name
        /// scope of the page (the scope is rooted at whatever <see cref="XamlReader.LoadComponent"/>
        /// loaded). Falls back to walking Panel.Children / ContentControl.Content for
        /// trees built imperatively in code.
        /// </summary>
        public virtual object? FindName(string name)
        {
            // Authoritative source: the XAML parser's name table. Set on the loaded
            // component (and inherited by lookups on the component itself).
            if (_nameScope != null && _nameScope.TryGetValue(name, out var hit))
                return hit;

            if (Name == name) return this;

            // The user's auto-generated InitializeComponent emits non-virtual `call FrameworkElement::FindName`
            // (rather than `callvirt`), so we cannot rely on a derived override taking effect.
            // Walk every kind of child container we know about, right here.
            if (this is Panel p)
            {
                foreach (UIElement child in p.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        object? found = fe.FindName(name);
                        if (found != null) return found;
                    }
                }
            }

            if (this is ContentControl cc)
            {
                if (cc.Content is FrameworkElement contentFe)
                {
                    object? found = contentFe.FindName(name);
                    if (found != null) return found;
                }
            }

            return null;
        }

        public double ActualWidth { get; private set; }
        public double ActualHeight { get; private set; }

        public event RoutedEventHandler? Loaded;
        public event RoutedEventHandler? Unloaded;

        // SizeChanged: WP Toolkit controls (Panorama) subscribe to it. Still not raised.
#pragma warning disable CS0067
        public event SizeChangedEventHandler? SizeChanged;
#pragma warning restore CS0067

        /// <summary>
        /// Raised after a layout pass — see <see cref="RunLayoutPass"/>.
        /// </summary>
        /// <remarks>
        /// <b>This is where a mixed-mode game builds its Silverlight-to-texture renderer</b>, so
        /// it is load-bearing rather than decorative: it was declared and never raised until
        /// 2026-09-22, and four of the six titles that composite phone controls into their scene
        /// create their <c>UIElementRenderer</c> from this handler and then dereference it in
        /// their draw. No event, no renderer, and a NullReferenceException once per frame out of
        /// the game's own OnDraw.
        /// </remarks>
        public event EventHandler? LayoutUpdated;

        protected internal void RaiseLayoutUpdated() => LayoutUpdated?.Invoke(this, EventArgs.Empty);

        public static readonly DependencyProperty StyleProperty =
            DependencyProperty.Register(nameof(Style), typeof(Style), typeof(FrameworkElement),
                new PropertyMetadata((object?)null, OnStyleChanged));

        public Style? Style
        {
            get => (Style?)GetValue(StyleProperty);
            set => SetValue(StyleProperty, value);
        }

        private static void OnStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // Apply the new style's setters to this element. We don't currently
            // unapply the old style — that's a known partial fidelity (real WP/SL
            // restores prior values via DP coercion). For one-shot XAML page
            // loads (the only place styles are set in WP7 games) the partial
            // semantics are fine.
            if (d is FrameworkElement fe && e.NewValue is Style s)
                s.Apply(fe);
        }

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(string), typeof(FrameworkElement),
                new PropertyMetadata("Segoe UI"));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(FrameworkElement),
                new PropertyMetadata(14.0));

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(FrameworkElement),
                new PropertyMetadata(FontWeights.Normal));

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FrameworkElement),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty LanguageProperty =
            DependencyProperty.Register(nameof(Language), typeof(XmlLanguage), typeof(FrameworkElement),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty FlowDirectionProperty =
            DependencyProperty.Register(nameof(FlowDirection), typeof(FlowDirection), typeof(FrameworkElement),
                new PropertyMetadata(FlowDirection.LeftToRight));

        public string FontFamily
        {
            get => (string)GetValue(FontFamilyProperty)!;
            set => SetValue(FontFamilyProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty)!;
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => (FontWeight)GetValue(FontWeightProperty)!;
            set => SetValue(FontWeightProperty, value);
        }

        public Brush? Foreground
        {
            get => (Brush?)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        /// <summary>The element's language (SL <c>FrameworkElement.Language</c>). Stored only —
        /// we don't do per-language text shaping — but the WP app template sets it during init.</summary>
        public XmlLanguage? Language
        {
            get => (XmlLanguage?)GetValue(LanguageProperty);
            set => SetValue(LanguageProperty, value);
        }

        /// <summary>Text/layout flow direction (SL <c>FrameworkElement.FlowDirection</c>). Stored
        /// only; RTL mirroring isn't applied, which is fine for LTR locales (the common case).</summary>
        public FlowDirection FlowDirection
        {
            get => (FlowDirection)GetValue(FlowDirectionProperty)!;
            set => SetValue(FlowDirectionProperty, value);
        }

        /// <summary>Stub for SL's template-application lifecycle hook. WP Toolkit
        /// overrides this to grab named template parts; we don't apply templates,
        /// so the base implementation is a no-op.</summary>
        public virtual void OnApplyTemplate() { }

        /// <summary>
        /// Silverlight's <c>FrameworkElement.Parent</c> returns <c>DependencyObject</c>
        /// (broader than <c>UIElement.Parent</c>'s return type). User IL emits
        /// <c>callvirt get_Parent</c> against <c>FrameworkElement</c> with return type
        /// <c>DependencyObject</c>, so we shadow the inherited UIElement.Parent
        /// with a same-named accessor whose signature matches. The actual parent
        /// pointer lives on UIElement; we just re-type it.
        /// </summary>
        public new DependencyObject? Parent => base.Parent;

        // Per-element resource bag — Silverlight's <FrameworkElement>.Resources["x"]
        // dictionary, populated from <UserControl.Resources>/<UserControl.Resources>
        // in XAML and read by user code via Resources[name]. Type is in the
        // WPR.WindowsCompability namespace (declared in this same assembly, see
        // ResourceDictionary.cs) to match the post-patch user-IL signature.
        private WPR.WindowsCompability.ResourceDictionary? _resources;

        public WPR.WindowsCompability.ResourceDictionary Resources
            => _resources ??= new WPR.WindowsCompability.ResourceDictionary();

        /// <summary>True if this element has had its own <see cref="Resources"/>
        /// touched. Used by the StaticResource resolver so walking the ancestor
        /// chain doesn't force-allocate an empty dictionary on every parent.</summary>
        internal bool HasResources => _resources != null && _resources.Count > 0;

        /// <summary>
        /// XAML-declared triggers on this element — <c>&lt;FrameworkElement.Triggers&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Read-only so the XAML reader appends to it, which is how a collection property is
        /// recognised. Silverlight only allows <see cref="EventTrigger"/> on <c>Loaded</c> here;
        /// see that type for why firing it matters rather than merely parsing it.
        /// </remarks>
        public TriggerCollection Triggers { get; } = new TriggerCollection();

        protected internal void RaiseLoaded()
        {
            Loaded?.Invoke(this, new RoutedEventArgs { OriginalSource = this });

            // After the game's own handlers, so a trigger cannot animate a property the handler
            // is about to overwrite.
            foreach (TriggerBase trigger in Triggers)
            {
                if (trigger is EventTrigger eventTrigger) eventTrigger.Fire(this);
            }
        }
        protected internal void RaiseUnloaded() => Unloaded?.Invoke(this, new RoutedEventArgs { OriginalSource = this });

        // Track whether Loaded has fired for this element so we don't double-fire
        // (real SL fires it once per attach; we only attach once per page lifetime).
        private bool _loadedRaised;

        /// <summary>
        /// Walk <paramref name="root"/>'s visual tree and raise <see cref="Loaded"/>
        /// on every <see cref="FrameworkElement"/> bottom-up (children before parents),
        /// matching Silverlight semantics. Idempotent per element — calling twice
        /// is a no-op on the second pass.
        ///
        /// We don't have a true visual tree (the renderer walks the logical tree on
        /// each frame), so this method discovers children via the well-known content
        /// hosts: <see cref="Panel.Children"/>, <see cref="ContentControl.Content"/>,
        /// <see cref="ScrollViewer.Content"/>, <see cref="Border.Child"/>,
        /// <see cref="Popup.Child"/>. Items added through other paths (custom
        /// templated controls) won't be traversed — caller may extend if needed.
        ///
        /// The Loaded event is the standard place where games kick off background
        /// work, swap out splash overlays, etc. <c>Minesweeper.MainPage..ctor</c>
        /// subscribes <c>r</c> to <c>this.Loaded</c>; <c>r</c> kicks off the
        /// <see cref="System.ComponentModel.BackgroundWorker"/> whose Completed
        /// handler closes the initial loading-screen popup. If we never raise
        /// Loaded the popup stays open and the per-tap guard reads
        /// <c>popup.IsOpen == true</c> and blocks every navigation.
        /// </summary>
        public static void RaiseLoadedTree(UIElement? root)
        {
            if (root == null) return;
            // DFS post-order so children fire before parents.
            foreach (UIElement child in EnumerateLogicalChildren(root))
                RaiseLoadedTree(child);
            if (root is FrameworkElement fe && !fe._loadedRaised)
            {
                fe._loadedRaised = true;
                bool hasSubscribers = fe.Loaded != null;
                if (hasSubscribers)
                    Console.WriteLine($"[Loaded] firing on {fe.GetType().Name}");
                try { fe.RaiseLoaded(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Loaded] handler on {fe.GetType().Name} threw {ex.GetType().Name}: {ex.Message}");
                    if (ex.StackTrace != null) Console.WriteLine(ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// Measures and arranges <paramref name="root"/> against the WP7 screen, then raises
        /// <see cref="LayoutUpdated"/> across the tree.
        /// </summary>
        /// <remarks>
        /// <para>Silverlight ran this continuously as part of compositing. WPR's mixed-mode host
        /// never composites the Silverlight tree — that is what lets those games run without
        /// Avalonia, and on Android at all — so nothing else drives a layout pass and every
        /// element would otherwise keep <c>ActualWidth</c>/<c>ActualHeight</c> of zero and never
        /// see <see cref="LayoutUpdated"/>.</para>
        ///
        /// <para><b>Called once after navigation, deliberately NOT once per frame.</b> These
        /// games treat the event as "the layout changed, rebuild the surface": their handlers
        /// dispose the previous <c>UIElementRenderer</c> and construct a new one, which on a
        /// per-frame schedule would allocate and destroy a full-screen texture sixty times a
        /// second. Silverlight only raised it after a pass that actually ran, and one pass is all
        /// a fixed 480x800 page with no live compositing will ever have.</para>
        ///
        /// <para>Post-order, so a parent's handler sees children that have already been
        /// arranged — the same ordering <see cref="RaiseLoadedTree"/> uses and for the same
        /// reason.</para>
        /// </remarks>
        public static void RunLayoutPass(UIElement? root, double width, double height)
        {
            if (root == null) return;

            LayOut(root, width, height);
            RaiseLayoutUpdatedTree(root);
        }

        /// <summary>
        /// Measures and arranges one element, then does the same for a hosted page.
        /// </summary>
        /// <remarks>
        /// <b>A <see cref="Frame"/> does not lay out its own page</b>, which is the whole reason
        /// this recurses rather than being two calls. The Avalonia frame view measures and
        /// arranges the current page by hand rather than relying on the frame to do it, so
        /// arranging a frame leaves the page inside it at <c>ActualWidth</c>/<c>ActualHeight</c>
        /// of zero — and a mixed-mode game's LayoutUpdated handler opens with exactly that test
        /// before it will build its renderer, so it returns immediately and the renderer stays
        /// null for ever. Measured on Sid Meier's Pirates.
        ///
        /// <para>The page gets the frame's own size because a frame fills its parent; there is no
        /// chrome around it on a phone.</para>
        ///
        /// <para>Fixed here rather than by giving <c>Frame</c> real layout overrides, which would
        /// be the tidier change but would alter the Avalonia path that already works — it would
        /// then measure every page twice. Worth revisiting if Frame ever needs real layout.</para>
        /// </remarks>
        private static void LayOut(UIElement element, double width, double height)
        {
            try
            {
                element.Measure(new Size(width, height));
                element.Arrange(new Rect(0, 0, width, height));
            }
            catch (Exception ex)
            {
                // A layout that throws is the page's own problem, and the event is still worth
                // raising: a handler that only needs the screen size does not care that one
                // child measured badly.
                System.Diagnostics.Trace.WriteLine(
                    "[wpr-layout] layout of " + element.GetType().Name + " threw: " +
                    ex.GetType().Name + ": " + ex.Message);
            }

            if (element is Frame frame && frame.Content is UIElement page)
            {
                LayOut(page, width, height);
            }
        }

        private static void RaiseLayoutUpdatedTree(UIElement? root)
        {
            if (root == null) return;
            foreach (UIElement child in EnumerateLogicalChildren(root))
                RaiseLayoutUpdatedTree(child);

            if (root is FrameworkElement fe)
            {
                try { fe.RaiseLayoutUpdated(); }
                catch (Exception ex)
                {
                    // Matches the Loaded walk: one element's handler must not stop the rest of
                    // the tree being told, or a single bad page kills every renderer on it.
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-layout] LayoutUpdated handler on {fe.GetType().Name} threw: " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<UIElement> EnumerateLogicalChildren(UIElement el)
        {
            // Order matters: more-specific subclasses first. ScrollViewer derives
            // from ContentControl so it'd be caught by the ContentControl arm
            // (which already covers Content), so we don't list it separately.
            switch (el)
            {
                // Frame FIRST, and it is not optional. Frame derives straight from
                // FrameworkElement rather than ContentControl, so without an arm of its own the
                // walk stopped at the frame and never reached the page inside it — and the frame
                // is the root visual of every WP7 app, so NOTHING below it was ever traversed.
                // That silently disabled both users of this walk: RaiseLoadedTree (so Loaded
                // never fired on a page) and the layout pass (so LayoutUpdated never fired, and
                // a mixed-mode title's UIElementRenderer was never built). Found 2026-09-22 via
                // Sid Meier's Pirates, whose LogoPage.OnDraw dereferenced that null renderer once
                // per frame.
                case Frame f:
                    if (f.Content is UIElement framed) yield return framed;
                    break;
                case Panel p:
                    foreach (UIElement c in p.Children) yield return c;
                    break;
                case ContentControl cc:
                    if (cc.Content is UIElement ccChild) yield return ccChild;
                    if (cc.Presenter != null && !ReferenceEquals(cc.Presenter, cc.Content))
                        yield return cc.Presenter;
                    break;
                case Border b:
                    if (b.Child != null) yield return b.Child;
                    break;
                case Popup pop:
                    if (pop.Child != null) yield return pop.Child;
                    break;
            }
        }

        protected override Size MeasureCore(Size availableSize)
        {
            Thickness m = Margin;

            double availW = Math.Max(0, availableSize.Width - m.Left - m.Right);
            double availH = Math.Max(0, availableSize.Height - m.Top - m.Bottom);

            double w = Width;
            double h = Height;
            double minW = MinWidth, maxW = MaxWidth;
            double minH = MinHeight, maxH = MaxHeight;

            if (!double.IsNaN(w)) availW = w;
            if (!double.IsNaN(h)) availH = h;

            availW = Clamp(availW, minW, maxW);
            availH = Clamp(availH, minH, maxH);

            Size measured = MeasureOverride(new Size(availW, availH));

            double mw = double.IsNaN(w) ? measured.Width : w;
            double mh = double.IsNaN(h) ? measured.Height : h;

            mw = Clamp(mw, minW, maxW);
            mh = Clamp(mh, minH, maxH);

            return new Size(mw + m.Left + m.Right, mh + m.Top + m.Bottom);
        }

        /// <summary>
        /// SL/WPF arrangement contract — translate the parent's slot into our actual
        /// placement: subtract Margin, snap to fixed Width/Height, then position
        /// inside the remaining inner slot based on HorizontalAlignment /
        /// VerticalAlignment. Returns an absolute rect (same coordinate space as
        /// the slot, i.e. relative to the parent).
        /// </summary>
        protected override Rect ResolveArrangeRect(Rect slot)
        {
            Thickness m = Margin;
            double slotX = slot.X + m.Left;
            double slotY = slot.Y + m.Top;
            double slotW = Math.Max(0, slot.Width - m.Left - m.Right);
            double slotH = Math.Max(0, slot.Height - m.Top - m.Bottom);

            // Start with full slot (Stretch behavior).
            double useW = slotW;
            double useH = slotH;

            // Non-Stretch alignment + measured DesiredSize determine our actual extent.
            Size desired = DesiredSize;
            double desiredInnerW = Math.Max(0, desired.Width - m.Left - m.Right);
            double desiredInnerH = Math.Max(0, desired.Height - m.Top - m.Bottom);

            if (HorizontalAlignment != HorizontalAlignment.Stretch)
                useW = Math.Min(slotW, desiredInnerW);
            if (VerticalAlignment != VerticalAlignment.Stretch)
                useH = Math.Min(slotH, desiredInnerH);

            // Fixed Width/Height wins.
            double fw = Width, fh = Height;
            if (!double.IsNaN(fw)) useW = fw;
            if (!double.IsNaN(fh)) useH = fh;

            useW = Clamp(useW, MinWidth, MaxWidth);
            useH = Clamp(useH, MinHeight, MaxHeight);

            // Position within slot according to alignment.
            double finalX = slotX;
            double finalY = slotY;
            switch (HorizontalAlignment)
            {
                case HorizontalAlignment.Center:  finalX = slotX + (slotW - useW) / 2; break;
                case HorizontalAlignment.Right:   finalX = slotX + (slotW - useW);     break;
                // Left and Stretch both stay at slotX (Stretch keeps useW=slotW anyway).
            }
            switch (VerticalAlignment)
            {
                case VerticalAlignment.Center:    finalY = slotY + (slotH - useH) / 2; break;
                case VerticalAlignment.Bottom:    finalY = slotY + (slotH - useH);     break;
                // Top and Stretch stay at slotY.
            }

            return new Rect(finalX, finalY, useW, useH);
        }

        protected override void ArrangeCore(Rect finalRect)
        {
            // finalRect is the placed-and-sized rect from ResolveArrangeRect.
            // Hand it as a size to ArrangeOverride for laying out our children.
            Size finalSize = ArrangeOverride(new Size(finalRect.Width, finalRect.Height));
            ActualWidth = finalSize.Width;
            ActualHeight = finalSize.Height;
        }

        protected virtual Size MeasureOverride(Size availableSize) => Size.Empty;

        protected virtual Size ArrangeOverride(Size finalSize) => finalSize;
    }
}
