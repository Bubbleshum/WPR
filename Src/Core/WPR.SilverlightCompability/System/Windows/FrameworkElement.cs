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

        // SizeChanged / LayoutUpdated: WP Toolkit controls (Panorama) subscribe to these.
        // Our renderer doesn't currently raise them; declared for ABI compatibility.
#pragma warning disable CS0067
        public event SizeChangedEventHandler? SizeChanged;
        public event EventHandler? LayoutUpdated;
#pragma warning restore CS0067

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

        protected internal void RaiseLoaded() => Loaded?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
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

        private static System.Collections.Generic.IEnumerable<UIElement> EnumerateLogicalChildren(UIElement el)
        {
            // Order matters: more-specific subclasses first. ScrollViewer derives
            // from ContentControl so it'd be caught by the ContentControl arm
            // (which already covers Content), so we don't list it separately.
            switch (el)
            {
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
