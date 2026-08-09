using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
// This file lives in namespace WPR.SilverlightCompability, which defines its own Color / Point /
// FlowDirection / SolidColorBrush shims that would shadow Avalonia's. Alias the Avalonia drawing
// types explicitly so there's no ambiguity.
using AvDrawingContext = Avalonia.Media.DrawingContext;
using AvRect = Avalonia.Rect;
using AvPoint = Avalonia.Point;
using AvColor = Avalonia.Media.Color;
using AvSolidColorBrush = Avalonia.Media.SolidColorBrush;
using AvGradientBrush = Avalonia.Media.LinearGradientBrush;
using AvGradientStop = Avalonia.Media.GradientStop;
using AvRelativePoint = Avalonia.RelativePoint;
using AvRelativeUnit = Avalonia.RelativeUnit;
using AvTypeface = Avalonia.Media.Typeface;
using AvFormattedText = Avalonia.Media.FormattedText;
using AvFlowDirection = Avalonia.Media.FlowDirection;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// A tasteful branded splash for games whose native engine WPR can't host (e.g. AC Pirates'
    /// native ARM Spark2 engine). Instead of the bare D3D test pattern, it presents the app's own
    /// store artwork — its largest live tile / icon — centered on a dark gradient with a subtle
    /// animated "loading" indicator, so a launched-but-unrunnable title at least looks like a real
    /// game splash rather than an error.
    ///
    /// Pure Avalonia drawing (no D3D), so it works on every backend and captures cleanly. Wired in
    /// via <see cref="DrawingSurfaceBackgroundGrid.LookupRenderer"/> as the fallback ahead of the
    /// test pattern. Art + title are discovered from the install folder; nothing is app-specific.
    /// </summary>
    public sealed class BrandedSplashRenderer : IBackgroundRenderer
    {
        // Candidate art, best (largest / most key-art-like) first. FlipCycle tiles carry the full
        // key art; iconic tiles / the app icon are logo-only fallbacks.
        private static readonly string[] ArtCandidates =
        {
            "Assets/Tiles/FlipCycleTileLarge.png",   // wide key-art banner (best)
            "Assets/Tiles/FlipCycleTileMedium.png",  // square key art
            "Assets/Tiles/IconicTileMediumLarge.png",
            "Assets/ApplicationIcon.png",
        };

        private readonly string _installFolder;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private AvBitmap? _art;
        private double _cropTopFraction;   // trim the WP8 "XBOX" branding strip off FlipCycle tiles
        private string? _title;
        private bool _loaded;

        public BrandedSplashRenderer(string installFolder)
        {
            _installFolder = installFolder ?? throw new ArgumentNullException(nameof(installFolder));
        }

        /// <summary>True if the install folder has any artwork we can present.</summary>
        public static bool HasArt(string installFolder) => ResolveArtPath(installFolder) != null;

        public void OnContentProviderAttached(object? contentProvider) { }
        public void OnManipulationHandlerAttached(object? manipulationHandler) { }

        public bool Render(AvDrawingContext ctx, AvRect bounds)
        {
            EnsureLoaded();
            if (bounds.Width <= 0 || bounds.Height <= 0) return true;

            // 1. Background: deep nautical navy → near-black vertical gradient.
            var bg = new AvGradientBrush
            {
                StartPoint = new AvRelativePoint(0, 0, AvRelativeUnit.Relative),
                EndPoint = new AvRelativePoint(0, 1, AvRelativeUnit.Relative),
                GradientStops =
                {
                    new AvGradientStop(AvColor.FromRgb(0x12, 0x20, 0x33), 0.0),
                    new AvGradientStop(AvColor.FromRgb(0x0A, 0x12, 0x1E), 0.55),
                    new AvGradientStop(AvColor.FromRgb(0x04, 0x07, 0x0C), 1.0),
                },
            };
            ctx.DrawRectangle(bg, null, bounds);

            // 2. Key art, centered, scaled to ~92% width (down-scaled from the 691px tile → crisp).
            if (_art != null)
            {
                double srcW = _art.Size.Width;
                double srcH = _art.Size.Height;
                double cropTop = srcH * _cropTopFraction;
                var srcRect = new AvRect(0, cropTop, srcW, srcH - cropTop);

                double aspect = srcRect.Width / srcRect.Height;
                double destW = bounds.Width * 0.86;
                double destH = destW / aspect;
                double maxH = bounds.Height * 0.5;   // keep tall (square) tiles in bounds
                if (destH > maxH) { destH = maxH; destW = destH * aspect; }

                double destX = bounds.X + (bounds.Width - destW) / 2;
                double destY = bounds.Y + bounds.Height * 0.40 - destH / 2;
                var destRect = new AvRect(destX, destY, destW, destH);
                ctx.DrawImage(_art, srcRect, destRect);

                // 3. Animated loading indicator (three softly pulsing dots) beneath the art.
                DrawLoadingDots(ctx, bounds, destY + destH + bounds.Height * 0.07);
            }

            // 4. Subtle footer caption with the app title.
            if (!string.IsNullOrEmpty(_title))
            {
                var caption = new AvFormattedText(_title!, CultureInfo.CurrentCulture,
                    AvFlowDirection.LeftToRight, new AvTypeface("Segoe UI"),
                    bounds.Height * 0.022, new AvSolidColorBrush(AvColor.FromArgb(0x99, 0xCF, 0xDA, 0xE6)));
                ctx.DrawText(caption, new AvPoint(
                    bounds.X + (bounds.Width - caption.Width) / 2,
                    bounds.Y + bounds.Height * 0.93));
            }

            return true;
        }

        private void DrawLoadingDots(AvDrawingContext ctx, AvRect bounds, double centerY)
        {
            double t = _clock.Elapsed.TotalSeconds;
            double r = Math.Max(2.0, bounds.Width * 0.010);
            double gap = r * 3.2;
            double cx = bounds.X + bounds.Width / 2;
            for (int i = -1; i <= 1; i++)
            {
                // Staggered sine pulse per dot.
                double phase = t * 3.0 - i * 0.6;
                double a = 0.30 + 0.55 * (0.5 + 0.5 * Math.Sin(phase));
                var brush = new AvSolidColorBrush(AvColor.FromArgb((byte)(a * 255), 0xE8, 0xC9, 0x8A)); // warm gold
                ctx.DrawEllipse(brush, null, new AvPoint(cx + i * gap, centerY), r, r);
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            string? artPath = ResolveArtPath(_installFolder);
            if (artPath != null)
            {
                try
                {
                    _art = new AvBitmap(artPath);
                    // FlipCycle tiles ship with a ~15% green "XBOX" strip along the top — trim it.
                    if (artPath.Replace('\\', '/').Contains("/FlipCycleTile", StringComparison.OrdinalIgnoreCase))
                        _cropTopFraction = 0.20;
                }
                catch { _art = null; }
            }

            _title = TryReadTitle(_installFolder);
        }

        private static string? ResolveArtPath(string installFolder)
        {
            foreach (string rel in ArtCandidates)
            {
                string p = Path.Combine(installFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>Read the app's display Title from WMAppManifest.xml (namespace-agnostic).</summary>
        private static string? TryReadTitle(string installFolder)
        {
            try
            {
                string manifest = Path.Combine(installFolder, "WMAppManifest.xml");
                if (!File.Exists(manifest)) return null;
                var doc = XDocument.Load(manifest);
                foreach (var el in doc.Descendants())
                {
                    if (el.Name.LocalName != "App") continue;
                    string? title = el.Attribute("Title")?.Value;
                    return string.IsNullOrWhiteSpace(title) ? null : title!.Trim();
                }
            }
            catch { }
            return null;
        }
    }
}
