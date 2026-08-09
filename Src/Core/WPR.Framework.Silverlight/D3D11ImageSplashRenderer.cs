#if WPR_D3D11
using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using AvDrawingContext = Avalonia.Media.DrawingContext;
using AvRect = Avalonia.Rect;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// A real D3D11 textured-quad renderer that draws an image full-screen with a slow Ken
    /// Burns pan/zoom. Built so app-specific renderers can fork this and replace the texture
    /// load + animation with whatever they need.
    ///
    /// What it does each frame:
    ///   1. Compiles an inline pass-through vertex shader and a sampling pixel shader once,
    ///      with Ken Burns UV transform driven by an elapsed-time constant buffer.
    ///   2. Draws a fullscreen quad textured with the supplied image.
    ///   3. Hands the result to <see cref="D3D11SurfaceRenderer"/> which copies into a CPU
    ///      staging texture and blits to an Avalonia bitmap.
    /// </summary>
    public sealed class D3D11ImageSplashRenderer : IBackgroundRenderer, IDisposable
    {
        private readonly string _imagePath;

        private D3D11SurfaceRenderer? _surface;
        private bool _resourcesReady;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _ps;
        private ID3D11InputLayout? _layout;
        private ID3D11Buffer? _vertexBuffer;
        private ID3D11Buffer? _constantBuffer;
        private ID3D11ShaderResourceView? _textureSrv;
        private ID3D11SamplerState? _sampler;
        private ID3D11BlendState? _blend;

        private float _imageAspect = 1f;

        public D3D11ImageSplashRenderer(string imagePath)
        {
            _imagePath = imagePath ?? throw new ArgumentNullException(nameof(imagePath));
        }

        public void OnContentProviderAttached(object? contentProvider) { }
        public void OnManipulationHandlerAttached(object? manipulationHandler) { }

        public bool Render(AvDrawingContext ctx, AvRect bounds)
        {
            _surface ??= new D3D11SurfaceRenderer(DrawScene);
            _surface.Render(ctx, bounds);
            return true;
        }

        // Vertex layout: position (float2) + uv (float2). 4 verts as a triangle strip = 1 quad.
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex { public float X, Y, U, V; }

        // Constant buffer: TimeSeconds, ViewportAspect, ImageAspect, Pad. 16 bytes.
        [StructLayout(LayoutKind.Sequential, Size = 16)]
        private struct Constants { public float Time, ViewportAspect, ImageAspect, Pad; }

        private const string Hlsl = @"
cbuffer Cb : register(b0) {
    float Time;
    float ViewportAspect;
    float ImageAspect;
    float _pad;
};
struct VsIn  { float2 pos : POSITION; float2 uv : TEXCOORD; };
struct VsOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };

VsOut VS(VsIn i) {
    VsOut o;
    o.pos = float4(i.pos, 0, 1);

    // Ken Burns: slow zoom (1.0 -> 1.15 over ~12s) and gentle drift.
    float zoom = 1.0 + 0.075 * (1.0 + sin(Time * 0.5));
    float2 drift = float2(0.04 * sin(Time * 0.27), 0.03 * cos(Time * 0.21));

    // Center, scale by zoom, then aspect-correct so the image fills the viewport without skew.
    float2 uv = (i.uv - 0.5) / zoom + 0.5 + drift;

    // Scale UV so the image fills the smaller dimension (UniformToFill style).
    float vp = ViewportAspect;
    float im = ImageAspect;
    if (vp > im) {
        // Viewport wider than image: stretch image horizontally, crop top/bottom.
        float s = im / vp;
        uv.y = (uv.y - 0.5) * s + 0.5;
    } else {
        float s = vp / im;
        uv.x = (uv.x - 0.5) * s + 0.5;
    }
    o.uv = uv;
    return o;
}

Texture2D Tex : register(t0);
SamplerState Samp : register(s0);

float4 PS(VsOut i) : SV_Target {
    float2 uv = i.uv;
    if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) return float4(0, 0, 0, 1);
    return Tex.Sample(Samp, uv);
}";

        private void CreateResources(ID3D11Device device)
        {
            // Compile shaders.
            using var vsBlob = Compile(Hlsl, "VS", "vs_4_0");
            using var psBlob = Compile(Hlsl, "PS", "ps_4_0");
            _vs = device.CreateVertexShader(vsBlob.AsSpan());
            _ps = device.CreatePixelShader(psBlob.AsSpan());

            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            };
            _layout = device.CreateInputLayout(inputElements, vsBlob.AsSpan());

            // Fullscreen quad: triangle strip with 4 vertices.
            var verts = new Vertex[]
            {
                new() { X = -1, Y = -1, U = 0, V = 1 },
                new() { X = -1, Y =  1, U = 0, V = 0 },
                new() { X =  1, Y = -1, U = 1, V = 1 },
                new() { X =  1, Y =  1, U = 1, V = 0 },
            };
            _vertexBuffer = device.CreateBuffer(verts, BindFlags.VertexBuffer, ResourceUsage.Immutable);

            _constantBuffer = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 16,
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);

            // Standard alpha-over so the dialog overlays composite cleanly on top.
            var blend = BlendDescription.AlphaBlend;
            _blend = device.CreateBlendState(blend);

            // Load the image: use Avalonia's bitmap loader (supports JPG/PNG/BMP) to get an
            // RGBA byte buffer, then upload as a D3D texture.
            using var fs = File.OpenRead(_imagePath);
            using var bmp = new Avalonia.Media.Imaging.Bitmap(fs);
            int w = bmp.PixelSize.Width;
            int h = bmp.PixelSize.Height;
            _imageAspect = (float)w / Math.Max(1, h);

            // Read the decoded image into a CPU buffer of BGRA8 bytes.
            byte[] pixels = new byte[w * h * 4];
            unsafe
            {
                fixed (byte* dst = pixels)
                {
                    bmp.CopyPixels(
                        new Avalonia.PixelRect(0, 0, w, h),
                        (IntPtr)dst,
                        pixels.Length,
                        w * 4);
                }
            }

            var texDesc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            };
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    var initial = new SubresourceData((IntPtr)p, (uint)(w * 4));
                    using var tex = device.CreateTexture2D(texDesc, new[] { initial });
                    _textureSrv = device.CreateShaderResourceView(tex);
                }
            }
        }

        private static Blob Compile(string hlsl, string entry, string profile)
        {
            var hr = Compiler.Compile(hlsl, entry, "inline.hlsl", profile, out var blob, out var errors);
            if (hr.Failure)
            {
                string msg = errors == null ? hr.ToString() : System.Text.Encoding.UTF8.GetString(errors.AsSpan());
                throw new InvalidOperationException("HLSL compile failed: " + msg);
            }
            return blob!;
        }

        private void DrawScene(ID3D11DeviceContext context, ID3D11RenderTargetView rtv,
                               double seconds, int width, int height)
        {
            if (!_resourcesReady)
            {
                using var dev = rtv.Device;
                CreateResources(dev);
                _resourcesReady = true;
            }

            // Update constants.
            float vpAspect = (float)width / Math.Max(1, height);
            var c = new Constants
            {
                Time = (float)seconds,
                ViewportAspect = vpAspect,
                ImageAspect = _imageAspect,
                Pad = 0,
            };
            context.UpdateSubresource(c, _constantBuffer!);

            // Clear.
            context.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));

            // IA / VS / PS / OM setup.
            context.IASetInputLayout(_layout);
            context.IASetVertexBuffer(0, _vertexBuffer!, sizeof(float) * 4, 0);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            context.VSSetShader(_vs);
            context.VSSetConstantBuffer(0, _constantBuffer);
            context.PSSetShader(_ps);
            context.PSSetShaderResource(0, _textureSrv);
            context.PSSetSampler(0, _sampler);

            context.OMSetRenderTargets(rtv);
            context.OMSetBlendState(_blend);

            context.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));

            context.Draw(4, 0);
        }

        public void Dispose()
        {
            _surface?.Dispose();
            _vs?.Dispose();
            _ps?.Dispose();
            _layout?.Dispose();
            _vertexBuffer?.Dispose();
            _constantBuffer?.Dispose();
            _textureSrv?.Dispose();
            _sampler?.Dispose();
            _blend?.Dispose();
        }
    }
}
#endif
