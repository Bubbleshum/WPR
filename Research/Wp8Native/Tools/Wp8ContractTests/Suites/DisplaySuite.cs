using System.Reflection;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The screen model (Wp8Display): WPR presents itself as a WXGA WP8 phone - 768x1280 physical
/// pixels at ResolutionScale 160, a 480x800 DIP CoreWindow - and every size answer derives from
/// that one model: DisplayProperties, the swap chain, the composition raster, the XAML shells'
/// sizes, and pointer positions (logical, like a WP8 PointerPoint).
/// </summary>
public sealed class DisplaySuite : IContractSuite
{
    public string Name => "display";

    private const long SOk = 0;

    /// <summary>The bottom of the guest stack, far below anything a call uses: the DisplayProperties
    /// getters only write through a stack out-parameter, as a game's would be.</summary>
    private const long StackScratch = 0x50000000L + 0x1000;

    public void Run(Harness h)
    {
        Presets(h);
        Model(h);
        DisplayProperties(h);
        SwapChain(h);
        Shells(h);
        Pointer(h);
    }

    private static void Presets(Harness h)
    {
        h.Check("wvga preset is 480x800 at scale 100", Wp8Display.Preset("wvga") == ("wvga", 480, 800, 100));
        h.Check("wxga preset is 768x1280 at scale 160", Wp8Display.Preset("WXGA") == ("wxga", 768, 1280, 160));
        h.Check("720p preset is 720x1280 at scale 150", Wp8Display.Preset("720p") == ("720p", 720, 1280, 150));
        h.Check("no preset is WXGA (Lumia 920 class)", Wp8Display.Preset(null) == ("wxga", 768, 1280, 160));
        h.Check("every preset is 480 DIPs wide",
            new[] { "wvga", "wxga", "720p" }.Select(Wp8Display.Preset).All(p => p.Width * 100 / p.Scale == 480));
    }

    private static bool Overridden =>
        new[] { "WPR_WP8_RESOLUTION", "WPR_WINDOW", "WPR_RESSCALE", "WPR_SCREEN" }
            .Any(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)));

    private static void Model(Harness h)
    {
        if (Overridden)
        {
            h.Check("default model (skipped: a WPR_* screen override is set)", true, Wp8Display.Describe());
            return;
        }

        h.Check("default physical size is 768x1280", Wp8Display.PhysicalWidth == 768 && Wp8Display.PhysicalHeight == 1280, Wp8Display.Describe());
        h.Check("default scale is 160", Wp8Display.Scale == 160 && Wp8Display.ResolutionScaleEnum == 160);
        h.Check("logical size is physical * 100 / scale = 480x800",
            Math.Abs(Wp8Display.LogicalWidth - 480) < 0.01 && Math.Abs(Wp8Display.LogicalHeight - 800) < 0.01);
        h.Check("LogicalDpi is 96 * 1.6", Math.Abs(Wp8Display.LogicalDpi - 153.6f) < 0.01);
        h.Check("composition is the physical size turned landscape (1280x768)",
            Wp8Display.CompositionWidth == 1280 && Wp8Display.CompositionHeight == 768);
        h.Check("DXGI's screen is the physical size",
            Direct3DRuntime.ScreenWidth == 768 && Direct3DRuntime.ScreenHeight == 1280);
        h.Check("the host raster is the composition size", FrameCapture.Width == 1280 && FrameCapture.Height == 768);
    }

    private static void SwapChain(Harness h)
    {
        // A swap chain is the size the game asks for, and the screen's when it asks for zero (WP8
        // stretched a smaller one to the panel): Angry Birds Rio asks for its 480x800 DIP bounds,
        // the later Rovio titles for 768x1280.
        Direct3DRuntime d3d = h.E.Direct3D;
        MethodInfo request = typeof(Direct3DRuntime).GetMethod("RequestSwapChainSize", BindingFlags.NonPublic | BindingFlags.Instance)!;
        h.Check("before any swap chain, the back buffer is the screen",
            d3d.BackBufferWidth == Direct3DRuntime.ScreenWidth && d3d.BackBufferHeight == Direct3DRuntime.ScreenHeight);
        request.Invoke(d3d, [480u, 800u]);
        h.Check("a 480x800 swap chain is honoured", d3d.BackBufferWidth == 480 && d3d.BackBufferHeight == 800);
        request.Invoke(d3d, [0u, 0u]);
        h.Check("a zero size means the screen's", d3d.BackBufferWidth == Direct3DRuntime.ScreenWidth && d3d.BackBufferHeight == Direct3DRuntime.ScreenHeight);
        request.Invoke(d3d, [100000u, 5u]);
        h.Check("a nonsense size means the screen's", d3d.BackBufferWidth == Direct3DRuntime.ScreenWidth);
    }

    private static void DisplayProperties(Harness h)
    {
        long statics = h.E.WinRt.GetActivationFactory("Windows.Graphics.Display.DisplayProperties")!.Value;
        int scaleSlot = WinRtSystemInterfaces.InspectableSlots + 6;
        int dpiSlot = WinRtSystemInterfaces.InspectableSlots + 7;

        h.E.WriteUInt32(StackScratch, 0);
        long hr = h.CallMethod(statics, scaleSlot, StackScratch);
        h.Check("DisplayProperties.ResolutionScale answers the model's scale",
            hr == SOk && h.U32(StackScratch) == Wp8Display.ResolutionScaleEnum, $"{h.U32(StackScratch)}");

        h.WriteF32(StackScratch, 0);
        hr = h.CallMethod(statics, dpiSlot, StackScratch);
        h.Check("DisplayProperties.LogicalDpi is 96 * scale / 100",
            hr == SOk && Math.Abs(h.F32(StackScratch) - Wp8Display.LogicalDpi) < 0.01, $"{h.F32(StackScratch)}");
    }

    private static void Shells(Harness h)
    {
        foreach (string dll in new[] { "AlexXamlComponent.dll", "MC4HybridComponent.dll" })
        {
            XamlShell shell = XamlShells.ForComponent(dll)!;
            SizeF Arg(string method) => (SizeF)shell.Loaded.First(s => s.Method == method).Arguments[0]!;
            h.Check($"{dll}: WindowBounds is in DIPs", Arg("set_WindowBounds") == new SizeF(Wp8Display.LogicalWidth, Wp8Display.LogicalHeight));
            h.Check($"{dll}: NativeResolution and RenderResolution are physical pixels",
                Arg("set_NativeResolution") == new SizeF(Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight) &&
                Arg("set_RenderResolution") == new SizeF(Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight));
            h.Check($"{dll}: the grid's render target is physical pixels",
                shell.RenderTarget == new SizeF(Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight));
        }

        XamlShell alex = XamlShells.ForComponent("AlexXamlComponent.dll")!;
        h.Check("Amazing Alex's ScaleFactor is the model's scale",
            alex.Loaded.First(s => s.Method == "set_ScaleFactor").Arguments[0] is float f && (int)f == Wp8Display.Scale);
    }

    private static void Pointer(Harness h)
    {
        // ToWindow takes a point in the landscape composition (host pixels) and answers the portrait
        // DIPs a PointerPoint carries.
        MethodInfo toWindow = typeof(WinRtRuntime).GetMethod("ToWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        WinRtRuntime winRt = h.E.WinRt;
        string? saved = winRt.PointerRotation;
        try
        {
            winRt.PointerRotation = "ccw";
            (float X, float Y) At(float x, float y) => ((float, float))toWindow.Invoke(winRt, [x, y])!;
            (float x0, float y0) = At(0, 0);
            (float x1, float y1) = At(FrameCapture.Width, FrameCapture.Height);
            h.Check("composition top-left is the window's top-right, in DIPs",
                Math.Abs(x0 - Wp8Display.LogicalWidth) < 0.01 && Math.Abs(y0) < 0.01, $"({x0},{y0})");
            h.Check("composition bottom-right is the window's bottom-left, in DIPs",
                Math.Abs(x1) < 0.01 && Math.Abs(y1 - Wp8Display.LogicalHeight) < 0.01, $"({x1},{y1})");
            (float xc, float yc) = At(FrameCapture.Width / 2f, FrameCapture.Height / 2f);
            h.Check("the centre stays the centre",
                Math.Abs(xc - Wp8Display.LogicalWidth / 2) < 0.01 && Math.Abs(yc - Wp8Display.LogicalHeight / 2) < 0.01);

            winRt.PointerRotation = "none";
            (float xn, float yn) = At(FrameCapture.Width, FrameCapture.Height);
            h.Check("unrotated, the composition's corner is the landscape DIP corner (800x480)",
                Math.Abs(xn - Wp8Display.LogicalHeight) < 0.01 && Math.Abs(yn - Wp8Display.LogicalWidth) < 0.01, $"({xn},{yn})");
        }
        finally
        {
            winRt.PointerRotation = saved;
        }
    }
}
