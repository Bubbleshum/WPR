using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.Viewbox</c>.</summary>
    /// <remarks>
    /// <para>A single-child container that scales its child to fill the slot. WP7 layouts lean on
    /// it heavily to make one design work at more than one resolution — Carcassonne's main menu
    /// names it 26 times.</para>
    ///
    /// <para><b>It hosts its child; it does not scale it yet.</b> Scaling needs a transform on the
    /// blit, which <c>SoftwareVisualRasteriser</c> does not have (it is axis-aligned and
    /// point-sampled). A child at its natural size in the right place is the useful approximation,
    /// and it is what the WP7 designs this appears in mostly ask for anyway — the common case is a
    /// Viewbox sized close to its content. <b>Until the rasteriser grows transforms, expect a
    /// Viewbox whose slot differs sharply from its content's natural size to render at the wrong
    /// size rather than clipped or missing.</b></para>
    ///
    /// <para>Deriving from <see cref="ContentControl"/> rather than reimplementing a child slot
    /// gets the XAML content property, the presenter and the renderer's existing traversal for
    /// free — the renderers already walk <c>ContentControl.Presenter</c>. <c>Child</c> is exposed
    /// beside <c>Content</c> because that is the name Silverlight's own API and game IL use.</para>
    /// </remarks>
    [ContentProperty(nameof(Child))]
    public class Viewbox : ContentControl
    {
        public static readonly DependencyProperty StretchProperty =
            DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(Viewbox),
                new PropertyMetadata(Stretch.Uniform));

        public static readonly DependencyProperty StretchDirectionProperty =
            DependencyProperty.Register(nameof(StretchDirection), typeof(StretchDirection), typeof(Viewbox),
                new PropertyMetadata(StretchDirection.Both));

        public Stretch Stretch
        {
            get => (Stretch)GetValue(StretchProperty)!;
            set => SetValue(StretchProperty, value);
        }

        public StretchDirection StretchDirection
        {
            get => (StretchDirection)GetValue(StretchDirectionProperty)!;
            set => SetValue(StretchDirectionProperty, value);
        }

        /// <summary>
        /// The hosted element. An alias of <see cref="ContentControl.Content"/>, which is where the
        /// XAML reader and both renderers already look.
        /// </summary>
        public UIElement? Child
        {
            get => Content as UIElement;
            set => Content = value;
        }
    }

    /// <summary>Shim for <c>System.Windows.Controls.StretchDirection</c>.</summary>
    public enum StretchDirection
    {
        UpOnly,
        DownOnly,
        Both,
    }
}
