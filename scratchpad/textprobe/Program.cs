using System;
using System.IO;
using System.Reflection;

// Renders a few strings through WPR's TextRasteriser and writes a PPM, so the glyph
// rasteriser can be checked without launching a game. Reflection because the rasteriser
// is internal to WPR.Framework.Silverlight (as it should be).
internal static class Program
{
    private static int Main()
    {
        Assembly asm = typeof(WPR.SilverlightCompability.HostContext).Assembly;

        Type? tr = asm.GetType("WPR.SilverlightCompability.TextRasteriser");
        if (tr == null) { Console.Error.WriteLine("TextRasteriser not found"); return 1; }

        object? available = tr.GetProperty("IsAvailable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                              ?.GetValue(null);
        Console.WriteLine($"IsAvailable = {available}");
        if (available is not true) return 2;

        const int W = 900, H = 260;
        uint[] buffer = new uint[W * H];

        // Opaque white background so coverage is easy to judge by eye.
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xFFFFFFFFu;

        Type rectType = asm.GetType("WPR.SilverlightCompability.Rect")!;
        object clip = Activator.CreateInstance(rectType, 0.0, 0.0, (double)W, (double)H)!;

        MethodInfo draw = tr.GetMethod("Draw", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo measure = tr.GetMethod("Measure", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // Premultiplied, R in the low byte (XNA Color packing) — opaque black.
        const uint Black = 0xFF000000u;

        double[] sizes = { 18.667, 20.0, 32.0, 42.667, 72.0 };
        double y = 4;

        foreach (double size in sizes)
        {
            object?[] margs = { "Carcassonne 123 gjpqy", size, null, null };
            measure.Invoke(null, margs);
            Console.WriteLine($"  size {size,7:F3}  measured {(double)margs[2]!,8:F2} x {(double)margs[3]!,7:F2}");

            draw.Invoke(null, new object?[]
            {
                buffer, W, H, "Carcassonne 123 gjpqy", size, 6.0, y, Black, clip,
            });

            y += (double)margs[3]!;
        }

        string outPath = Path.Combine(AppContext.BaseDirectory, "text.ppm");
        using (var fs = new FileStream(outPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            foreach (char c in $"P6\n{W} {H}\n255\n") bw.Write((byte)c);
            for (int i = 0; i < buffer.Length; i++)
            {
                uint p = buffer[i];
                bw.Write((byte)(p & 0xFF));
                bw.Write((byte)((p >> 8) & 0xFF));
                bw.Write((byte)((p >> 16) & 0xFF));
            }
        }

        // Ink coverage: a rasteriser that silently draws nothing is the failure to catch.
        int inked = 0;
        for (int i = 0; i < buffer.Length; i++) if ((buffer[i] & 0xFFFFFFu) != 0xFFFFFFu) inked++;
        Console.WriteLine($"inked pixels = {inked} of {buffer.Length}");
        Console.WriteLine($"wrote {outPath}");

        return inked > 0 ? 0 : 3;
    }
}
