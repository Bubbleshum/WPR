using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Single-content host. <see cref="Content"/> is typed as <c>object?</c> — a UIElement
    /// is hosted directly; any other value is wrapped in a TextBlock at render time.
    /// </summary>
    [ContentProperty(nameof(Content))]
    public class ContentControl : FrameworkElement
    {
        public static readonly DependencyProperty ContentProperty =
            DependencyProperty.Register(nameof(Content), typeof(object), typeof(ContentControl),
                new PropertyMetadata((object?)null, OnContentChanged));

        // Alias of FrameworkElement.BackgroundProperty — see FrameworkElement.cs
        // for why every Background field shares one DP slot. The CLR Background
        // property is inherited from FrameworkElement.
        public static readonly DependencyProperty BackgroundProperty = FrameworkElement.BackgroundProperty;

        public object? Content
        {
            get => GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        private UIElement? _presenter;

        /// <summary>The UIElement actually placed in the visual tree for the current Content.</summary>
        internal UIElement? Presenter => _presenter ??= BuildPresenter(Content);

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ContentControl cc)
            {
                cc._presenter?.SetParent(null);
                cc._presenter = cc.BuildPresenter(e.NewValue);
                cc._presenter?.SetParent(cc);
                cc.InvalidateMeasure();
            }
        }

        private UIElement? BuildPresenter(object? content)
        {
            if (content == null) return null;
            if (content is UIElement el) return el;
            return new TextBlock { Text = content.ToString() ?? string.Empty };
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            UIElement? p = Presenter;
            Size ds = Size.Empty;
            if (p != null)
            {
                p.Measure(availableSize);
                ds = p.DesiredSize;
            }
            // Some heavily-templated toolkit controls deriving from
            // ContentControl rely on their ControlTemplate to set the layout
            // size (header + slider + state text for a ToggleSwitch, for
            // instance). We never apply the template, so without a forced
            // minimum size StackPanel would arrange these at bare-Content
            // height and the renderer's drawn chrome would overlap siblings.
            // Bake in the known minimums here so the override-free templated
            // types still measure correctly.
            string typeName = GetType().FullName ?? "";
            if (typeName == "Microsoft.Phone.Controls.ToggleSwitch")
            {
                // Header (24 DIP) + 4 DIP gap + 38 DIP track + 12 DIP bottom margin = 78 DIP.
                // Width = track (95) + 12 DIP gap + state word (~60 DIP) = ~170 DIP.
                double w = Math.Max(ds.Width, 180);
                double h = Math.Max(ds.Height, 86);
                if (!double.IsInfinity(availableSize.Width) && w > availableSize.Width)
                    w = availableSize.Width;
                return new Size(w, h);
            }
            return ds;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Presenter?.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }

        public override object? FindName(string name)
        {
            if (Name == name) return this;
            if (Presenter is FrameworkElement fe) return fe.FindName(name);
            return null;
        }
    }
}
