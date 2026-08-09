using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Default Windows Phone 7 dark-theme resource set. Real WP7 ships these as
    /// <c>StaticResource</c>s under <c>Application.Resources</c> via the system
    /// theme XAML; user XAML references them by name (<c>{StaticResource PhoneForegroundBrush}</c>,
    /// <c>{StaticResource PhoneFontSizeNormal}</c>, etc.). Without these, every
    /// <c>{StaticResource …}</c> in user XAML resolves to null and text comes out
    /// as black-on-black (or invisible).
    ///
    /// We populate just the visually-relevant subset — colors, brushes, font sizes,
    /// and a couple of font families. Styles (PhoneTextLargeStyle, …) aren't
    /// represented faithfully yet; the renderer just inherits FrameworkElement
    /// defaults when a Style lookup misses.
    /// </summary>
    public static class PhoneTheme
    {
        /// <summary>True if <see cref="Apply"/> has run for the current process.</summary>
        public static bool Applied { get; private set; }

        /// <summary>
        /// Optional override for the WP7 accent color used in
        /// <c>PhoneAccentColor</c>/<c>PhoneAccentBrush</c>/<c>PhoneTextAccentStyle</c>.
        /// The host sets this from user configuration before constructing
        /// <c>WPR.WindowsCompability.Application</c> (which calls <see cref="Apply"/>);
        /// leave null to keep the WP7 default <c>#1BA1E2</c> ("Cyan").
        /// </summary>
        public static Color? AccentColorOverride { get; set; }

        /// <summary>
        /// Merge the default phone theme into the given dictionary if it doesn't
        /// already define each key (so user App.xaml overrides win).
        /// </summary>
        public static void Apply(WPR.WindowsCompability.ResourceDictionary target)
        {
            foreach (var kv in BuildDefaults())
            {
                if (!target.ContainsKey(kv.Key))
                    target[kv.Key] = kv.Value;
            }
            Applied = true;
        }

        private static IEnumerable<KeyValuePair<string, object?>> BuildDefaults()
        {
            // --- Colors -----------------------------------------------------
            var fg            = Color.FromRgb(0xFF, 0xFF, 0xFF);   // white
            var bg            = Color.FromRgb(0x00, 0x00, 0x00);   // black
            var accent        = AccentColorOverride
                                ?? Color.FromRgb(0x1B, 0xA1, 0xE2); // WP7 default blue
            var chrome        = Color.FromRgb(0x1F, 0x1F, 0x1F);   // panel chrome
            var subtle        = Color.FromRgb(0x7F, 0x7F, 0x7F);   // disabled text
            var disabled      = Color.FromRgb(0x40, 0x40, 0x40);
            var contrast      = Color.FromRgb(0x00, 0x00, 0x00);
            var border        = Color.FromRgb(0xFF, 0xFF, 0xFF);
            var inactive      = Color.FromRgb(0x7F, 0x7F, 0x7F);

            yield return P("PhoneForegroundColor",      fg);
            yield return P("PhoneBackgroundColor",      bg);
            yield return P("PhoneAccentColor",          accent);
            yield return P("PhoneChromeColor",          chrome);
            yield return P("PhoneSubtleColor",          subtle);
            yield return P("PhoneDisabledColor",        disabled);
            yield return P("PhoneContrastBackgroundColor", contrast);
            yield return P("PhoneContrastForegroundColor", fg);
            yield return P("PhoneBorderColor",          border);
            yield return P("PhoneInactiveColor",        inactive);

            // --- Brushes (named SolidColorBrush) ----------------------------
            yield return P("PhoneForegroundBrush",      new SolidColorBrush(fg));
            yield return P("PhoneBackgroundBrush",      new SolidColorBrush(bg));
            yield return P("PhoneAccentBrush",          new SolidColorBrush(accent));
            yield return P("PhoneChromeBrush",          new SolidColorBrush(chrome));
            yield return P("PhoneSubtleBrush",          new SolidColorBrush(subtle));
            yield return P("PhoneDisabledBrush",        new SolidColorBrush(disabled));
            yield return P("PhoneContrastForegroundBrush", new SolidColorBrush(fg));
            yield return P("PhoneContrastBackgroundBrush", new SolidColorBrush(contrast));
            yield return P("PhoneBorderBrush",          new SolidColorBrush(border));
            yield return P("PhoneInactiveBrush",        new SolidColorBrush(inactive));
            yield return P("TransparentBrush",          new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)));

            // --- Font sizes -------------------------------------------------
            yield return P("PhoneFontSizeSmall",        18.667);   // PT 14
            yield return P("PhoneFontSizeNormal",       20.0);     // PT 15
            yield return P("PhoneFontSizeMedium",       22.667);   // PT 17
            yield return P("PhoneFontSizeMediumLarge",  25.333);   // PT 19
            yield return P("PhoneFontSizeLarge",        32.0);     // PT 24
            yield return P("PhoneFontSizeExtraLarge",   42.667);   // PT 32
            yield return P("PhoneFontSizeExtraExtraLarge", 54.667);// PT 41
            yield return P("PhoneFontSizeHuge",         72.0);     // PT 54

            // --- Font families ----------------------------------------------
            yield return P("PhoneFontFamilyNormal",     "Segoe WP");
            yield return P("PhoneFontFamilyLight",      "Segoe WP Light");
            yield return P("PhoneFontFamilySemiLight",  "Segoe WP SemiLight");
            yield return P("PhoneFontFamilySemiBold",   "Segoe WP SemiBold");
            yield return P("PhoneFontFamilyBold",       "Segoe WP Bold");

            // --- Misc geometry ----------------------------------------------
            yield return P("PhoneMargin",               new Thickness(12));
            yield return P("PhoneTouchTargetOverhang",  new Thickness(12));
            yield return P("PhoneHorizontalMargin",     new Thickness(12, 0, 12, 0));
            yield return P("PhoneVerticalMargin",       new Thickness(0, 12, 0, 12));
            yield return P("PhoneBorderThickness",      3.0);
            yield return P("PhoneStrokeThickness",      3.0);
            yield return P("PhoneTextBlockMargin",      new Thickness(0, 0, 0, 4));
            yield return P("PhoneTouchTargetLargeOverhang", new Thickness(12, 20, 12, 20));

            // --- Text styles ------------------------------------------------
            // The WP7 typography stack. Real WP ships these as <Style>s with
            // <Setter Property="..." Value="..."/> children that XAML resolves.
            // We build them in code so every TextBlock with Style="{StaticResource PhoneTextXxxStyle}"
            // picks up correct font size / family / brush instead of falling back
            // to FrameworkElement defaults (14pt, default brush).
            yield return P("PhoneTextNormalStyle",      BuildTextStyle(20.0,      family: "Segoe WP"));
            yield return P("PhoneTextSubtleStyle",      BuildTextStyle(20.0,      family: "Segoe WP",
                                                                        foreground: new SolidColorBrush(subtle)));
            yield return P("PhoneTextSmallStyle",       BuildTextStyle(18.667,    family: "Segoe WP"));
            yield return P("PhoneTextLargeStyle",       BuildTextStyle(32.0,      family: "Segoe WP Light"));
            yield return P("PhoneTextExtraLargeStyle",  BuildTextStyle(42.667,    family: "Segoe WP Light"));
            yield return P("PhoneTextHugeStyle",        BuildTextStyle(72.0,      family: "Segoe WP Light"));
            yield return P("PhoneTextTitle1Style",      BuildTextStyle(72.0,      family: "Segoe WP Light"));
            yield return P("PhoneTextTitle2Style",      BuildTextStyle(42.667,    family: "Segoe WP Light"));
            yield return P("PhoneTextTitle3Style",      BuildTextStyle(24.0,      family: "Segoe WP"));
            yield return P("PhoneTextContrastStyle",    BuildTextStyle(20.0,      family: "Segoe WP",
                                                                        foreground: new SolidColorBrush(contrast)));
            yield return P("PhoneTextAccentStyle",      BuildTextStyle(20.0,      family: "Segoe WP",
                                                                        foreground: new SolidColorBrush(accent)));
            yield return P("PhoneTextGroupHeaderStyle", BuildTextStyle(24.0,      family: "Segoe WP",
                                                                        foreground: new SolidColorBrush(subtle)));
            yield return P("PhoneTextSmallSubtleStyle", BuildTextStyle(18.667,    family: "Segoe WP",
                                                                        foreground: new SolidColorBrush(subtle)));

            // Panorama-specific styles — used as Style="{StaticResource ...}"
            // in user XAML; we provide empty styles so the assignment doesn't
            // null-out anything else. Sub-styles below are how WP toolkit games
            // typically theme their panorama.
            yield return P("DarkThemePanoramaStyle",    new Style { TargetType = null });
            yield return P("DarkThemePanoramaItemStyle", new Style { TargetType = null });

            // Common button style placeholder.
            yield return P("ButtonStyleLight",          BuildTextStyle(20.0,      family: "Segoe WP",
                                                                        foreground: new SolidColorBrush(fg)));
        }

        /// <summary>Build a Style whose Setters set FontSize, FontFamily,
        /// optionally Foreground. <see cref="Style.TargetType"/> is left null
        /// because our Style.Apply doesn't restrict by type — the named DP
        /// (FontSize, etc.) is looked up directly on the target element.</summary>
        private static Style BuildTextStyle(double fontSize, string? family = null, Brush? foreground = null)
        {
            var s = new Style { TargetType = null };
            s.Setters.Add(new Setter("FontSize", fontSize));
            if (family != null) s.Setters.Add(new Setter("FontFamily", family));
            if (foreground != null) s.Setters.Add(new Setter("Foreground", foreground));
            return s;
        }

        private static KeyValuePair<string, object?> P(string key, object? value)
            => new KeyValuePair<string, object?>(key, value);
    }
}
