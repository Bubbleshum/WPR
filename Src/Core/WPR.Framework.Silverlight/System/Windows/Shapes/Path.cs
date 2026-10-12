using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Shapes.Path</c>.</summary>
    /// <remarks>
    /// <c>Data</c> is a <see cref="Geometry"/>; the XAML mini-language string form
    /// (<c>Data="F1 M 0,0 L 10,10 Z"</c>) is turned into a <see cref="PathGeometry"/> by
    /// <see cref="PathMarkupParser"/> through <c>XamlTypeConverter</c>.
    /// </remarks>
    public class Path : Shape
    {
        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Path),
                new PropertyMetadata((object?)null));

        public Geometry? Data
        {
            get => (Geometry?)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }
    }
}
