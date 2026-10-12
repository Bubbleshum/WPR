namespace WPR.Wp8Native
{
    /// <summary>
    /// The one screen model every WP8 answer about size derives from: physical pixels plus a
    /// resolution scale, from which the logical (DIP) size follows.
    /// </summary>
    /// <remarks>
    /// <para>A WP8 phone is portrait-native. On every device the CoreWindow is about 480 DIPs wide;
    /// what changes is how many pixels each DIP covers, which the device reports as
    /// <c>DisplayProperties.ResolutionScale</c>:</para>
    /// <list type="table">
    /// <item><c>wvga</c> - 480x800, scale 100 (Lumia 520/620/820).</item>
    /// <item><c>wxga</c> - 768x1280, scale 160 (Lumia 920/1020). <b>The default.</b></item>
    /// <item><c>720p</c> - 720x1280, scale 150 (HTC 8X).</item>
    /// </list>
    /// <para>So on WXGA <c>CoreWindow.Bounds</c> is still 480x800 and pointer positions are in that
    /// space, while the swap chain, the DXGI output and a DrawingSurface's native and render
    /// resolutions are 768x1280 - games keep their layout and render 1.6x the pixels. Every WP8
    /// title had to support it (the store certified against all three), and several pick a larger
    /// asset set from the scale or the back-buffer width.</para>
    /// <para>The host composes landscape - the phone turned a quarter - so the raster the host
    /// draws into (<see cref="CompositionWidth"/> x <see cref="CompositionHeight"/>) is the
    /// physical size transposed.</para>
    /// <para>Overrides: <c>WPR_WP8_RESOLUTION=wvga|wxga|720p</c> picks a device; <c>WPR_WINDOW=WxH</c>
    /// replaces the physical size, <c>WPR_RESSCALE=N</c> the scale and <c>WPR_SCREEN=WxH</c> the
    /// composition size, each on top of that. An override of the physical size alone keeps the
    /// scale, so <c>WPR_WINDOW=480x800</c> with no scale is a 300x500 DIP window: name both, or
    /// use <c>WPR_WP8_RESOLUTION</c>.</para>
    /// </remarks>
    public static class Wp8Display
    {
        /// <summary>The device's portrait width in physical pixels.</summary>
        public static int PhysicalWidth { get; }

        /// <summary>The device's portrait height in physical pixels.</summary>
        public static int PhysicalHeight { get; }

        /// <summary>DisplayProperties.ResolutionScale, as a percentage: 100, 150 or 160.</summary>
        public static int Scale { get; }

        /// <summary>The landscape raster width the host composes into.</summary>
        public static int CompositionWidth { get; }

        /// <summary>The landscape raster height the host composes into.</summary>
        public static int CompositionHeight { get; }

        /// <summary>Which preset was chosen, for the log.</summary>
        public static string Name { get; }

        /// <summary>CoreWindow width in DIPs: physical * 100 / scale.</summary>
        public static float LogicalWidth => PhysicalWidth * 100f / Scale;

        /// <summary>CoreWindow height in DIPs.</summary>
        public static float LogicalHeight => PhysicalHeight * 100f / Scale;

        /// <summary>DisplayProperties.LogicalDpi: 96 at scale 100, 153.6 on WXGA.</summary>
        public static float LogicalDpi => 96f * Scale / 100f;

        /// <summary>
        /// The ResolutionScale enum value. Scale100Percent is 100, Scale150Percent 150,
        /// Scale160Percent 160: the enum's values are the percentages themselves.
        /// </summary>
        public static uint ResolutionScaleEnum => (uint)Scale;

        static Wp8Display()
        {
            (Name, PhysicalWidth, PhysicalHeight, Scale) = Preset(Environment.GetEnvironmentVariable("WPR_WP8_RESOLUTION"));

            if (TryParseSize(Environment.GetEnvironmentVariable("WPR_WINDOW"), out int width, out int height))
            {
                (PhysicalWidth, PhysicalHeight) = (width, height);
                Name += $"+window {width}x{height}";
            }

            if (int.TryParse(Environment.GetEnvironmentVariable("WPR_RESSCALE"), out int scale) && scale is > 0 and <= 400)
            {
                Scale = scale;
                Name += $"+scale {scale}";
            }

            (CompositionWidth, CompositionHeight) = TryParseSize(Environment.GetEnvironmentVariable("WPR_SCREEN"), out int sw, out int sh)
                ? (sw, sh)
                : (PhysicalHeight, PhysicalWidth);
        }

        /// <summary>A named device: (name, portrait width, portrait height, scale).</summary>
        public static (string Name, int Width, int Height, int Scale) Preset(string? name)
            => (name ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "wvga" or "480x800" or "100" => ("wvga", 480, 800, 100),
                "720p" or "720x1280" or "150" => ("720p", 720, 1280, 150),
                _ => ("wxga", 768, 1280, 160),
            };

        /// <summary>A one-line description for the session log.</summary>
        public static string Describe()
            => $"{Name}: {PhysicalWidth}x{PhysicalHeight} px at scale {Scale} -> {LogicalWidth:0.##}x{LogicalHeight:0.##} DIPs, " +
               $"dpi {LogicalDpi:0.##}, composed {CompositionWidth}x{CompositionHeight}";

        private static bool TryParseSize(string? text, out int width, out int height)
        {
            width = height = 0;
            string[] parts = text?.Split('x', 'X') ?? [];
            return parts.Length == 2 &&
                   int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) &&
                   width > 0 && height > 0 && width <= 4096 && height <= 4096;
        }
    }
}
