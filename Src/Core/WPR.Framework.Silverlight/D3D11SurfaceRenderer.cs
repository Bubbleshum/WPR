#if WPR_D3D11
using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using AvDrawingContext = Avalonia.Media.DrawingContext;
using AvRect = Avalonia.Rect;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Real Direct3D 11 backend for <see cref="DrawingSurfaceBackgroundGrid"/>.
    ///
    /// Maintains a single D3D11 device + offscreen render target. Each Avalonia render pass
    /// asks the renderer to produce a frame: it invokes the user-supplied <c>DrawScene</c>
    /// callback against an <see cref="ID3D11DeviceContext"/>, then maps the texture back to
    /// CPU memory and uploads to an Avalonia <see cref="WriteableBitmap"/> for compositing.
    ///
    /// CPU readback isn't fast (suitable for ~30fps test patterns and low-fidelity scenes,
    /// not high-perf gameplay) but it cleanly bridges D3D11 → Avalonia without requiring
    /// a shared-handle interop into the host's compositor. Real game-grade integration would
    /// upgrade this path to a DXGI shared resource + Avalonia OpenGL/D3D interop.
    /// </summary>
    internal sealed class D3D11SurfaceRenderer : IDisposable
    {
        private readonly Action<ID3D11DeviceContext, ID3D11RenderTargetView, double, int, int> _drawScene;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private ID3D11Texture2D? _renderTarget;
        private ID3D11RenderTargetView? _renderTargetView;
        private ID3D11Texture2D? _staging;     // CPU-readable copy

        private WriteableBitmap? _bitmap;
        private int _width;
        private int _height;

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public D3D11SurfaceRenderer(Action<ID3D11DeviceContext, ID3D11RenderTargetView, double, int, int> drawScene)
        {
            _drawScene = drawScene ?? throw new ArgumentNullException(nameof(drawScene));
            CreateDevice();
        }

        private void CreateDevice()
        {
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
            };

            var flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
            // Skip the debug layer if it's not installed — common on stock Windows.
            try
            {
                D3D11.D3D11CreateDevice(
                    null, DriverType.Hardware,
                    flags | DeviceCreationFlags.Debug, featureLevels,
                    out _device, out _, out _context).CheckError();
                return;
            }
            catch
            {
                _device = null;
                _context = null;
            }
#endif
            D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, flags, featureLevels,
                out _device, out _, out _context).CheckError();
        }

        private void EnsureSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (_renderTarget != null && _width == width && _height == height) return;

            DisposeSizedResources();

            _width = width;
            _height = height;

            var rtDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            };
            _renderTarget = _device!.CreateTexture2D(rtDesc);
            _renderTargetView = _device.CreateRenderTargetView(_renderTarget);

            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            _staging = _device.CreateTexture2D(stagingDesc);

            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        /// <summary>
        /// Render one frame and blit it into <paramref name="ctx"/> at <paramref name="bounds"/>.
        /// </summary>
        public unsafe void Render(AvDrawingContext ctx, AvRect bounds)
        {
            int w = (int)Math.Round(bounds.Width);
            int h = (int)Math.Round(bounds.Height);
            if (w <= 0 || h <= 0) return;

            EnsureSize(w, h);
            if (_renderTargetView == null || _staging == null || _bitmap == null) return;

            double seconds = _clock.Elapsed.TotalSeconds;
            _drawScene(_context!, _renderTargetView, seconds, w, h);
            _context!.Flush();

            // Copy to staging texture so we can map it.
            _context.CopyResource(_staging, _renderTarget!);

            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                using var fb = _bitmap.Lock();
                int bytesPerPixel = 4;
                int srcPitch = (int)mapped.RowPitch;
                int dstPitch = fb.RowBytes;
                int copyBytes = w * bytesPerPixel;
                byte* src = (byte*)mapped.DataPointer;
                byte* dst = (byte*)fb.Address;
                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(src + y * srcPitch, dst + y * dstPitch, copyBytes, copyBytes);
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            ctx.DrawImage(_bitmap, bounds);
        }

        private void DisposeSizedResources()
        {
            _renderTargetView?.Dispose(); _renderTargetView = null;
            _renderTarget?.Dispose();     _renderTarget = null;
            _staging?.Dispose();          _staging = null;
            _bitmap?.Dispose();           _bitmap = null;
        }

        public void Dispose()
        {
            DisposeSizedResources();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
#endif
