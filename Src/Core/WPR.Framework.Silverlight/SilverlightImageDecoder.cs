using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using WPR.WindowsCompability;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Turns a Silverlight <see cref="ImageSource"/> into ARGB pixels for the software
    /// rasteriser, resolving the three places a WP7 app's image can actually live: an absolute
    /// path, a file in the install folder, and — the one that matters most — an entry in the
    /// app assembly's <c>&lt;AssemblyName&gt;.g.resources</c> bundle, which is where the
    /// Silverlight <c>Resource</c> build action puts it.
    /// </summary>
    /// <remarks>
    /// <para><b>The embedded case is the common one, not the exotic one.</b> Rabbids Go Phone's
    /// menu background is <c>ImageSource="LoadingScreen.png"</c> on the page's root Canvas and
    /// there is no such file anywhere in the install — it is `loadingscreen.png` inside
    /// `RabbidsGoPhone.g.resources`. A resolver that only looked at the filesystem would find
    /// nothing and the page would paint empty, which is indistinguishable from "this brush is
    /// not supported yet".</para>
    ///
    /// <para><b>Decoding goes through <see cref="BitmapSource.SetSource"/></b> rather than
    /// carrying its own copy of the stb_image call. Two reasons: there is then exactly one
    /// image-decode path in this assembly, and when the source really is a
    /// <see cref="BitmapSource"/> the decoded pixels land <i>on the game's own object</i>, so a
    /// title that later reads <c>PixelWidth</c> or <c>Pixels</c> off the bitmap it handed us
    /// gets real numbers instead of the 1x1 placeholder.</para>
    ///
    /// <para><b>Failures are cached, and that is not an optimisation detail.</b> Resolution
    /// walks <c>GetManifestResourceNames</c> and opens a <c>ResourceReader</c> over every
    /// <c>.g.resources</c> bundle; the rasteriser runs from a game's draw, so an uncached miss
    /// would do that sixty times a second for ever.</para>
    /// </remarks>
    public static class SilverlightImageDecoder
    {
        private sealed class Decoded
        {
            public int[]? Argb;
            public int Width;
            public int Height;
            public bool Failed;
        }

        private static readonly ConditionalWeakTable<ImageSource, Decoded> Cache = new();

        /// <summary>
        /// Decoded images by the path they came from, so the same picture is read and decoded
        /// once however many <see cref="ImageSource"/> objects name it. Games rebuild brushes
        /// freely — Rabbids Go Phone's <c>RenderBlueBG</c> setter constructs a fresh
        /// <c>ImageBrush</c> and <c>BitmapImage</c> over the same file on every screen change —
        /// and the per-instance table alone would re-decode each time.
        ///
        /// <para>Only ever holds images loaded from a path, so nothing a game can mutate in place
        /// (a <c>WriteableBitmap</c>'s pixels) is ever shared between two sources. Bounded because
        /// a full-screen WVGA image is 1.5 MB and this is process-lifetime; past the cap decoding
        /// still works, it simply stops being cached.</para>
        /// </summary>
        private static readonly Dictionary<string, Decoded> ByPath = new(StringComparer.OrdinalIgnoreCase);

        private const int MaxCachedImages = 24;

        private static int _generation;

        /// <summary>
        /// Bumped whenever a decode succeeds. The rasteriser folds this into its change
        /// signature so the frame after an image resolves repaints — without it, a tree whose
        /// only change was "the picture finally arrived" would compare equal and never be drawn.
        /// </summary>
        internal static int Generation => Volatile.Read(ref _generation);

        /// <summary>
        /// Drops every cached image. Called at game teardown.
        /// </summary>
        /// <remarks>
        /// <b>Required for correctness, not just to give the memory back.</b> The path cache is
        /// keyed by the relative name a page asked for, and on the desktop head two games run in
        /// one process — so a second title asking for its own <c>LoadingScreen.png</c> would be
        /// handed the first one's. The generation counter is left alone: it only has to change,
        /// and resetting it could collide with a signature computed before teardown.
        /// </remarks>
        public static void ResetForNewLaunch()
        {
            lock (ByPath) { ByPath.Clear(); }
            Cache.Clear();
        }

        /// <summary>
        /// Pixels for <paramref name="source"/> as 0xAARRGGBB, row-major, straight (not
        /// premultiplied) alpha — the layout <see cref="BitmapSource"/> already uses.
        /// </summary>
        public static bool TryGetPixels(ImageSource? source, out int[]? argb, out int width, out int height)
        {
            argb = null;
            width = 0;
            height = 0;
            if (source == null) return false;

            // Live object first, and before the cache: a game may SetSource the same bitmap
            // again with different content (WriteableBitmap is the obvious case), and a cached
            // copy would keep painting the old pixels.
            if (source is BitmapSource live && live.PixelBuffer is int[] livePixels &&
                live.DecodedWidth > 0 && live.DecodedHeight > 0 &&
                livePixels.Length >= live.DecodedWidth * live.DecodedHeight)
            {
                argb = livePixels;
                width = live.DecodedWidth;
                height = live.DecodedHeight;
                return true;
            }

            if (Cache.TryGetValue(source, out Decoded? cached))
            {
                if (cached.Failed || cached.Argb == null) return false;
                argb = cached.Argb;
                width = cached.Width;
                height = cached.Height;
                return true;
            }

            Decoded decoded = Decode(source);
            Cache.Add(source, decoded);

            if (decoded.Failed || decoded.Argb == null) return false;

            Interlocked.Increment(ref _generation);
            argb = decoded.Argb;
            width = decoded.Width;
            height = decoded.Height;
            return true;
        }

        private static Decoded Decode(ImageSource source)
        {
            var result = new Decoded { Failed = true };

            string? path = source.Path;
            if (string.IsNullOrWhiteSpace(path)) return result;

            lock (ByPath)
            {
                if (ByPath.TryGetValue(path!, out Decoded? seen)) return seen;
            }

            Stream? stream = null;
            try
            {
                stream = OpenImageStream(path!);
                if (stream == null)
                {
                    System.Diagnostics.Trace.WriteLine(
                        "[wpr-uirender] could not resolve image '" + path + "' as a file or an " +
                        "embedded resource; it will not be painted.");
                    return result;
                }

                // Decode onto the game's own bitmap when it gave us one, so its PixelWidth /
                // Pixels start answering too; otherwise onto a throwaway we keep in the cache.
                BitmapSource target = source as BitmapSource ?? new BitmapSource();
                target.SetSource(stream);

                if (target.PixelBuffer is int[] pixels && target.DecodedWidth > 0 && target.DecodedHeight > 0 &&
                    pixels.Length >= target.DecodedWidth * target.DecodedHeight)
                {
                    result.Argb = pixels;
                    result.Width = target.DecodedWidth;
                    result.Height = target.DecodedHeight;
                    result.Failed = false;
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine(
                        "[wpr-uirender] image '" + path + "' resolved but decoded to nothing " +
                        "(no graphics backend, or a format stb_image cannot read).");
                }
            }
            catch (Exception ex)
            {
                // A page with one bad image must still paint the rest of itself.
                System.Diagnostics.Trace.WriteLine(
                    "[wpr-uirender] decoding image '" + path + "' threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                stream?.Dispose();
            }

            lock (ByPath)
            {
                // A failure is worth remembering too: resolution walks every manifest resource
                // and opens a ResourceReader over each .g.resources bundle, and this runs from a
                // draw.
                if (ByPath.Count < MaxCachedImages) ByPath[path!] = result;
            }

            return result;
        }

        private static Stream? OpenImageStream(string path)
        {
            // 1. Exactly as given — an absolute path, or one relative to the process cwd.
            try
            {
                if (File.Exists(path)) return File.OpenRead(path);
            }
            catch (Exception) { /* unreadable is the same as absent here */ }

            // 2. Relative to the install folder. Separators are normalised because WP7 XAML is
            //    full of Windows-style paths and Android treats a backslash as a filename
            //    character — the same trap ContentPaths exists for.
            string? install = HostContext.CurrentInstallFolder;
            if (!string.IsNullOrEmpty(install))
            {
                try
                {
                    string relative = path.TrimStart('/', '\\')
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar);
                    string full = Path.Combine(install!, relative);
                    if (File.Exists(full)) return File.OpenRead(full);
                }
                catch (Exception) { /* as above */ }
            }

            // 3. An embedded Silverlight resource. Application.GetResourceStream already knows
            //    both shapes — a plain manifest resource and a .g.resources bundle entry — so
            //    this only has to decide which assemblies to ask.
            var uri = new Uri(path, UriKind.RelativeOrAbsolute);
            foreach (Assembly? asm in CandidateAssemblies())
            {
                if (asm == null) continue;
                try
                {
                    StreamResourceInfo? info = Application.GetResourceStream(uri, asm);
                    if (info?.Stream != null) return info.Stream;
                }
                catch (Exception) { /* a game assembly with no resources at all */ }
            }

            return null;
        }

        /// <summary>
        /// The game's own assembly, which is where a resource named relative to the XAP root
        /// lives. <c>HostContext.UserAssembly</c> is asked first and is normally the answer; the
        /// running <c>Application</c>'s assembly is the fallback for a launch that got far enough
        /// to construct the app but not to record it, and is deliberately only touched then —
        /// <c>Application.Current</c> mints a singleton when there is none, which a draw-time
        /// helper has no business doing.
        /// </summary>
        private static Assembly?[] CandidateAssemblies()
        {
            Assembly? fromHost = HostContext.UserAssembly;
            if (fromHost != null) return new[] { fromHost };

            try { return new[] { Application.Current.GetType().Assembly }; }
            catch (Exception) { return Array.Empty<Assembly?>(); }
        }
    }
}
