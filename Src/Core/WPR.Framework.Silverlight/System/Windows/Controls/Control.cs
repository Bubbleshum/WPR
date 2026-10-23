using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Minimal stand-in for a Control base — the WP TextBox derives from Control, but
    /// our framework collapses Control to FrameworkElement since most of Control's chrome
    /// (template, focus visual) isn't relevant on WPR.</summary>
    public class Control : FrameworkElement
    {
        // Alias of FrameworkElement.BackgroundProperty so user IL calling our
        // Control.set_Background on a non-Control instance (Minesweeper's
        // Panorama IL does exactly that, inheriting the pre-patch Silverlight
        // Panorama:Control chain) writes to the same DP slot that the
        // renderer reads via Panel.Background. The static field stays so
        // ldsfld Control::BackgroundProperty still resolves; Background is
        // the FE-inherited CLR property.
        public static readonly DependencyProperty BackgroundProperty = FrameworkElement.BackgroundProperty;

        public static readonly DependencyProperty BorderBrushProperty =
            DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(Control),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty BorderThicknessProperty =
            DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(Control),
                new PropertyMetadata(new Thickness(0)));

        public static readonly DependencyProperty PaddingProperty =
            DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(Control),
                new PropertyMetadata(new Thickness(0)));

        // Real WP/Silverlight expose IsEnabled on Control (it bubbles
        // visually through templates as state-group activation). Our renderer
        // doesn't honour it visually yet, but games still read/write it from
        // code (Minesweeper.HelpOptionsPage.RefreshSettings toggles per-option
        // buttons by IsEnabled). Without the DP, the user-IL `set_IsEnabled`
        // call raises MissingMethodException at the first call site, taking
        // down navigation entirely.
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.Register(nameof(IsEnabled), typeof(bool), typeof(Control),
                new PropertyMetadata((object)true, OnIsEnabledChanged));

        // The control's template. WPR never applies it (see ControlTemplate), but the renderers
        // need to know one was given: a Button with its own template looks nothing like the
        // default WP7 button chrome they synthesise, so drawing that chrome over it boxes every
        // image button in a white frame (Galactic Reign's and Carcassonne's menus). Set by
        // Template="{StaticResource …}" and by a Style's <Setter Property="Template">.
        public static readonly DependencyProperty TemplateProperty =
            DependencyProperty.Register(nameof(Template), typeof(ControlTemplate), typeof(Control),
                new PropertyMetadata((object?)null));

        public ControlTemplate? Template
        {
            get => (ControlTemplate?)GetValue(TemplateProperty);
            set => SetValue(TemplateProperty, value);
        }

        // Background is inherited from FrameworkElement — no shadow here.

        public Brush? BorderBrush
        {
            get => (Brush?)GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        public Thickness BorderThickness
        {
            get => (Thickness)GetValue(BorderThicknessProperty)!;
            set => SetValue(BorderThicknessProperty, value);
        }

        public Thickness Padding
        {
            get => (Thickness)GetValue(PaddingProperty)!;
            set => SetValue(PaddingProperty, value);
        }

        public bool IsEnabled
        {
            get => (bool)GetValue(IsEnabledProperty)!;
            set => SetValue(IsEnabledProperty, value);
        }

        // The handlers live in the property store, not an event field: the toolkit's MenuItem
        // subscribes from its constructor through Control::add_IsEnabledChanged, and its base chain
        // here is HeaderedItemsControl -> ItemsControl -> StackPanel, which has no Control fields.
        // A field event wrote its delegate over a field of the panel (its children list), and the
        // next child added threw ArrayTypeMismatchException: no Fox News row's context menu built.
        private static readonly DependencyProperty IsEnabledChangedHandlersProperty =
            DependencyProperty.Register("WprIsEnabledChanged", typeof(DependencyPropertyChangedEventHandler), typeof(Control),
                new PropertyMetadata((object?)null));

        /// <summary>Raised when <see cref="IsEnabled"/> changes.</summary>
        public event DependencyPropertyChangedEventHandler? IsEnabledChanged
        {
            add => SetValue(IsEnabledChangedHandlersProperty,
                (DependencyPropertyChangedEventHandler?)Delegate.Combine(GetValue(IsEnabledChangedHandlersProperty) as Delegate, value));
            remove => SetValue(IsEnabledChangedHandlersProperty,
                (DependencyPropertyChangedEventHandler?)Delegate.Remove(GetValue(IsEnabledChangedHandlersProperty) as Delegate, value));
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d.GetValue(IsEnabledChangedHandlersProperty) as DependencyPropertyChangedEventHandler)?.Invoke(d, e);
        }

        public static readonly DependencyProperty HorizontalContentAlignmentProperty =
            DependencyProperty.Register(nameof(HorizontalContentAlignment), typeof(HorizontalAlignment), typeof(Control),
                new PropertyMetadata((object)HorizontalAlignment.Center));

        public static readonly DependencyProperty VerticalContentAlignmentProperty =
            DependencyProperty.Register(nameof(VerticalContentAlignment), typeof(VerticalAlignment), typeof(Control),
                new PropertyMetadata((object)VerticalAlignment.Center));

        /// <summary>Stored; the renderers place content by the content's own alignment.</summary>
        public HorizontalAlignment HorizontalContentAlignment
        {
            get => (HorizontalAlignment)GetValue(HorizontalContentAlignmentProperty)!;
            set => SetValue(HorizontalContentAlignmentProperty, value);
        }

        /// <summary>Stored; the renderers place content by the content's own alignment.</summary>
        public VerticalAlignment VerticalContentAlignment
        {
            get => (VerticalAlignment)GetValue(VerticalContentAlignmentProperty)!;
            set => SetValue(VerticalContentAlignmentProperty, value);
        }

        /// <summary>Gives the control focus. WPR has no keyboard focus; the answer is whether it could have taken it.</summary>
        public bool Focus() => IsEnabled && Visibility == Visibility.Visible;

        // Kept in the property store, not a field: like DefaultStyleKey below, these two are
        // called with a `this` whose patched base chain skips Control (an ItemsControl here is a
        // StackPanel), so they may touch only what every DependencyObject has.
        private static readonly DependencyProperty TemplateAppliedProperty =
            DependencyProperty.Register("WprTemplateApplied", typeof(bool), typeof(Control),
                new PropertyMetadata((object)false));

        /// <summary>
        /// Templates are not applied (see <see cref="ControlTemplate"/>), but a control that builds
        /// itself in <see cref="FrameworkElement.OnApplyTemplate"/> still needs that call, once.
        /// </summary>
        public bool ApplyTemplate()
        {
            if (GetValue(TemplateAppliedProperty) is true) return false;
            SetValue(TemplateAppliedProperty, true);
            BuildTemplateRoot(this);
            OnApplyTemplate();
            return true;
        }

        // Builds the template (the control's own, else its default style's) as the tree
        // GetTemplateChild searches. Not drawn: see ApplyTemplate.
        private static void BuildTemplateRoot(FrameworkElement control)
        {
            if (control.GetValue(TemplateRootProperty) != null) return;
            ControlTemplate? template = control.GetValue(TemplateProperty) as ControlTemplate
                ?? DefaultStyles.FindTemplate(control.GetValue(DefaultStyleKeyProperty) as Type ?? control.GetType());
            if (template == null) return;
            try { control.SetValue(TemplateRootProperty, template.LoadContent()); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-template] {control.GetType().Name}: template did not build ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// Silverlight applies a control's template before its first measure, and so before
        /// Loaded, and the control's <c>OnApplyTemplate</c> takes its named parts there. Here that
        /// happens for a control whose <c>OnApplyTemplate</c> is the app's or a library's it
        /// ships (WPR's own controls build themselves): the template, its own or its default
        /// style's, is built and the override runs, once.
        /// </summary>
        /// <remarks>
        /// The toolkit's Pivot is why: it raises LoadedPivotItem (where Fox News starts loading a
        /// feed) only from the page-change animation it runs on its PivotItemPresenter part, and
        /// with no part it never raised it. The tree is still not drawn; WpChrome draws a Pivot.
        /// </remarks>
        internal static void EnsureTemplateApplied(FrameworkElement element)
        {
            if (!HasOwnApplyTemplate(element.GetType())) return;
            if (!BeginApplyTemplate(element)) return;
            BuildTemplateRoot(element);
            try { element.OnApplyTemplate(); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-template] {element.GetType().Name}.OnApplyTemplate threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, object> OwnApplyTemplate = new();

        private static bool HasOwnApplyTemplate(Type type)
        {
            if (OwnApplyTemplate.TryGetValue(type, out object? cached)) return (bool)cached;
            System.Reflection.MethodInfo? method = type.GetMethod(nameof(OnApplyTemplate),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, Type.EmptyTypes);
            string? owner = method?.DeclaringType?.Assembly.GetName().Name;
            bool own = owner != null && owner != "Microsoft.Phone" && !owner.StartsWith("WPR.", StringComparison.Ordinal);
            OwnApplyTemplate.AddOrUpdate(type, own);
            return own;
        }

        // The tree ApplyTemplate built from the template. It is not drawn: it exists so a control
        // that asks for its named parts (Arkadium's UniformGrid wants its ItemsPresenter) gets them.
        private static readonly DependencyProperty TemplateRootProperty =
            DependencyProperty.Register("WprTemplateRoot", typeof(UIElement), typeof(Control),
                new PropertyMetadata((object?)null));

        /// <summary>Gives a control whose visual tree WPR builds itself (ListBox) its template root,
        /// so <see cref="GetTemplateChild"/> finds its parts.</summary>
        internal static void SetBuiltInTemplateRoot(DependencyObject control, UIElement root) =>
            control.SetValue(TemplateRootProperty, root);

        /// <summary>Marks the template applied; true the first time, as Silverlight applies a
        /// template once before the control's first measure.</summary>
        internal static bool BeginApplyTemplate(DependencyObject control)
        {
            if (control.GetValue(TemplateAppliedProperty) is true) return false;
            control.SetValue(TemplateAppliedProperty, true);
            return true;
        }

        /// <summary>
        /// A named part of the template <see cref="ApplyTemplate"/> built, or null. Public rather
        /// than protected for the reason above: a subclass that is not a Control here fails the
        /// access check.
        /// </summary>
        public DependencyObject? GetTemplateChild(string childName) => FindTemplateChild(this, childName);

        /// <summary><see cref="GetTemplateChild"/> for any control, including the ones whose base
        /// chain here is not Control (a ListBox is a StackPanel).</summary>
        internal static DependencyObject? FindTemplateChild(DependencyObject control, string childName) =>
            control.GetValue(TemplateRootProperty) is UIElement root ? FindNamed(root, childName) : null;

        private static DependencyObject? FindNamed(DependencyObject node, string name)
        {
            if (node is FrameworkElement fe && fe.Name == name) return fe;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(node, i) is DependencyObject child
                    && FindNamed(child, name) is DependencyObject found) return found;
            }
            return null;
        }

        // Silverlight's templated-control plumbing: every WP toolkit control sets
        // DefaultStyleKey = typeof(self) in its ctor, and the key names the implicit style in
        // themes/generic.xaml whose Template EnsureTemplateApplied builds.
        //
        // Kept in the property store, never a field: the patched WP Toolkit IL emits
        // `call Control::set_DefaultStyleKey` with `this` of types (Panorama, Pivot, …) whose
        // post-patch base chain bypasses Control (TemplatedItemsControl<T> → ItemsControl →
        // StackPanel → Panel). A field on this object would be memory the object does not have;
        // the store is on every DependencyObject.
        private static readonly DependencyProperty DefaultStyleKeyProperty =
            DependencyProperty.Register("WprDefaultStyleKey", typeof(object), typeof(Control),
                new PropertyMetadata((object?)null));

        public object? DefaultStyleKey
        {
            get => GetValue(DefaultStyleKeyProperty);
            set => SetValue(DefaultStyleKeyProperty, value);
        }
    }
}
