using WPR.Engine.Audio;
using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using WPR.Common;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// A decoded icon for the game's window, handed to <see cref="FnaGameHost"/> as data.
    ///
    /// <para><b>Why the backend owns the applying.</b> Setting the icon needs the SDL window
    /// handle, which only exists on the <c>Game</c> — so the launcher used to reach it through an
    /// <c>Action&lt;Game&gt;</c> hook and call <c>SDL_SetWindowIcon</c> from its own P/Invoke
    /// declarations. That put both a spine type and a native windowing call in a platform head.
    /// The head now decodes pixels (an Avalonia job it is the right place for) and hands them
    /// over; everything below the pixels is backend work. Moved 2026-09-01, Stage 5.</para>
    ///
    /// <para><b>Why this exists at all:</b> FNA's own icon path (<c>INTERNAL_SetIcon</c> in
    /// SDL2_FNAPlatform) only fires during window creation and looks for
    /// <c>&lt;entry-assembly-title&gt;.png</c> next to the game — i.e.
    /// <c>WPR.Platform.Windows.png</c>, never the per-game icon. So the icon is re-set explicitly
    /// once the <c>Game</c> instance exists, at which point the window handle is valid (the
    /// <c>Game</c> ctor has run <c>FNAPlatform.CreateWindow()</c>).</para>
    /// </summary>
    /// <param name="Bgra">Tightly packed little-endian Bgra8888, <paramref name="Width"/> * 4 stride.</param>
    public sealed record GameWindowIcon(byte[] Bgra, int Width, int Height)
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

        internal bool IsUsable => Bgra != null && Bgra.Length > 0 && Width > 0 && Height > 0;

        /// <summary>
        /// Applies this icon to the game's window. Never throws — a wrong icon must not cost the
        /// user their launch.
        /// </summary>
        internal void ApplyTo(Game game)
        {
            if (game == null || !IsUsable) return;
            try
            {
                IntPtr window = game.Window?.Handle ?? IntPtr.Zero;
                if (window == IntPtr.Zero) return;

                // Pin the pixel buffer for the lifetime of the SDL surface — SDL_CreateRGBSurfaceFrom
                // does NOT copy. SDL_SetWindowIcon DOES copy the surface's pixels into its own store,
                // so it's safe to free both the surface and the pin afterwards.
                GCHandle pin = GCHandle.Alloc(Bgra, GCHandleType.Pinned);
                try
                {
                    IntPtr px = pin.AddrOfPinnedObject();
                    // Avalonia's CopyPixels yields little-endian Bgra8888 — byte order in memory is
                    // B, G, R, A. As a uint32 that's 0xAARRGGBB, so Rmask=00FF0000 / Gmask=0000FF00
                    // / Bmask=000000FF / Amask=FF000000.
                    IntPtr surface = SDL_CreateRGBSurfaceFrom(
                        px, Width, Height,
                        32, Width * 4,
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
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppLaunch, $"SDL_SetWindowIcon failed: {ex.Message}");
            }
        }
    }
}
