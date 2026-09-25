using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WPR.Backend.FNA;
using WPR.Xna.Rhi;

namespace TexFmt
{
    /// <summary>
    /// Round-trips known texels through every SurfaceFormat TextureFormatShim touches, on whatever
    /// driver FNA3D_FORCE_DRIVER selects, and reports three numbers per format.
    ///
    /// <para>Two questions, deliberately separated because they fail independently:</para>
    /// <list type="number">
    /// <item><description><b>round trip</b> — SetData then GetData must return the exact bits the
    /// game wrote. This is what a game that reads, edits and writes a texture depends on.</description></item>
    /// <item><description><b>on screen</b> — the texel drawn through SpriteBatch and read back off
    /// the backbuffer must be the right colour. This is what catches a driver-side swizzle, and it
    /// is the half a pure managed unit test cannot answer.</description></item>
    /// </list>
    ///
    /// <para>Run it from the desktop head's output directory: the native SDL2 / FNA3D / FAudio
    /// DLLs are copied there by that project, not by a ProjectReference.</para>
    ///
    /// <para>Reproduce Android's GLES path on Windows with
    /// <c>FNA3D_FORCE_DRIVER=OpenGL</c> + <c>FNA3D_OPENGL_FORCE_ES3=1</c>.</para>
    /// </summary>
    internal static class Program
    {
        private const int Size = 4;

        [STAThread]
        public static void Main()
        {
            Console.WriteLine("[texfmt] FNA3D_FORCE_DRIVER=" +
                (Environment.GetEnvironmentVariable("FNA3D_FORCE_DRIVER") ?? "(unset)") +
                "  FNA3D_OPENGL_FORCE_ES3=" +
                (Environment.GetEnvironmentVariable("FNA3D_OPENGL_FORCE_ES3") ?? "(unset)"));

            XnaBackend.SetGraphics(new FnaGraphicsBackend());
            XnaBackend.SetPlatform(new FnaPlatformBackend());
            XnaBackend.SetInput(new FnaInputBackend());
            XnaBackend.SetStorage(new FnaStorageBackend());
            XnaBackend.SetLogInfo(Console.WriteLine);
            XnaBackend.SetLogWarn(Console.WriteLine);
            FNALoggerEXT.LogInfo = Console.WriteLine;
            FNALoggerEXT.LogWarn = Console.WriteLine;

            using (Harness game = new Harness())
            {
                game.Run();
            }
        }

        private sealed class Harness : Game
        {
            private readonly GraphicsDeviceManager graphics;

            public Harness()
            {
                graphics = new GraphicsDeviceManager(this);
                graphics.PreferredBackBufferWidth = Size;
                graphics.PreferredBackBufferHeight = Size;
                IsFixedTimeStep = false;
            }

            protected override void Update(GameTime gameTime)
            {
                Check();
                Exit();
            }

            private void Check()
            {
                Console.WriteLine();
                /* Opaque red and opaque blue, plus a half-alpha green, because an R/B swap is the
                 * classic driver-side failure and alpha is what the 4444/5551 packings disagree
                 * about most. Values are chosen to survive the precision of every format here. */
                RoundTrip(SurfaceFormat.Bgra4444, new ushort[] { 0xFF00, 0xF00F, 0xF0F0, 0x70F0 });
                RoundTrip(SurfaceFormat.Bgra5551, new ushort[] { 0xFC00, 0x801F, 0x83E0, 0x03E0 });
                RoundTrip(SurfaceFormat.Bgr565, new ushort[] { 0xF800, 0x001F, 0x07E0, 0xFFFF });
                OnScreen(SurfaceFormat.Bgra4444, 0xFF00, "red");
                OnScreen(SurfaceFormat.Bgra4444, 0xF00F, "blue");
                OnScreen(SurfaceFormat.Bgra5551, 0xFC00, "red");
                OnScreen(SurfaceFormat.Bgra5551, 0x801F, "blue");
                OnScreen(SurfaceFormat.Bgr565, 0xF800, "red");
                OnScreen(SurfaceFormat.Bgr565, 0x001F, "blue");
                Guard();
            }

            /// <summary>
            /// The readback guard. On GLES a mip-level read is a NULL glGetTexImage call and a
            /// narrow-format read overruns the pinned array, so both must come back as zeros
            /// rather than a SIGSEGV; everywhere else they must return real pixels.
            /// </summary>
            private void Guard()
            {
                try
                {
                    Texture2D mip = new Texture2D(GraphicsDevice, 4, 4, true, SurfaceFormat.Color);
                    Color[] level0 = new Color[16];
                    for (int i = 0; i < level0.Length; i += 1)
                    {
                        level0[i] = Color.Red;
                    }
                    mip.SetData(0, null, level0, 0, level0.Length);
                    Color[] level1 = new Color[4];
                    for (int i = 0; i < level1.Length; i += 1)
                    {
                        level1[i] = Color.Blue;
                    }
                    mip.SetData(1, null, level1, 0, level1.Length);

                    Color[] read0 = new Color[16];
                    mip.GetData(0, null, read0, 0, read0.Length);
                    Color[] read1 = new Color[4];
                    mip.GetData(1, null, read1, 0, read1.Length);

                    Console.WriteLine(
                        "[texfmt] guard      level0 -> R=" + read0[0].R + " G=" + read0[0].G +
                        " B=" + read0[0].B +
                        "   level1 -> R=" + read1[0].R + " G=" + read1[0].G + " B=" + read1[0].B +
                        " (survived, no access violation)");
                    mip.Dispose();

                    /* Alpha8 is one byte per pixel: on GLES the FBO path would write four bytes
                     * per pixel into this array regardless of the length we pass. */
                    Texture2D narrow = new Texture2D(GraphicsDevice, 4, 4, false, SurfaceFormat.Alpha8);
                    narrow.SetData(new byte[16]);
                    byte[] readNarrow = new byte[16];
                    narrow.GetData(readNarrow);
                    Console.WriteLine("[texfmt] guard      Alpha8 GetData survived");
                    narrow.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine("[texfmt] guard      THREW " + e.GetType().Name + ": " + e.Message);
                }
            }

            /// <summary>SetData then GetData must be bit-identical.</summary>
            private void RoundTrip(SurfaceFormat format, ushort[] texels)
            {
                try
                {
                    Texture2D tex = new Texture2D(GraphicsDevice, 2, 2, false, format);
                    tex.SetData(texels);

                    ushort[] read = new ushort[texels.Length];
                    tex.GetData(read);

                    bool ok = true;
                    for (int i = 0; i < texels.Length; i += 1)
                    {
                        if (texels[i] != read[i])
                        {
                            ok = false;
                        }
                    }

                    Console.WriteLine(
                        "[texfmt] round trip " + format + ": " + (ok ? "OK" : "MISMATCH") +
                        "  wrote=" + Hex(texels) + " read=" + Hex(read) +
                        "  Format=" + tex.Format);
                    tex.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine("[texfmt] round trip " + format + ": THREW " + e.GetType().Name + ": " + e.Message);
                }
            }

            /// <summary>Draw one texel over the whole backbuffer and read the pixel back.</summary>
            private void OnScreen(SurfaceFormat format, ushort texel, string expected)
            {
                try
                {
                    Texture2D tex = new Texture2D(GraphicsDevice, 1, 1, false, format);
                    tex.SetData(new ushort[] { texel });

                    GraphicsDevice.Clear(Color.Black);
                    SpriteBatch batch = new SpriteBatch(GraphicsDevice);
                    batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque);
                    batch.Draw(tex, new Rectangle(0, 0, Size, Size), Color.White);
                    batch.End();

                    Color[] screen = new Color[Size * Size];
                    GraphicsDevice.GetBackBufferData(screen);

                    Console.WriteLine(
                        "[texfmt] on screen  " + format + " texel 0x" + texel.ToString("X4") +
                        " (" + expected + ") -> R=" + screen[0].R + " G=" + screen[0].G +
                        " B=" + screen[0].B + " A=" + screen[0].A);

                    batch.Dispose();
                    tex.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine("[texfmt] on screen  " + format + ": THREW " + e.GetType().Name + ": " + e.Message);
                }
            }

            private static string Hex(ushort[] v)
            {
                string s = "";
                for (int i = 0; i < v.Length; i += 1)
                {
                    s += (i == 0 ? "" : ",") + v[i].ToString("X4");
                }
                return s;
            }
        }
    }
}
