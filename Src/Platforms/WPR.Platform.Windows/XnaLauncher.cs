using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using WPR.Backend.FNA;
using WPR.Common;
using WPRModel = WPR.Models.Application;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// Boots an installed XNA game, decoding the game's icon so the backend can put it on the
    /// game's window.
    /// </summary>
    /// <remarks>
    /// <para>FNA's own icon path (<c>INTERNAL_SetIcon</c> in SDL2_FNAPlatform) only fires during
    /// window creation and looks for <c>&lt;entry-assembly-title&gt;.png</c> next to the game —
    /// i.e. <c>WPR.Platform.Windows.png</c>, never the per-game icon. So the icon is re-set
    /// explicitly once the game object exists; <see cref="GameWindowIcon"/> does that.</para>
    ///
    /// <para><b>This class used to do considerably more</b> (until 2026-09-01, Stage 5): it
    /// declared its own <c>SDL_SetWindowIcon</c> P/Invokes and passed an
    /// <c>Action&lt;Game&gt;</c> hook down to the host, in which it reached the SDL window handle
    /// and attached two FNA <c>GameComponent</c>s for the tilt emulator. All of that was backend
    /// work being done in a platform head, and it was the reason this assembly appeared in
    /// <c>KnownBackendLeaks</c>. What is left here is the one genuinely head-shaped step:
    /// decoding a PNG with Avalonia's imaging stack. The pixels go down as data.</para>
    /// </remarks>
    public static class XnaLauncher
    {
        public static Task LaunchAsync(WPRModel app, Action<Microsoft.Xna.Framework.DisplayOrientation>? requestOrientation = null)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            GameWindowIcon? icon = null;
            try
            {
                if (!string.IsNullOrEmpty(app.IconPath))
                {
                    string iconFullPath = Configuration.Current!.DataPath(app.IconPath);
                    if (File.Exists(iconFullPath))
                    {
                        byte[] bgra = DecodeIconToBgra(iconFullPath, out int w, out int h);
                        if (w > 0 && h > 0) icon = new GameWindowIcon(bgra, w, h);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppLaunch, $"Failed to decode game icon '{app.IconPath}' for window: {ex.Message}");
                icon = null;
            }

            // Drive the game through the IGameHost seam (FNA backend).
            // RunAsync() returns the same Task the legacy ApplicationLaunch.Start call did.
            return new FnaGameHost(app, requestOrientation, icon).RunAsync();
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
    }
}
