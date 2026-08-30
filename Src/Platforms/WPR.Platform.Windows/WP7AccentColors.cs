using System.Collections.Generic;
using Avalonia.Media;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// The fixed set of accent colors WP7's "theme" Settings screen offered. The
    /// hex values match the published WP7/WP8 system palette, with "Cyan"
    /// (#1BA1E2) being the device default. Used by the desktop SettingsPage's
    /// highlight-color picker.
    /// </summary>
    public sealed class WP7AccentColor
    {
        /// <summary>Localized-ish display name shown in the picker.</summary>
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

        public override string ToString() => Name;
    }

    public static class WP7AccentColors
    {
        public static IReadOnlyList<WP7AccentColor> Presets { get; } = new[]
        {
            new WP7AccentColor("Lime",     "#FFA4C400"),
            new WP7AccentColor("Green",    "#FF60A917"),
            new WP7AccentColor("Emerald",  "#FF008A00"),
            new WP7AccentColor("Teal",     "#FF00ABA9"),
            new WP7AccentColor("Cyan",     "#FF1BA1E2"), // WP7 default
            new WP7AccentColor("Cobalt",   "#FF0050EF"),
            new WP7AccentColor("Indigo",   "#FF6A00FF"),
            new WP7AccentColor("Violet",   "#FFAA00FF"),
            new WP7AccentColor("Pink",     "#FFF472D0"),
            new WP7AccentColor("Magenta",  "#FFD80073"),
            new WP7AccentColor("Crimson",  "#FFA20025"),
            new WP7AccentColor("Red",      "#FFE51400"),
            new WP7AccentColor("Orange",   "#FFFA6800"),
            new WP7AccentColor("Amber",    "#FFF0A30A"),
            new WP7AccentColor("Yellow",   "#FFE3C800"),
            new WP7AccentColor("Brown",    "#FF825A2C"),
            new WP7AccentColor("Olive",    "#FF6D8764"),
            new WP7AccentColor("Steel",    "#FF647687"),
            new WP7AccentColor("Mauve",    "#FF76608A"),
            new WP7AccentColor("Sienna",   "#FFA0522D"),
        };

        /// <summary>Default accent (Cyan) — used when Configuration.AccentColor is unset.</summary>
        public static WP7AccentColor Default => Presets[4];
    }
}
