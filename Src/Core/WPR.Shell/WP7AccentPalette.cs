using System;
using System.Collections.Generic;

namespace WPR.Shell
{
    /// <summary>
    /// The twenty system accent colours Windows Phone 7's theme picker offered, in the order it
    /// offered them. Persisted into <c>Configuration.AccentColor</c> as the <see cref="Hex"/>
    /// string, so this list is also the set of legal values for that setting.
    ///
    /// <para>Shared 2026-09-02. Both shells carried the same twenty name/hex pairs, and the Android
    /// copy's own comment said "if one ever changes, change it in both places" — which is the
    /// clearest possible sign the data wanted a home. The reason it had not been shared was that
    /// the desktop type eagerly built an <c>Avalonia.Media.IBrush</c> per entry, and the native
    /// Android shell must never touch Avalonia's type system. The fix is simply not to put the
    /// brush here: a shell projects these pairs into whatever paint type it uses.</para>
    /// </summary>
    public readonly struct WP7Accent
    {
        public WP7Accent(string name, string hex)
        {
            Name = name;
            Hex = hex;
        }

        /// <summary>Display name, lowercase, as WP7 spelled it in Settings.</summary>
        public string Name { get; }

        /// <summary>"#AARRGGBB" — the exact form <c>Configuration.AccentColor</c> persists.</summary>
        public string Hex { get; }

        public override string ToString() => Name;
    }

    /// <summary>The WP7 accent palette. See <see cref="WP7Accent"/>.</summary>
    public static class WP7AccentPalette
    {
        /// <summary>WP7's device default, "cyan". Also mirrored in Android's Resources/values/colors.xml.</summary>
        public const string DefaultHex = "#FF1BA1E2";

        public static IReadOnlyList<WP7Accent> Presets { get; } = new[]
        {
            new WP7Accent("lime",    "#FFA4C400"),
            new WP7Accent("green",   "#FF60A917"),
            new WP7Accent("emerald", "#FF008A00"),
            new WP7Accent("teal",    "#FF00ABA9"),
            new WP7Accent("cyan",    DefaultHex),
            new WP7Accent("cobalt",  "#FF0050EF"),
            new WP7Accent("indigo",  "#FF6A00FF"),
            new WP7Accent("violet",  "#FFAA00FF"),
            new WP7Accent("pink",    "#FFF472D0"),
            new WP7Accent("magenta", "#FFD80073"),
            new WP7Accent("crimson", "#FFA20025"),
            new WP7Accent("red",     "#FFE51400"),
            new WP7Accent("orange",  "#FFFA6800"),
            new WP7Accent("amber",   "#FFF0A30A"),
            new WP7Accent("yellow",  "#FFE3C800"),
            new WP7Accent("brown",   "#FF825A2C"),
            new WP7Accent("olive",   "#FF6D8764"),
            new WP7Accent("steel",   "#FF647687"),
            new WP7Accent("mauve",   "#FF76608A"),
            new WP7Accent("sienna",  "#FFA0522D"),
        };

        /// <summary>The default accent ("cyan"), used when <c>Configuration.AccentColor</c> is unset.</summary>
        public static WP7Accent Default => Presets[4];

        /// <summary>
        /// Resolves a persisted accent string to a preset, falling back to <see cref="Default"/>
        /// for an unset, unknown or corrupt value.
        ///
        /// <para>Centralised because the two shells disagreed here: one matched against the preset
        /// list and silently left a corrupt value in Configuration while showing the default, the
        /// other re-parsed the hex on every read. Neither wrote the fallback back, so a bad value
        /// survived forever — a caller that wants to repair it should persist
        /// <c>Resolve(stored).Hex</c>.</para>
        /// </summary>
        public static WP7Accent Resolve(string? storedHex)
        {
            if (string.IsNullOrWhiteSpace(storedHex)) return Default;

            foreach (WP7Accent accent in Presets)
            {
                if (string.Equals(accent.Hex, storedHex, StringComparison.OrdinalIgnoreCase))
                {
                    return accent;
                }
            }

            return Default;
        }
    }
}
