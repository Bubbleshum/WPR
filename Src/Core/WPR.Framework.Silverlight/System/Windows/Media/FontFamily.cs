using System;

namespace System.Windows.Media
{
    /// <summary>Shim for <c>System.Windows.Media.FontFamily</c>.</summary>
    /// <remarks>
    /// A name, or a font file in the package plus a family (<c>/Fonts/x.ttf#Family</c>). The
    /// rasteriser draws every family with the system font, so only <see cref="Source"/> is kept.
    /// The <c>FontFamily</c> properties of TextBlock, TextBox and FrameworkElement were strings
    /// here, so game IL calling <c>set_FontFamily(FontFamily)</c> (Microsoft Bingo's card numbers,
    /// Microsoft's ad control) did not resolve, and <c>&lt;FontFamily x:Key&gt;</c> resources could
    /// not be built.
    /// </remarks>
    public class FontFamily
    {
        public FontFamily(string familyName)
        {
            Source = familyName ?? throw new ArgumentNullException(nameof(familyName));
        }

        public string Source { get; }

        public override string ToString() => Source;

        public override bool Equals(object? obj) => obj is FontFamily other && other.Source == Source;

        public override int GetHashCode() => Source.GetHashCode();
    }
}
