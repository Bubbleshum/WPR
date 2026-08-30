using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// The graphics RHI (rendering hardware interface) seam — Stage 5c
	/// (Plans/STAGE5C-SCOPE.md). The WPR-owned XNA runtime
	/// (<c>GraphicsDevice</c>/<c>SpriteBatch</c>/<c>Texture2D</c>/<c>Effect</c>/buffers/states)
	/// issues every GPU operation through this interface instead of calling native code, so the
	/// XNA type system stays backend-agnostic. A rendering backend implements it:
	/// <c>WPR.Backend.FNA.FnaGraphicsBackend</c> forwards to FNA3D; a future
	/// <c>WPR.Backend.Direct3D11</c> would forward to Vortice.
	///
	/// <para><b>Shape:</b> this deliberately mirrors the FNA3D C API almost 1:1 — opaque
	/// <see cref="IntPtr"/> handles (device / texture / buffer / effect / renderbuffer / query)
	/// plus WPR-owned value types (the enums/structs vendored into WPR.Framework.Xna in 5b, e.g.
	/// <see cref="SurfaceFormat"/>, <see cref="Blend"/>, <see cref="Color"/>). FNA3D is already a
	/// clean low-level RHI whose own C structs are written in exactly these types, so the XNA
	/// runtime's leaf calls (<c>FNA3D_Foo(...)</c>) become <c>_backend.Foo(...)</c> mechanically in
	/// 5c-1/5c-2. The compound state structs cross the seam as the layout-identical <c>Rhi*</c>
	/// structs (see RhiStructs.cs); vertex-binding / render-target arrays cross as an
	/// <see cref="IntPtr"/> to a pinned array of those structs.</para>
	///
	/// <para>It lives in WPR.Framework.Xna (not WPR.Abstractions) on purpose: the RHI vocabulary
	/// <em>is</em> the XNA enums, which live here — putting it in the dependency-free
	/// WPR.Abstractions would force Abstractions to reference WPR.Framework.Xna and create a cycle.
	/// Any backend that renders XNA content already references the XNA type system, so co-locating
	/// the contract with it is not a layering violation.</para>
	/// </summary>
	public interface IGraphicsBackend
	{
		// ---- Driver / device lifetime ----

		/// <summary>SDL window-attribute flags the device wants (FNA3D_PrepareWindowAttributes).</summary>
		uint PrepareWindowAttributes();

		/// <summary>Drawable (pixel) size of the given window handle.</summary>
		void GetDrawableSize(IntPtr window, out int w, out int h);

		/// <summary>Creates the graphics device for the given presentation parameters; returns an opaque device handle.</summary>
		IntPtr CreateDevice(ref RhiPresentationParameters presentationParameters, byte debugMode);

		/// <summary>Destroys a device handle from <see cref="CreateDevice"/>.</summary>
		void DestroyDevice(IntPtr device);

		// ---- Presentation ----

		/// <summary>Presents the backbuffer. <paramref name="sourceRectangle"/>/<paramref name="destinationRectangle"/>
		/// are <see cref="IntPtr.Zero"/> for the whole buffer, or a pointer to a pinned <see cref="Rectangle"/>.</summary>
		void SwapBuffers(IntPtr device, Rectangle? sourceRectangle, Rectangle? destinationRectangle, IntPtr overrideWindowHandle);

		// ---- Drawing ----

		void Clear(IntPtr device, ClearOptions options, ref Vector4 color, float depth, int stencil);

		void DrawIndexedPrimitives(
			IntPtr device, PrimitiveType primitiveType, int baseVertex, int minVertexIndex,
			int numVertices, int startIndex, int primitiveCount, IntPtr indices, IndexElementSize indexElementSize);

		void DrawInstancedPrimitives(
			IntPtr device, PrimitiveType primitiveType, int baseVertex, int minVertexIndex,
			int numVertices, int startIndex, int primitiveCount, int instanceCount, IntPtr indices, IndexElementSize indexElementSize);

		void DrawPrimitives(IntPtr device, PrimitiveType primitiveType, int vertexStart, int primitiveCount);

		// ---- Mutable render states ----

		void SetViewport(IntPtr device, ref RhiViewport viewport);
		void SetScissorRect(IntPtr device, ref Rectangle scissor);
		void GetBlendFactor(IntPtr device, out Color blendFactor);
		void SetBlendFactor(IntPtr device, ref Color blendFactor);
		int  GetMultiSampleMask(IntPtr device);
		void SetMultiSampleMask(IntPtr device, int mask);
		int  GetReferenceStencil(IntPtr device);
		void SetReferenceStencil(IntPtr device, int reference);

		// ---- Immutable render states ----

		void SetBlendState(IntPtr device, ref RhiBlendState blendState);
		void SetDepthStencilState(IntPtr device, ref RhiDepthStencilState depthStencilState);
		void ApplyRasterizerState(IntPtr device, ref RhiRasterizerState rasterizerState);
		void VerifySampler(IntPtr device, int index, IntPtr texture, ref RhiSamplerState sampler);
		void VerifyVertexSampler(IntPtr device, int index, IntPtr texture, ref RhiSamplerState sampler);

		/// <summary><paramref name="bindings"/> points to a pinned array of <see cref="RhiVertexBufferBinding"/>.</summary>
		void ApplyVertexBufferBindings(IntPtr device, IntPtr bindings, int numBindings, byte bindingsUpdated, int baseVertex);

		// ---- Render targets ----

		/// <summary><paramref name="renderTargets"/> points to a pinned array of <see cref="RhiRenderTargetBinding"/> (or <see cref="IntPtr.Zero"/> for the backbuffer).</summary>
		void SetRenderTargets(IntPtr device, IntPtr renderTargets, int numRenderTargets, IntPtr depthStencilBuffer, DepthFormat depthFormat, byte preserveDepthStencilContents);
		void ResolveTarget(IntPtr device, ref RhiRenderTargetBinding target);

		// ---- Backbuffer ----

		void ResetBackbuffer(IntPtr device, ref RhiPresentationParameters presentationParameters);
		void ReadBackbuffer(IntPtr device, int x, int y, int w, int h, IntPtr data, int dataLength);
		void GetBackbufferSize(IntPtr device, out int w, out int h);
		SurfaceFormat GetBackbufferSurfaceFormat(IntPtr device);
		DepthFormat GetBackbufferDepthFormat(IntPtr device);
		int GetBackbufferMultiSampleCount(IntPtr device);

		// ---- Textures ----

		IntPtr CreateTexture2D(IntPtr device, SurfaceFormat format, int width, int height, int levelCount, byte isRenderTarget);
		IntPtr CreateTexture3D(IntPtr device, SurfaceFormat format, int width, int height, int depth, int levelCount);
		IntPtr CreateTextureCube(IntPtr device, SurfaceFormat format, int size, int levelCount, byte isRenderTarget);
		void AddDisposeTexture(IntPtr device, IntPtr texture);
		void SetTextureData2D(IntPtr device, IntPtr texture, int x, int y, int w, int h, int level, IntPtr data, int dataLength);
		void SetTextureData3D(IntPtr device, IntPtr texture, int x, int y, int z, int w, int h, int d, int level, IntPtr data, int dataLength);
		void SetTextureDataCube(IntPtr device, IntPtr texture, int x, int y, int w, int h, CubeMapFace cubeMapFace, int level, IntPtr data, int dataLength);
		void SetTextureDataYUV(IntPtr device, IntPtr y, IntPtr u, IntPtr v, int yWidth, int yHeight, int uvWidth, int uvHeight, IntPtr data, int dataLength);
		void GetTextureData2D(IntPtr device, IntPtr texture, int x, int y, int w, int h, int level, IntPtr data, int dataLength);
		void GetTextureData3D(IntPtr device, IntPtr texture, int x, int y, int z, int w, int h, int d, int level, IntPtr data, int dataLength);
		void GetTextureDataCube(IntPtr device, IntPtr texture, int x, int y, int w, int h, CubeMapFace cubeMapFace, int level, IntPtr data, int dataLength);

		// ---- Renderbuffers ----

		IntPtr GenColorRenderbuffer(IntPtr device, int width, int height, SurfaceFormat format, int multiSampleCount, IntPtr texture);
		IntPtr GenDepthStencilRenderbuffer(IntPtr device, int width, int height, DepthFormat format, int multiSampleCount);
		void AddDisposeRenderbuffer(IntPtr device, IntPtr renderbuffer);

		// ---- Vertex buffers ----

		IntPtr GenVertexBuffer(IntPtr device, byte dynamic, BufferUsage usage, int sizeInBytes);
		void AddDisposeVertexBuffer(IntPtr device, IntPtr buffer);
		void SetVertexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int elementCount, int elementSizeInBytes, int vertexStride, SetDataOptions options);
		void GetVertexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int elementCount, int elementSizeInBytes, int vertexStride);

		// ---- Index buffers ----

		IntPtr GenIndexBuffer(IntPtr device, byte dynamic, BufferUsage usage, int sizeInBytes);
		void AddDisposeIndexBuffer(IntPtr device, IntPtr buffer);
		void SetIndexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int dataLength, SetDataOptions options);
		void GetIndexBufferData(IntPtr device, IntPtr buffer, int offsetInBytes, IntPtr data, int dataLength);

		// ---- Effects ----

		void CreateEffect(IntPtr device, byte[] effectCode, int length, out IntPtr effect, out IntPtr effectData);
		void CloneEffect(IntPtr device, IntPtr cloneSource, out IntPtr effect, out IntPtr effectData);
		void AddDisposeEffect(IntPtr device, IntPtr effect);
		void SetEffectTechnique(IntPtr device, IntPtr effect, IntPtr technique);
		void ApplyEffect(IntPtr device, IntPtr effect, uint pass, IntPtr stateChanges);
		void BeginPassRestore(IntPtr device, IntPtr effect, IntPtr stateChanges);
		void EndPassRestore(IntPtr device, IntPtr effect);

		// ---- Queries ----

		IntPtr CreateQuery(IntPtr device);
		void AddDisposeQuery(IntPtr device, IntPtr query);
		void QueryBegin(IntPtr device, IntPtr query);
		void QueryEnd(IntPtr device, IntPtr query);
		byte QueryComplete(IntPtr device, IntPtr query);
		int QueryPixelCount(IntPtr device, IntPtr query);

		// ---- Feature queries ----

		byte SupportsDXT1(IntPtr device);
		byte SupportsS3TC(IntPtr device);
		byte SupportsBC7(IntPtr device);
		byte SupportsHardwareInstancing(IntPtr device);
		byte SupportsNoOverwrite(IntPtr device);
		byte SupportsSRGBRenderTargets(IntPtr device);
		void GetMaxTextureSlots(IntPtr device, out int textures, out int vertexTextures);
		int GetMaxMultiSampleCount(IntPtr device, SurfaceFormat format, int preferredMultiSampleCount);

		/// <summary>Debug marker for GPU capture tools (no-op on release backends).</summary>
		void SetStringMarker(IntPtr device, string text);

		// ---- Image load/save (device-independent codec; Texture2D.FromStream / SaveAsPng/Jpeg) ----

		/// <summary>Decodes an image stream to a native BGRA pixel buffer. Free it with <see cref="FreeImage"/>.</summary>
		IntPtr ReadImageStream(Stream stream, out int width, out int height, out int len, int forceW = -1, int forceH = -1, bool zoom = false);
		void FreeImage(IntPtr mem);
		void WritePNGStream(Stream stream, int srcW, int srcH, int dstW, int dstH, IntPtr data);
		void WriteJPGStream(Stream stream, int srcW, int srcH, int dstW, int dstH, IntPtr data, int quality);

		// ---- Adapter / display enumeration (was FNAPlatform.GetGraphicsAdapters/GetCurrentDisplayMode) ----

		/// <summary>Enumerates the graphics adapters (backend constructs the WPR-owned instances).</summary>
		GraphicsAdapter[] GetGraphicsAdapters();
		DisplayMode GetCurrentDisplayMode(int adapterIndex);
	}
}
