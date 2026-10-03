using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using System.Globalization;
using WPR.Common;

namespace Microsoft.Xna.Framework.GamerServices
{
    public sealed class GamerProfile : IDisposable
    {

        internal GamerProfile()
        {
        }

        public void Dispose()
        {
            IsDisposed = true;
        }

        public Texture2D GamerPicture
        {
            get;
            internal set;
        }

        /// <summary>
        /// WP7 API surface: returns the gamer picture as an encoded image stream (PNG/JPG)
        /// suitable for <c>Texture2D.FromStream</c>. Fruit Ninja and other titles that
        /// integrate with the Xbox LIVE profile UI call this on the signed-in gamer's
        /// profile; the absence of the method JIT-fails the calling site with
        /// MissingMethodException, which games typically surface as a generic error dialog.
        ///
        /// Resolution order:
        /// 1. The WPR Hub gamerpic while signed in (<see cref="Configuration.EffectiveGamerPicturePath"/>,
        ///    the file the hub module saved), shared by every game that asks for it. Returned
        ///    as a raw file stream so the original encoding is preserved. Signed out there is
        ///    none, and the bundled default at the end is used.
        /// 2. In-memory <see cref="GamerPicture"/> texture if a game/shim explicitly set one
        ///    (currently no internal code does, but the property is publicly settable in
        ///    spirit) — encoded to PNG via <c>Texture2D.SaveAsPng</c>.
        /// 3. <see cref="Stream.Null"/> as a last resort. Callers' inner try/catch around
        ///    FromStream is expected to handle this and skip the picture.
        /// </summary>
        /// <summary>Set for another player (a leaderboard row): their picture is theirs, never the local one.</summary>
        internal bool IsOtherPlayer;

        /// <summary>Another player's picture from WPR Hub, as encoded image bytes; null when they have none.</summary>
        internal byte[] PictureBytes;

        public Stream GetGamerPicture()
        {
            // Another player (a leaderboard row, Zuma's Revenge draws one per entry): their hub
            // gamerpic, or the bundled default. Falling through to the local player's picture would
            // put your face beside every name on the board.
            if (IsOtherPlayer)
            {
                if (PictureBytes is { Length: > 0 }) return AtGamerPictureSize(new MemoryStream(PictureBytes, writable: false));
                IReadOnlyList<string> stock = GamerPictureDefaults.Ids;
                Stream other = stock.Count > 0 ? GamerPictureDefaults.Open(stock[0]) : null;
                return other != null ? AtGamerPictureSize(other) : Stream.Null;
            }

            string? configured = Configuration.Current?.EffectiveGamerPicturePath;
            if (!string.IsNullOrEmpty(configured))
            {
                if (GamerPictureDefaults.IsDefault(configured))
                {
                    string id = GamerPictureDefaults.ExtractId(configured)!;
                    Stream? embedded = GamerPictureDefaults.Open(id);
                    if (embedded != null) return AtGamerPictureSize(embedded);
                    Log.Warn(LogCategory.Common, $"GamerProfile.GetGamerPicture: bundled default '{id}' not found, falling back");
                }
                else if (File.Exists(configured))
                {
                    try { return AtGamerPictureSize(File.OpenRead(configured)); }
                    catch (Exception ex) { Log.Warn(LogCategory.Common, $"GamerProfile.GetGamerPicture: failed to open {configured}: {ex.Message}"); }
                }
            }

            Texture2D pic = GamerPicture;
            if (pic != null)
            {
                MemoryStream ms = new MemoryStream();
                pic.SaveAsPng(ms, pic.Width, pic.Height);
                ms.Position = 0;
                return ms;
            }

            // Nothing configured and no in-memory picture: fall back to a bundled default
            // avatar rather than Stream.Null. An empty stream makes Texture2D.FromStream
            // produce a null/invalid texture, which games store and later draw — surfacing
            // as a blank gamerpic and, for games that don't null-check (Fruit Ninja 2013),
            // a crash on the next redraw/focus change.
            IReadOnlyList<string> defaults = GamerPictureDefaults.Ids;
            if (defaults.Count > 0)
            {
                Stream? fallback = GamerPictureDefaults.Open(defaults[0]);
                if (fallback != null) return AtGamerPictureSize(fallback);
            }

            return Stream.Null;
        }

        /// <summary>The size of a gamer picture on Windows Phone 7 (and Xbox LIVE of the time).</summary>
        private const int GamerPictureSize = 64;

        /// <summary>
        /// Re-encode <paramref name="source"/> as a 64x64 PNG unless it already is one.
        /// </summary>
        /// <remarks>
        /// Every gamer picture a WP7 title ever received was 64x64, and titles draw it at its own
        /// size: Fruit Ninja puts it on its home screen through <c>Texture2D.FromStream</c> and a
        /// plain <c>SpriteBatch.Draw</c>, so the hub's 256x256 gamerpic covered a quarter of the
        /// screen. Scaling here rather than downloading the hub's 64px variant also covers every
        /// install that already has the 256 file cached, and the bundled defaults. FNA3D's image
        /// codec is device-independent, so this needs no graphics device. Any failure hands back
        /// the original bytes: a big picture is better than none.
        /// </remarks>
        private static Stream AtGamerPictureSize(Stream source)
        {
            MemoryStream original = new MemoryStream();
            using (source) source.CopyTo(original);

            if (!WPR.Xna.Rhi.XnaBackend.HasGraphics)
            {
                original.Position = 0;
                return original;
            }
            var graphics = WPR.Xna.Rhi.XnaBackend.Graphics;

            IntPtr pixels = IntPtr.Zero;
            try
            {
                // Decode at native size and let the PNG writer scale. Do NOT pass forceW/forceH
                // here: FNA3D_Image_Load's resize path frees stb's SIMD-aligned buffer with plain
                // SDL_free, which Android's scudo allocator reports as heap corruption and aborts
                // the process (Fruit Ninja died on launch, 2026-10-03). The write path scales
                // through an ordinary surface and never frees the caller's buffer, which is the
                // route Texture2D.SaveAsPng already takes.
                original.Position = 0;
                pixels = graphics.ReadImageStream(original, out int width, out int height, out _);
                if (pixels != IntPtr.Zero && width > 0 && height > 0)
                {
                    if (width == GamerPictureSize && height == GamerPictureSize)
                    {
                        original.Position = 0;
                        return original;
                    }
                    MemoryStream scaled = new MemoryStream();
                    graphics.WritePNGStream(scaled, width, height, GamerPictureSize, GamerPictureSize, pixels);
                    scaled.Position = 0;
                    return scaled;
                }
                Log.Warn(LogCategory.Common, $"GamerProfile.GetGamerPicture: could not decode the picture ({width}x{height}); using it as it is");
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Common, $"GamerProfile.GetGamerPicture: could not scale the picture: {ex.Message}");
            }
            finally
            {
                if (pixels != IntPtr.Zero) graphics.FreeImage(pixels);
            }

            original.Position = 0;
            return original;
        }

        public int GamerScore
        {
            get;
            internal set;
        }

        public GamerZone GamerZone
        {
            get;
            internal set;
        }

        public bool IsDisposed
        {
            get;
            internal set;
        }

        public string Motto
        {
            get;
            internal set;
        }

        public RegionInfo Region
        {
            get;
            internal set;
        }

        public float Reputation
        {
            get;
            internal set;
        }

        public int TitlesPlayed
        {
            get;
            internal set;
        }

        public int TotalAchievements
        {
            get;
            internal set;
        }

    }
}
