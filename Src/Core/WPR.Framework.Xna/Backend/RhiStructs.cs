using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Rhi
{
	// WPR-owned RHI marshalling structs for IGraphicsBackend. Each is a byte-for-byte mirror of the
	// corresponding internal FNA3D C struct (Src/Backends/FNA.Platform/src/Graphics/FNA3D.cs) — same field
	// order, same field types (the enums/Color are the WPR.Framework.Xna types FNA3D already uses),
	// same [StructLayout(Sequential)]. That identical layout is load-bearing: the FNA backend adapter
	// converts these to the FNA3D_* structs by field copy (the compile validates the transcription),
	// and passes pinned arrays of RhiVertexBufferBinding / RhiRenderTargetBinding straight through as
	// pointers (reinterpreted to FNA3D_* — valid only because the layouts match). If you edit a field
	// here, edit the matching FNA3D_* struct's mirror in the adapter conversion in lockstep.

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiViewport
	{
		public int x;
		public int y;
		public int w;
		public int h;
		public float minDepth;
		public float maxDepth;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiBlendState
	{
		public Blend colorSourceBlend;
		public Blend colorDestinationBlend;
		public BlendFunction colorBlendFunction;
		public Blend alphaSourceBlend;
		public Blend alphaDestinationBlend;
		public BlendFunction alphaBlendFunction;
		public ColorWriteChannels colorWriteEnable;
		public ColorWriteChannels colorWriteEnable1;
		public ColorWriteChannels colorWriteEnable2;
		public ColorWriteChannels colorWriteEnable3;
		public Color blendFactor;
		public int multiSampleMask;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiDepthStencilState
	{
		public byte depthBufferEnable;
		public byte depthBufferWriteEnable;
		public CompareFunction depthBufferFunction;
		public byte stencilEnable;
		public int stencilMask;
		public int stencilWriteMask;
		public byte twoSidedStencilMode;
		public StencilOperation stencilFail;
		public StencilOperation stencilDepthBufferFail;
		public StencilOperation stencilPass;
		public CompareFunction stencilFunction;
		public StencilOperation ccwStencilFail;
		public StencilOperation ccwStencilDepthBufferFail;
		public StencilOperation ccwStencilPass;
		public CompareFunction ccwStencilFunction;
		public int referenceStencil;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiRasterizerState
	{
		public FillMode fillMode;
		public CullMode cullMode;
		public float depthBias;
		public float slopeScaleDepthBias;
		public byte scissorTestEnable;
		public byte multiSampleAntiAlias;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiSamplerState
	{
		public TextureFilter filter;
		public TextureAddressMode addressU;
		public TextureAddressMode addressV;
		public TextureAddressMode addressW;
		public float mipMapLevelOfDetailBias;
		public int maxAnisotropy;
		public int maxMipLevel;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiVertexDeclaration
	{
		public int vertexStride;
		public int elementCount;
		public IntPtr elements; /* FNA3D_VertexElement* */
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiVertexBufferBinding
	{
		public IntPtr vertexBuffer; /* FNA3D_Buffer* */
		public RhiVertexDeclaration vertexDeclaration;
		public int vertexOffset;
		public int instanceFrequency;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiRenderTargetBinding
	{
		public byte type;
		public int data1; /* width for 2D, size for Cube */
		public int data2; /* height for 2D, face for Cube */
		public int levelCount;
		public int multiSampleCount;
		public IntPtr texture;
		public IntPtr colorBuffer;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RhiPresentationParameters
	{
		public int backBufferWidth;
		public int backBufferHeight;
		public SurfaceFormat backBufferFormat;
		public int multiSampleCount;
		public IntPtr deviceWindowHandle;
		public byte isFullScreen;
		public DepthFormat depthStencilFormat;
		public PresentInterval presentationInterval;
		public DisplayOrientation displayOrientation;
		public RenderTargetUsage renderTargetUsage;
	}
}
