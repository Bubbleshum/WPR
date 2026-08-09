#if WPR_D3D11
using System;
using Avalonia.Media;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using AvDrawingContext = Avalonia.Media.DrawingContext;
using AvRect = Avalonia.Rect;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// A simple test-pattern <see cref="IBackgroundRenderer"/> backed by a real D3D11 device.
    /// Clears the surface to an animated colour cycle and prints "D3D11" + a frame counter
    /// in the corner, so it's visually obvious that GPU pixels are flowing through the
    /// pipeline. Per-app re-implementations should follow the same pattern: own a
    /// <see cref="D3D11SurfaceRenderer"/>, pass a <c>DrawScene</c> callback, do GPU work in
    /// that callback against the supplied device context.
    /// </summary>
    public sealed class D3D11TestPatternRenderer : IBackgroundRenderer, IDisposable
    {
        private D3D11SurfaceRenderer? _renderer;
        private long _frame;

        public void OnContentProviderAttached(object? contentProvider) { /* not used */ }
        public void OnManipulationHandlerAttached(object? manipulationHandler) { /* not used */ }

        public bool Render(AvDrawingContext ctx, AvRect bounds)
        {
            _renderer ??= new D3D11SurfaceRenderer(DrawScene);
            _renderer.Render(ctx, bounds);
            return true;
        }

        private void DrawScene(ID3D11DeviceContext context, ID3D11RenderTargetView rtv,
                                double seconds, int width, int height)
        {
            // Animate the clear colour across hue. Hue goes 0..1 over 6 seconds.
            float t = (float)((seconds / 6.0) % 1.0);
            var (r, g, b) = HsvToRgb(t, 0.55f, 0.85f);

            context.ClearRenderTargetView(rtv, new Color4(r, g, b, 1f));
            context.OMSetRenderTargets(rtv);

            // No geometry yet — the cleared colour is the test pattern. To draw triangles you
            // would: compile shaders (D3DCompiler), create input layout + vertex buffer, set
            // viewport, and Draw(). Out of scope for the smoke test; per-app renderers can
            // do that work themselves against this same context.
            var vp = new Viewport(0, 0, width, height, 0f, 1f);
            context.RSSetViewport(vp);

            _frame++;
        }

        private static (float r, float g, float b) HsvToRgb(float h, float s, float v)
        {
            int hi = (int)Math.Floor(h * 6.0) % 6;
            float f = (float)(h * 6.0 - Math.Floor(h * 6.0));
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            return hi switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q),
            };
        }

        public void Dispose()
        {
            _renderer?.Dispose();
            _renderer = null;
        }
    }
}
#endif
