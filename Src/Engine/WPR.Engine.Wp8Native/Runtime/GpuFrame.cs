namespace WPR.Wp8Native
{
    /// <summary>
    /// One presented frame as an ordered list of GPU commands, for a host that replays them on
    /// its own GPU with the game's shaders - the 3D path. <see cref="FrameDrawList"/> is the 2D
    /// path's equivalent, already reduced to textured triangles.
    /// </summary>
    /// <remarks>
    /// Everything here is a copy taken when the command was issued (geometry, constants, decoded
    /// texture pixels), because the game rewrites its buffers between draws and the frame is
    /// replayed later on another thread.
    /// </remarks>
    public sealed class GpuFrame
    {
        public required IReadOnlyList<GpuCommand> Commands { get; init; }
    }

    public abstract record GpuCommand;

    /// <summary>A render target: the swap chain's back buffer, or a texture by its storage key.</summary>
    public readonly record struct GpuTarget(long Key, int Width, int Height)
    {
        public const long BackBufferKey = -1;

        public bool IsBackBuffer => Key == BackBufferKey;
    }

    public sealed record GpuClearColour(GpuTarget Target, float[] Colour) : GpuCommand;

    /// <param name="Flags">D3D11_CLEAR_DEPTH (1) | D3D11_CLEAR_STENCIL (2).</param>
    public sealed record GpuClearDepth(GpuTarget Target, int Flags, float Depth, byte Stencil) : GpuCommand;

    /// <summary>
    /// CopyResource, or CopySubresourceRegion of mip 0, between two render targets.
    /// </summary>
    /// <param name="DestinationX">Where the copied region lands (CopySubresourceRegion's DstX/DstY).</param>
    /// <param name="SourceBox">
    /// The region copied, as left, top, right, bottom (D3D11_BOX without the depth); null is the
    /// whole source. A region is copied texel for texel and clipped to the destination, as
    /// Direct3D does. A whole-surface copy between targets of different sizes - which Direct3D
    /// would refuse - is stretched instead: Spark2 copies its 800x480 surface into the 480x800
    /// host target, and the picture it draws there is already turned for the portrait target.
    /// </param>
    public sealed record GpuCopy(GpuTarget Destination, GpuTarget Source, int DestinationX = 0, int DestinationY = 0, int[]? SourceBox = null) : GpuCommand
    {
        public bool IsWholeSurface => DestinationX == 0 && DestinationY == 0 && SourceBox is null;
    }

    /// <summary>A shader, by the object the game created, with its DXBC.</summary>
    public sealed record GpuShader(long Key, byte[] Dxbc);

    /// <summary>What a pixel-shader texture slot held: decoded pixels, or a render target drawn earlier.</summary>
    public sealed record GpuTexture(TextureImage? Image, GpuTarget? RenderTarget);

    /// <summary>
    /// A draw and everything bound for it. State descriptions are the raw D3D11 desc bytes
    /// (D3D11_BLEND_DESC, D3D11_DEPTH_STENCIL_DESC, D3D11_RASTERIZER_DESC, D3D11_SAMPLER_DESC);
    /// null means the default state. <paramref name="Scissor"/> is the first rectangle of
    /// RSSetScissorRects (left, top, right, bottom), or null when none is set; it applies only
    /// while the rasterizer state's ScissorEnable is on.
    /// </summary>
    public sealed record GpuDraw(
        GpuShader Vertex,
        GpuShader Pixel,
        IReadOnlyDictionary<int, byte[]> VertexConstants,
        IReadOnlyDictionary<int, byte[]> PixelConstants,
        IReadOnlyDictionary<int, GpuTexture> Textures,
        IReadOnlyDictionary<int, byte[]> Samplers,
        byte[]? Blend,
        float[] BlendFactor,
        byte[]? DepthStencil,
        int StencilRef,
        byte[]? Rasterizer,
        GpuTarget Target,
        GpuTarget? DepthTarget,
        float[]? Viewport,
        FrameCapture.DrawCall Geometry,
        int[]? Scissor = null) : GpuCommand;
}
