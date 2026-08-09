namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Colors</c>. Silverlight / WP7 exposed
    /// a fixed set of named colours here (Black, White, Red, ...). Game code
    /// typically writes <c>new SolidColorBrush(Colors.Red)</c>, so we expose
    /// the same named statics. Values are sRGB and match the WPF / Silverlight
    /// definitions.
    /// </summary>
    public static class Colors
    {
        public static Color Black       => Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        public static Color Blue        => Color.FromArgb(0xFF, 0x00, 0x00, 0xFF);
        public static Color Brown       => Color.FromArgb(0xFF, 0xA5, 0x2A, 0x2A);
        public static Color Cyan        => Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF);
        public static Color DarkGray    => Color.FromArgb(0xFF, 0xA9, 0xA9, 0xA9);
        public static Color Gray        => Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
        public static Color Green       => Color.FromArgb(0xFF, 0x00, 0x80, 0x00);
        public static Color LightGray   => Color.FromArgb(0xFF, 0xD3, 0xD3, 0xD3);
        public static Color Magenta     => Color.FromArgb(0xFF, 0xFF, 0x00, 0xFF);
        public static Color Orange      => Color.FromArgb(0xFF, 0xFF, 0xA5, 0x00);
        public static Color Purple      => Color.FromArgb(0xFF, 0x80, 0x00, 0x80);
        public static Color Red         => Color.FromArgb(0xFF, 0xFF, 0x00, 0x00);
        public static Color Transparent => Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        public static Color White       => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        public static Color Yellow      => Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00);
    }
}
