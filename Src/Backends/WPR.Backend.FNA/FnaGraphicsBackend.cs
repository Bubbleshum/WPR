using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WPR.Xna.Rhi;
using F3D = Microsoft.Xna.Framework.Graphics.FNA3D;

namespace WPR.Backend.FNA
{
	/// <summary>
	/// FNA implementation of the graphics RHI seam (<see cref="IGraphicsBackend"/>) — Stage 5c-0
	/// (docs/STAGE5C-SCOPE.md). Every member forwards to FNA's internal <c>FNA3D</c> P/Invoke class
	/// (reachable via <c>[InternalsVisibleTo("WPR.Backend.FNA")]</c> on FNA), so FNA's own DllImport
	/// resolver (FNADllMap) fires for the native calls. Compound state structs cross the seam as the
	/// layout-identical <see cref="WPR.Xna.Rhi"/> <c>Rhi*</c> structs and are converted here by field
	/// copy (the compile validates every field against the FNA3D_* definition); vertex-binding /
	/// render-target arrays cross as an <see cref="IntPtr"/> to a pinned array and are passed straight
	/// through (reinterpreted where FNA3D takes a typed pointer).
	///
	/// <para>Stateless: all per-device state is the <see cref="IntPtr"/> device handle the caller
	/// passes in, so a single instance is safe to register for the whole host lifetime. In 5c-1/5c-2
	/// the WPR-owned <c>GraphicsDevice</c>/<c>Texture2D</c>/… move into WPR.Framework.Xna and route
	/// their (currently direct) FNA3D calls through this adapter; today nothing consumes it yet.</para>
	/// </summary>
	public sealed class FnaGraphicsBackend : IGraphicsBackend
	{
		static FnaGraphicsBackend()
		{
			// The two structs crossing the seam as reinterpreted pointer arrays MUST be
			// byte-identical to their FNA3D_* counterparts. Fail loud at first construction
			// (host startup) rather than corrupt GPU state later.
			AssertSameSize<RhiVertexBufferBinding, F3D.FNA3D_VertexBufferBinding>();
			AssertSameSize<RhiRenderTargetBinding, F3D.FNA3D_RenderTargetBinding>();
		}

		private static void AssertSameSize<TWpr, TFna>()
		{
			int a = Marshal.SizeOf<TWpr>();
			int b = Marshal.SizeOf<TFna>();
			if (a != b)
				throw new InvalidOperationException(
					$"RHI layout mismatch: {typeof(TWpr).Name} ({a} bytes) != {typeof(TFna).Name} ({b} bytes). " +
					"The Rhi* struct in WPR.Framework.Xna and its FNA3D_* mirror have diverged.");
		}

		// ---- Driver / device lifetime ----

		public uint PrepareWindowAttributes() => F3D.FNA3D_PrepareWindowAttributes();

		public void GetDrawableSize(IntPtr window, out int w, out int h) =>
			F3D.FNA3D_GetDrawableSize(window, out w, out h);

		public IntPtr CreateDevice(ref RhiPresentationParameters presentationParameters, byte debugMode)
		{
			F3D.FNA3D_PresentationParameters pp = ToFna(in presentationParameters);
			return F3D.FNA3D_CreateDevice(ref pp, debugMode);
		}

		public void DestroyDevice(IntPtr device) => F3D.FNA3D_DestroyDevice(device);

		// ---- Presentation ----

		public void SwapBuffers(IntPtr device, Rectangle? sourceRectangle, Rectangle? destinationRectangle, IntPtr overrideWindowHandle)
		{
			// Dispatch to FNA3D's overload set (the ref/null combinations) here, in the backend.
			if (sourceRectangle.HasValue && destinationRectangle.HasValue)
			{
				Rectangle s = sourceRectangle.Value, d = destinationRectangle.Value;
				F3D.FNA3D_SwapBuffers(device, ref s, ref d, overrideWindowHandle);
			}
			else if (sourceRectangle.HasValue)
			{
				Rectangle s = sourceRectangle.Value;
				F3D.FNA3D_SwapBuffers(device, ref s, IntPtr.Zero, overrideWindowHandle);
			}
			else if (destinationRectangle.HasValue)
			{
				Rectangle d = destinationRectangle.Value;
				F3D.FNA3D_SwapBuffers(device, IntPtr.Zero, ref d, overrideWindowHandle);
			}
			else
			{
				F3D.FNA3D_SwapBuffers(device, IntPtr.Zero, IntPtr.Zero, overrideWindowHandle);
			}
		}

		// ---- Drawing ----

		public void Clear(IntPtr device, ClearOptions options, ref Vector4 color, float depth, int stencil) =>
			F3D.FNA3D_Clear(device, options, ref color, depth, stencil);

		public void DrawIndexedPrimitives(
			IntPtr device, PrimitiveType primitiveType, int baseVertex, int minVertexIndex,
			int numVertices, int startIndex, int primitiveCount, IntPtr indices, IndexElementSize indexElementSize) =>
			F3D.FNA3D_DrawIndexedPrimitives(device, primitiveType, baseVertex, minVertexIndex,
				numVertices, startIndex, primitiveCount, indices, indexElementSize);

		public void DrawInstancedPrimitives(
			IntPtr device, PrimitiveType primitiveType, int baseVertex, int minVertexIndex,
			int numVertices, int startIndex, int primitiveCount, int instanceCount, IntPtr indices, IndexElementSize indexElementSize) =>
			F3D.FNA3D_DrawInstancedPrimitives(device, primitiveType, baseVertex, minVertexIndex,
				numVertices, startIndex, primitiveCount, instanceCount, indices, indexElementSize);

		public void DrawPrimitives(IntPtr device, PrimitiveType primitiveType, int vertexStart, int primitiveCount) =>
			F3D.FNA3D_DrawPrimitives(device, primitiveType, vertexStart, primitiveCount);

		// ---- Mutable render states ----

		public void SetViewport(IntPtr device, ref RhiViewport viewport)
		{
			F3D.FNA3D_Viewport v = ToFna(in viewport);
			F3D.FNA3D_SetViewport(device, ref v);
		}

		public void SetScissorRect(IntPtr device, ref Rectangle scissor) => F3D.FNA3D_SetScissorRect(device, ref scissor);
		public void GetBlendFactor(IntPtr device, out Color blendFactor) => F3D.FNA3D_GetBlendFactor(device, out blendFactor);
		public void SetBlendFactor(IntPtr device, ref Color blendFactor) => F3D.FNA3D_SetBlendFactor(device, ref blendFactor);
		public int GetMultiSampleMask(IntPtr device) => F3D.FNA3D_GetMultiSampleMask(device);
		public void SetMultiSampleMask(IntPtr device, int mask) => F3D.FNA3D_SetMultiSampleMask(device, mask);
		public int GetReferenceStencil(IntPtr device) => F3D.FNA3D_GetReferenceStencil(device);
		public void SetReferenceStencil(IntPtr device, int reference) => F3D.FNA3D_SetReferenceStencil(device, reference);

		// ---- Immutable render states ----

		public void SetBlendState(IntPtr device, ref RhiBlendState blendState)
		{
			F3D.FNA3D_BlendState s = ToFna(in blendState);
			F3D.FNA3D_SetBlendState(device, ref s);
		}

		public void SetDepthStencilState(IntPtr device, ref RhiDepthStencilState depthStencilState)
		{
			F3D.FNA3D_DepthStencilState s = ToFna(in depthStencilState);
			F3D.FNA3D_SetDepthStencilState(device, ref s);
		}

		public void ApplyRasterizerState(IntPtr device, ref RhiRasterizerState rasterizerState)
		{
			F3D.FNA3D_RasterizerState s = ToFna(in rasterizerState);
			F3D.FNA3D_ApplyRasterizerState(device, ref s);
		}

		public void VerifySampler(IntPtr device, int index, IntPtr texture, ref RhiSamplerState sampler)
		{
			F3D.FNA3D_SamplerState s = ToFna(in sampler);
			F3D.FNA3D_VerifySampler(device, index, texture, ref s);
		}

		public void VerifyVertexSampler(IntPtr device, int index, IntPtr texture, ref RhiSamplerState sampler)
		{
			F3D.FNA3D_SamplerState s = ToFna(in sampler);
			F3D.FNA3D_VerifyVertexSampler(device, index, texture, ref s);
		}

		public unsafe void ApplyVertexBufferBindings(IntPtr device, IntPtr bindings, int numBindings, byte bindingsUpdated, int baseVertex) =>
			F3D.FNA3D_ApplyVertexBufferBindings(device, (F3D.FNA3D_VertexBufferBinding*)bindings, numBindings, bindingsUpdated, baseVertex);

		// ---- Render targets ----

		public void SetRenderTargets(IntPtr device, IntPtr renderTargets, int numRenderTargets, IntPtr depthStencilBuffer, DepthFormat depthFormat, byte preserveDepthStencilContents) =>
			F3D.FNA3D_SetRenderTargets(device, renderTargets, numRenderTargets, depthStencilBuffer, depthFormat, preserveDepthStencilContents);

		public void ResolveTarget(IntPtr device, ref RhiRenderTargetBinding target)
		{
			F3D.FNA3D_RenderTargetBinding t = ToFna(in target);
			F3D.FNA3D_ResolveTarget(device, ref t);
		}

		// ---- Backbuffer ----

		public void ResetBackbuffer(IntPtr device, ref RhiPresentationParameters presentationParameters)
		{
			F3D.FNA3D_PresentationParameters pp = ToFna(in presentationParameters);
			F3D.FNA3D_ResetBackbuffer(device, ref pp);
		}

		public void ReadBackbuffer(IntPtr device, int x, int y, int w, int h, IntPtr data, int dataLength) =>
			F3D.FNA3D_ReadBackbuffer(device, x, y, w, h, data, dataLength);

		public void GetBackbufferSize(IntPtr device, out int w, out int h) => F3D.FNA3D_GetBackbufferSize(device, out w, out h);
		public SurfaceFormat GetBackbufferSurfaceFormat(IntPtr device) => F3D.FNA3D_GetBackbufferSurfaceFormat(device);
		public DepthFormat GetBackbufferDepthFormat(IntPtr device) => F3D.FNA3D_GetBackbufferDepthFormat(device);
		public int GetBackbufferMultiSampleCount(IntPtr device) => F3D.FNA3D_GetBackbufferMultiSampleCount(device);

		// ---- Textures ----

		public IntPtr CreateTexture2D(IntPtr device, SurfaceFormat format, int width, int height, int levelCount, byte isRenderTarget) =>
			F3D.FNA3D_CreateTexture2D(device, format, width, height, levelCount, isRenderTarget);

		public IntPtr CreateTexture3D(IntPtr device, SurfaceFormat format, int width, int height, int depth, int levelCount) =>
			F3D.FNA3D_CreateTexture3D(device, format, width, height, depth, levelCount);

		public IntPtr CreateTextureCube(IntPtr device, SurfaceFormat format, int size, int levelCount, byte isRenderTarget) =>
			F3D.FNA3D_CreateTextureCube(device, format, size, levelCount, isRenderTarget);

		public void AddDisposeTexture(IntPtr device, IntPtr texture) => F3D.FNA3D_AddDisposeTexture(device, texture);

		public void SetTextureData2D(IntPtr device, IntPtr texture, int x, int y, int w, int h, int level, IntPtr data, int dataLength) =>
			F3D.FNA3D_SetTextureData2D(device, texture, x, y, w, h, level, data, dataLength);

		public void SetTextureData3D(IntPtr device, IntPtr texture, int x, int y, int z, int w, int h, int d, int level, IntPtr data, int dataLength) =>
			F3D.FNA3D_SetTextureData3D(device, texture, x, y, z, w, h, d, level, data, dataLength);

		public void SetTextureDataCube(IntPtr device, IntPtr texture, int x, int y, int w, int h, CubeMapFace cubeMapFace, int level, IntPtr data, int dataLength) =>
			F3D.FNA3D_SetTextureDataCube(device, texture, x, y, w, h, cubeMapFace, level, data, dataLength);

		public void SetTextureDataYUV(IntPtr device, IntPtr y, IntPtr u, IntPtr v, int yWidth, int yHeight, int uvWidth, int uvHeight, IntPtr data, int dataLength) =>
			F3D.FNA3D_SetTextureDataYUV(device, y, u, v, yWidth, yHeight, uvWidth, uvHeight, data, dataLength);

		public void GetTextureData2D(IntPtr device, IntPtr texture, int x, int y, int w, int h, int level, IntPtr data, int dataLength) =>
			F3D.FNA3D_GetTextureData2D(device, texture, x, y, w, h, level, data, dataLength);

		public void GetTextureData3D(IntPtr device, IntPtr texture, int x, int y, int z, int w, int h, int d, int level, IntPtr data, int dataLength) =>
			F3D.FNA3D_GetTextureData3D(device, texture, x, y, z, w, h, d, level, data, dataLength);

		public void GetTextureDataCube(IntPtr device, IntPtr texture, int x, int y, int w, int h, CubeMapFace cubeMapFace, int level, IntPtr data, int dataLength) =>
			F3D.FNA3D_GetTextureDataCube(device, texture, x, y, w, h, cubeMapFace, level, data, dataLength);

		// ---- Renderbuffers ----

		public IntPtr GenColorRenderbuffer(IntPtr device, int width, int height, SurfaceFormat format, int multiSampleCount, IntPtr texture) =>
			F3D.FNA3D_GenColorRenderbuffer(device, width, height, format, multiSampleCount, texture);

		public IntPtr GenDepthStencilRenderbuffer(IntPtr device, int width, int height, DepthFormat format, int multiSampleCount) =>
			F3D.FNA3D_GenDepthStencilRenderbuffer(device, width, height, format, multiSampleCount);

		public void AddDisposeRenderbuffer(IntPtr device, IntPtr renderbuffer) => F3D.FNA3D_AddDisposeRenderbuffer(device, renderbuffer);

		// ---- Vertex buffers ----

		public IntPtr GenVertexBuffer(IntPtr device, byte dynamic, BufferUsage usage, int sizeInBytes) =>
			F3D.FNA3D_GenVertexBuffer(device, dynamic, usage, sizeInBytes);

		public void AddDisposeVertexBuffer(IntPtr device, IntPtr buffer) => F3D.FNA3D_AddDisposeVertexBuffer(device, buffer);

		public void SetVertexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int elementCount, int elementSizeInBytes, int vertexStride, SetDataOptions options) =>
			F3D.FNA3D_SetVertexBufferData(device, buffer, offsetInBytes, data, elementCount, elementSizeInBytes, vertexStride, options);

		public void GetVertexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int elementCount, int elementSizeInBytes, int vertexStride) =>
			F3D.FNA3D_GetVertexBufferData(device, buffer, offsetInBytes, data, elementCount, elementSizeInBytes, vertexStride);

		// ---- Index buffers ----

		public IntPtr GenIndexBuffer(IntPtr device, byte dynamic, BufferUsage usage, int sizeInBytes) =>
			F3D.FNA3D_GenIndexBuffer(device, dynamic, usage, sizeInBytes);

		public void AddDisposeIndexBuffer(IntPtr device, IntPtr buffer) => F3D.FNA3D_AddDisposeIndexBuffer(device, buffer);

		public void SetIndexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int dataLength, SetDataOptions options) =>
			F3D.FNA3D_SetIndexBufferData(device, buffer, offsetInBytes, data, dataLength, options);

		public void GetIndexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int dataLength) =>
			F3D.FNA3D_GetIndexBufferData(device, buffer, offsetInBytes, data, dataLength);

		// ---- Effects ----

		public void CreateEffect(IntPtr device, byte[] effectCode, int length, out IntPtr effect, out IntPtr effectData) =>
			F3D.FNA3D_CreateEffect(device, effectCode, length, out effect, out effectData);

		public void CloneEffect(IntPtr device, IntPtr cloneSource, out IntPtr effect, out IntPtr effectData) =>
			F3D.FNA3D_CloneEffect(device, cloneSource, out effect, out effectData);

		public void AddDisposeEffect(IntPtr device, IntPtr effect) => F3D.FNA3D_AddDisposeEffect(device, effect);
		public void SetEffectTechnique(IntPtr device, IntPtr effect, IntPtr technique) => F3D.FNA3D_SetEffectTechnique(device, effect, technique);
		public void ApplyEffect(IntPtr device, IntPtr effect, uint pass, IntPtr stateChanges) => F3D.FNA3D_ApplyEffect(device, effect, pass, stateChanges);
		public void BeginPassRestore(IntPtr device, IntPtr effect, IntPtr stateChanges) => F3D.FNA3D_BeginPassRestore(device, effect, stateChanges);
		public void EndPassRestore(IntPtr device, IntPtr effect) => F3D.FNA3D_EndPassRestore(device, effect);

		// ---- Queries ----

		public IntPtr CreateQuery(IntPtr device) => F3D.FNA3D_CreateQuery(device);
		public void AddDisposeQuery(IntPtr device, IntPtr query) => F3D.FNA3D_AddDisposeQuery(device, query);
		public void QueryBegin(IntPtr device, IntPtr query) => F3D.FNA3D_QueryBegin(device, query);
		public void QueryEnd(IntPtr device, IntPtr query) => F3D.FNA3D_QueryEnd(device, query);
		public byte QueryComplete(IntPtr device, IntPtr query) => F3D.FNA3D_QueryComplete(device, query);
		public int QueryPixelCount(IntPtr device, IntPtr query) => F3D.FNA3D_QueryPixelCount(device, query);

		// ---- Feature queries ----

		public byte SupportsDXT1(IntPtr device) => F3D.FNA3D_SupportsDXT1(device);
		public byte SupportsS3TC(IntPtr device) => F3D.FNA3D_SupportsS3TC(device);
		public byte SupportsBC7(IntPtr device) => F3D.FNA3D_SupportsBC7(device);
		public byte SupportsHardwareInstancing(IntPtr device) => F3D.FNA3D_SupportsHardwareInstancing(device);
		public byte SupportsNoOverwrite(IntPtr device) => F3D.FNA3D_SupportsNoOverwrite(device);
		public byte SupportsSRGBRenderTargets(IntPtr device) => F3D.FNA3D_SupportsSRGBRenderTargets(device);
		public void GetMaxTextureSlots(IntPtr device, out int textures, out int vertexTextures) => F3D.FNA3D_GetMaxTextureSlots(device, out textures, out vertexTextures);
		public int GetMaxMultiSampleCount(IntPtr device, SurfaceFormat format, int preferredMultiSampleCount) => F3D.FNA3D_GetMaxMultiSampleCount(device, format, preferredMultiSampleCount);

		public void SetStringMarker(IntPtr device, string text) => F3D.FNA3D_SetStringMarker(device, text);

		// ---- Image load/save (device-independent stb_image codec in FNA3D) ----

		public IntPtr ReadImageStream(Stream stream, out int width, out int height, out int len, int forceW, int forceH, bool zoom) =>
			F3D.ReadImageStream(stream, out width, out height, out len, forceW, forceH, zoom);

		public void FreeImage(IntPtr mem) => F3D.FNA3D_Image_Free(mem);

		public void WritePNGStream(Stream stream, int srcW, int srcH, int dstW, int dstH, IntPtr data) =>
			F3D.WritePNGStream(stream, srcW, srcH, dstW, dstH, data);

		public void WriteJPGStream(Stream stream, int srcW, int srcH, int dstW, int dstH, IntPtr data, int quality) =>
			F3D.WriteJPGStream(stream, srcW, srcH, dstW, dstH, data, quality);

		// ---- Adapter / display enumeration (FNA's SDL-backed FNAPlatform) ----

		public GraphicsAdapter[] GetGraphicsAdapters() => Microsoft.Xna.Framework.FNAPlatform.GetGraphicsAdapters();
		public DisplayMode GetCurrentDisplayMode(int adapterIndex) => Microsoft.Xna.Framework.FNAPlatform.GetCurrentDisplayMode(adapterIndex);

		// ---- Rhi* -> FNA3D_* field-copy conversions (compile-validate the struct transcription) ----

		private static byte B(bool b) => (byte)(b ? 1 : 0);

		private static F3D.FNA3D_Viewport ToFna(in RhiViewport v) => new F3D.FNA3D_Viewport
		{
			x = v.x, y = v.y, w = v.w, h = v.h, minDepth = v.minDepth, maxDepth = v.maxDepth,
		};

		private static F3D.FNA3D_BlendState ToFna(in RhiBlendState s) => new F3D.FNA3D_BlendState
		{
			colorSourceBlend = s.colorSourceBlend,
			colorDestinationBlend = s.colorDestinationBlend,
			colorBlendFunction = s.colorBlendFunction,
			alphaSourceBlend = s.alphaSourceBlend,
			alphaDestinationBlend = s.alphaDestinationBlend,
			alphaBlendFunction = s.alphaBlendFunction,
			colorWriteEnable = s.colorWriteEnable,
			colorWriteEnable1 = s.colorWriteEnable1,
			colorWriteEnable2 = s.colorWriteEnable2,
			colorWriteEnable3 = s.colorWriteEnable3,
			blendFactor = s.blendFactor,
			multiSampleMask = s.multiSampleMask,
		};

		private static F3D.FNA3D_DepthStencilState ToFna(in RhiDepthStencilState s) => new F3D.FNA3D_DepthStencilState
		{
			depthBufferEnable = s.depthBufferEnable,
			depthBufferWriteEnable = s.depthBufferWriteEnable,
			depthBufferFunction = s.depthBufferFunction,
			stencilEnable = s.stencilEnable,
			stencilMask = s.stencilMask,
			stencilWriteMask = s.stencilWriteMask,
			twoSidedStencilMode = s.twoSidedStencilMode,
			stencilFail = s.stencilFail,
			stencilDepthBufferFail = s.stencilDepthBufferFail,
			stencilPass = s.stencilPass,
			stencilFunction = s.stencilFunction,
			ccwStencilFail = s.ccwStencilFail,
			ccwStencilDepthBufferFail = s.ccwStencilDepthBufferFail,
			ccwStencilPass = s.ccwStencilPass,
			ccwStencilFunction = s.ccwStencilFunction,
			referenceStencil = s.referenceStencil,
		};

		private static F3D.FNA3D_RasterizerState ToFna(in RhiRasterizerState s) => new F3D.FNA3D_RasterizerState
		{
			fillMode = s.fillMode,
			cullMode = s.cullMode,
			depthBias = s.depthBias,
			slopeScaleDepthBias = s.slopeScaleDepthBias,
			scissorTestEnable = s.scissorTestEnable,
			multiSampleAntiAlias = s.multiSampleAntiAlias,
		};

		private static F3D.FNA3D_SamplerState ToFna(in RhiSamplerState s) => new F3D.FNA3D_SamplerState
		{
			filter = s.filter,
			addressU = s.addressU,
			addressV = s.addressV,
			addressW = s.addressW,
			mipMapLevelOfDetailBias = s.mipMapLevelOfDetailBias,
			maxAnisotropy = s.maxAnisotropy,
			maxMipLevel = s.maxMipLevel,
		};

		private static F3D.FNA3D_RenderTargetBinding ToFna(in RhiRenderTargetBinding b) => new F3D.FNA3D_RenderTargetBinding
		{
			type = b.type,
			data1 = b.data1,
			data2 = b.data2,
			levelCount = b.levelCount,
			multiSampleCount = b.multiSampleCount,
			texture = b.texture,
			colorBuffer = b.colorBuffer,
		};

		private static F3D.FNA3D_PresentationParameters ToFna(in RhiPresentationParameters p) => new F3D.FNA3D_PresentationParameters
		{
			backBufferWidth = p.backBufferWidth,
			backBufferHeight = p.backBufferHeight,
			backBufferFormat = p.backBufferFormat,
			multiSampleCount = p.multiSampleCount,
			deviceWindowHandle = p.deviceWindowHandle,
			isFullScreen = p.isFullScreen,
			depthStencilFormat = p.depthStencilFormat,
			presentationInterval = p.presentationInterval,
			displayOrientation = p.displayOrientation,
			renderTargetUsage = p.renderTargetUsage,
		};
	}
}
