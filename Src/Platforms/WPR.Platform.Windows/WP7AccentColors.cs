using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using WPR.Shell;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// The desktop picker's view of the WP7 accent palette: each shared
    /// <see cref="WP7Accent"/> plus the pre-built brush Avalonia needs.
    ///
    /// <para>The twenty name/hex pairs themselves live in
    /// <see cref="WP7AccentPalette"/> and are shared with the Android shell. They used to be
    /// duplicated in both heads, with a comment on the Android copy asking whoever changed one to
    /// remember the other. The brush is the reason the data could not simply be shared as-is —
    /// building it eagerly per entry put <c>Avalonia.Media.IBrush</c> in the type, and the native
    /// Android shell has no Avalonia application to touch it with. Projecting the shared pair into
    /// a brush here keeps the data in one place and the brush where it belongs.</para>
    /// </summary>
    public sealed class WP7AccentColor
    {
        /// <summary>Display name shown in the picker, title-cased for the desktop UI.</summary>
        public string Name { get; }
        /// <summary>Hex string in "#AARRGGBB" form, persisted into Configuration.</summary>
        public string Hex { get; }
        /// <summary>Pre-built solid brush — XAML data templates bind to this for the
        /// swatch background. Binding a string to <c>Border.Background</c> would
        /// rely on a runtime type converter that compiled bindings don't surface.</summary>
        public IBrush Brush { get; }

        public WP7AccentColor(string name, string hex)
        {
            Name = name;
            Hex = hex;
            Brush = new SolidColorBrush(Color.Parse(hex));
        }

        internal WP7AccentColor(WP7Accent accent)
            : this(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(accent.Name), accent.Hex)
        {
        }

        public override string ToString() => Name;
    }

    public static class WP7AccentColors
    {
        /// <summary>
        /// The picker's entries, in WP7's own order. Projected from
        /// <see cref="WP7AccentPalette.Presets"/> — do not re-list the hexes here.
        /// </summary>
        public static IReadOnlyList<WP7AccentColor> Presets { get; } =
            WP7AccentPalette.Presets.Select(a => new WP7AccentColor(a)).ToArray();

        /// <summary>Default accent (Cyan) — used when Configuration.AccentColor is unset.</summary>
        public static WP7AccentColor Default => Resolve(null);

        /// <summary>
        /// Resolves a persisted accent string to a picker entry, falling back to the default for
        /// an unset, unknown or corrupt value. Shares its matching rule with the Android shell via
        /// <see cref="WP7AccentPalette.Resolve"/>, which the two used to implement differently.
        /// </summary>
        public static WP7AccentColor Resolve(string? storedHex)
        {
            WP7Accent accent = WP7AccentPalette.Resolve(storedHex);
            return Presets.First(p => p.Hex == accent.Hex);
        }
    }
}
