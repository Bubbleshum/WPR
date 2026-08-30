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
        /// 1. User-configured picture file (<see cref="Configuration.GamerPicturePath"/>) —
        ///    set in the host's settings page, persisted across launches, shared by every
        ///    game that asks for it. Returned as a raw file stream so the original encoding
        ///    is preserved (FromStream handles PNG/JPG/GIF/BMP).
        /// 2. In-memory <see cref="GamerPicture"/> texture if a game/shim explicitly set one
        ///    (currently no internal code does, but the property is publicly settable in
        ///    spirit) — encoded to PNG via <c>Texture2D.SaveAsPng</c>.
        /// 3. <see cref="Stream.Null"/> as a last resort. Callers' inner try/catch around
        ///    FromStream is expected to handle this and skip the picture.
        /// </summary>
        public Stream GetGamerPicture()
        {
            string? configured = Configuration.Current?.GamerPicturePath;
            if (!string.IsNullOrEmpty(configured))
            {
                if (GamerPictureDefaults.IsDefault(configured))
                {
                    string id = GamerPictureDefaults.ExtractId(configured)!;
                    Stream? embedded = GamerPictureDefaults.Open(id);
                    if (embedded != null) return embedded;
                    Log.Warn(LogCategory.Common, $"GamerProfile.GetGamerPicture: bundled default '{id}' not found, falling back");
                }
                else if (File.Exists(configured))
                {
                    try { return File.OpenRead(configured); }
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
                if (fallback != null) return fallback;
            }

            return Stream.Null;
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
