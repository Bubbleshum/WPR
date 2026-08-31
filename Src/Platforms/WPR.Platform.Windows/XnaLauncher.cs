using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Microsoft.Xna.Framework;
using WPR.Common;
using WPR.Platform.Windows.Input;
using WPRModel = WPR.Models.Application;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// Boots an installed XNA game and decorates its SDL window with the game's icon
    /// before <see cref="Game.Run"/> takes over the thread. Falls back to
    /// <see cref="ApplicationLaunch.Start"/> unchanged when no icon is available.
    /// </summary>
    /// <remarks>
    /// FNA's own icon path (<c>INTERNAL_SetIcon</c> in SDL2_FNAPlatform) only fires
    /// during window creation and looks for <c>&lt;entry-assembly-title&gt;.png</c>
    /// next to the game — i.e. <c>WPR.Platform.Windows.png</c>, never the per-game icon.
    /// We re-set the icon explicitly using <c>SDL_SetWindowIcon</c> right after the
    /// <see cref="Game"/> instance is constructed (window handle is valid then;
    /// see <c>Game.ctor</c> in vendored FNA which calls <c>FNAPlatform.CreateWindow()</c>).
    /// </remarks>
    public static class XnaLauncher
    {
        private const string SDL2 = "SDL2";

        [DllImport(SDL2, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_CreateRGBSurfaceFrom")]
        private static extern IntPtr SDL_CreateRGBSurfaceFrom(
            IntPtr pixels, int width, int height, int depth, int pitch,
            uint Rmask, uint Gmask, uint Bmask, uint Amask);

        [DllImport(SDL2, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetWindowIcon")]
        private static extern void SDL_SetWindowIcon(IntPtr window, IntPtr icon);

        [DllImport(SDL2, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_FreeSurface")]
        private static extern void SDL_FreeSurface(IntPtr surface);

        public static Task LaunchAsync(WPRModel app, Action<Microsoft.Xna.Framework.DisplayOrientation>? requestOrientation = null)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            byte[]? iconBgra = null;
            int iconW = 0, iconH = 0;
            try
            {
                if (!string.IsNullOrEmpty(app.IconPath))
                {
                    string iconFullPath = Configuration.Current!.DataPath(app.IconPath);
                    if (File.Exists(iconFullPath))
                    {
                        iconBgra = DecodeIconToBgra(iconFullPath, out iconW, out iconH);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppLaunch, $"Failed to decode game icon '{app.IconPath}' for window: {ex.Message}");
                iconBgra = null;
            }

            byte[]? pixelsCapture = iconBgra;
            int wCapture = iconW, hCapture = iconH;

            Action<Game> onCreated = game =>
            {
                if (pixelsCapture != null && wCapture > 0 && hCapture > 0)
                {
                    try
                    {
                        IntPtr window = game.Window?.Handle ?? IntPtr.Zero;
                        if (window != IntPtr.Zero)
                        {
                            ApplyIcon(window, pixelsCapture, wCapture, hCapture);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(LogCategory.AppLaunch, $"SDL_SetWindowIcon failed: {ex.Message}");
                    }
                }

                // Wire the keyboard → accelerometer simulator: a polling GameComponent on the
                // game's own update tick, plus the overlay if enabled.
                try
                {
                    KeyboardTiltBinding.ApplyConfigurationToHost();
                    game.Components.Add(new TiltInputXnaComponent(game));
                    if (WPR.Common.Configuration.Current?.TiltOverlayEnabled == true)
                    {
                        game.Components.Add(new TiltOverlayXnaComponent(game));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppLaunch, $"Failed to wire tilt input/overlay component: {ex.Message}");
                }
            };

            // Drive the game through the WPR.Abstractions IGameHost seam (FNA backend).
            // RunAsync() returns the same Task the legacy ApplicationLaunch.Start call did.
            return new WPR.Backend.FNA.FnaGameHost(app, requestOrientation, onCreated).RunAsync();
        }

        private static byte[] DecodeIconToBgra(string path, out int width, out int height)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Bitmap bmp = new Bitmap(fs);
            width = bmp.PixelSize.Width;
            height = bmp.PixelSize.Height;
            int stride = width * 4;
            byte[] buffer = new byte[stride * height];
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, width, height), pin.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                pin.Free();
            }
            return buffer;
        }

        private static void ApplyIcon(IntPtr window, byte[] bgraPixels, int width, int height)
        {
            // Pin the pixel buffer for the lifetime of the SDL surface — SDL_CreateRGBSurfaceFrom
            // does NOT copy. SDL_SetWindowIcon DOES copy the surface's pixels into its own store,
            // so it's safe to free both the surface and the pin afterwards.
            GCHandle pin = GCHandle.Alloc(bgraPixels, GCHandleType.Pinned);
            try
            {
                IntPtr px = pin.AddrOfPinnedObject();
                // Avalonia's CopyPixels yields little-endian Bgra8888 — byte order in memory is
                // B, G, R, A. As a uint32 that's 0xAARRGGBB, so Rmask=00FF0000 / Gmask=0000FF00
                // / Bmask=000000FF / Amask=FF000000.
                IntPtr surface = SDL_CreateRGBSurfaceFrom(
                    px, width, height,
                    32, width * 4,
                    0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u);
                if (surface == IntPtr.Zero) return;
                try
                {
                    SDL_SetWindowIcon(window, surface);
                }
                finally
                {
                    SDL_FreeSurface(surface);
                }
            }
            finally
            {
                pin.Free();
            }
        }
    }
}
