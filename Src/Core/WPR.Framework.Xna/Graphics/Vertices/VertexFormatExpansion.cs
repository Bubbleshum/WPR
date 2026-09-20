#region Using Statements
using System;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	/// <summary>
	/// Rewrites the vertex element formats that some GPUs cannot accept into plain
	/// float ones, and converts vertex data to match.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why this exists.</b> FNA3D's Vulkan driver maps <c>Byte4</c> to
	/// <c>VK_FORMAT_R8G8B8A8_USCALED</c>, and <c>Short2</c>/<c>Short4</c> to the
	/// <c>*_SSCALED</c> equivalents. Those formats are <i>optional</i> in Vulkan for
	/// vertex buffers, and Adreno does not implement them — measured on an Adreno 750:
	/// </para>
	/// <code>
	/// [wpr-vfmt] COLOR    vkFormat=37 vertexBuffer=YES
	/// [wpr-vfmt] BYTE4    vkFormat=39 vertexBuffer=*** NO ***
	/// [wpr-vfmt] SHORT2   vkFormat=80 vertexBuffer=*** NO ***
	/// [wpr-vfmt] SHORT4   vkFormat=94 vertexBuffer=*** NO ***
	/// </code>
	/// <para>
	/// An unsupported attribute delivers nothing, so the shader reads zeros. For
	/// <c>SkinnedEffect</c> that means every vertex resolves to bone 0 — which is the
	/// identity matrix — and the mesh renders in its bind pose. That is the Mirror's
	/// Edge T-pose, and it is <b>not</b> a shader translation bug: the SPIR-V for the
	/// one- and four-bone variants is correct, and the bone matrices arrive intact.
	/// </para>
	/// <para>
	/// <b>It is gated on the VULKAN driver, and that gate is the point.</b> The defect
	/// is Vulkan's alone: only Vulkan maps these three to <c>*_SCALED</c> types, and only
	/// <c>*_SCALED</c> is optional for vertex buffers. D3D11 takes <c>Byte4</c> as
	/// <c>DXGI_FORMAT_R8G8B8A8_UINT</c> and OpenGL as an unnormalised
	/// <c>GL_UNSIGNED_BYTE</c>, both universally supported — so on those drivers this
	/// rewrite fixes nothing and is pure risk. It ran unconditionally from 2026-09-07,
	/// when Android moved to Vulkan, until 2026-09-20, which silently rewrote the vertex
	/// layout of every affected game on the DESKTOP head too, for an Adreno bug no
	/// desktop GPU has.
	/// </para>
	/// <para>
	/// Within Vulkan it is still unconditional, because narrowing to "devices that
	/// actually reject the format" needs a real
	/// <c>vkGetPhysicalDeviceFormatProperties</c> query, and that means a new FNA3D
	/// vtable entry, a P/Invoke, a seam member and a rebuild of the four prebuilt
	/// binaries. Expanding to float is lossless and lands on formats every device
	/// supports, so over-applying it within Vulkan costs only a wider buffer, which
	/// WP7-era meshes make immaterial. That query is the remaining correct fix if a
	/// Vulkan device is ever found this transform makes worse.
	/// </para>
	/// <para>
	/// <b>Scope, measured 2026-09-20.</b> Three of the 36 installed titles construct a
	/// <c>Byte4</c>/<c>Short2</c>/<c>Short4</c> vertex element at all — Mirror's Edge,
	/// Kinectimals and citizen12. For every other game <see cref="TryTranslate"/> returns
	/// null and none of this executes on any driver, so this pass is never the
	/// explanation for a game with no such element: read the declaration before
	/// suspecting it.
	/// </para>
	/// <para>
	/// The game keeps seeing the <see cref="VertexDeclaration"/> it created. Only the
	/// binding path — <c>GraphicsDevice.PrepareVertexBindingArray</c> — is shown the
	/// translated one, so nothing the game can observe changes shape.
	/// </para>
	/// </remarks>
	internal static class VertexFormatExpansion
	{
		#region Driver Gate

		/* One flag per format, because the device answers per format — it is not a
		 * single "this GPU is bad" fact. Defaulting every one of them to "expand" is
		 * deliberate: until the device has been asked we cannot prove a format works,
		 * and being wrong that way costs a wider vertex buffer while being wrong the
		 * other way costs a character stuck in bind pose.
		 */
		private static bool expandByte4 = true;
		private static bool expandShort2 = true;
		private static bool expandShort4 = true;

		/// <summary>
		/// True when at least one format still needs rewriting on this device, so the
		/// common "nothing to do" case costs one bool rather than a scan.
		/// </summary>
		internal static bool Enabled
		{
			get { return expandByte4 || expandShort2 || expandShort4; }
		}

		/// <summary>
		/// Records what the device said. Called by the graphics backend from the same
		/// call that creates the device — after the driver has come up, so there is a
		/// real device to ask, and before any <see cref="VertexBuffer"/> can exist.
		/// </summary>
		/// <param name="byte4Supported">
		/// Whether the device accepts the format in a vertex buffer. A format it
		/// supports is left exactly as the game declared it.
		/// </param>
		internal static void SetDeviceSupport(
			bool byte4Supported,
			bool short2Supported,
			bool short4Supported,
			string detail
		) {
			expandByte4 = !byte4Supported;
			expandShort2 = !short2Supported;
			expandShort4 = !short4Supported;

			/* One line per launch, unconditionally including the "nothing to expand"
			 * case: "this rewrite did not run" and "this rewrite ran and did nothing"
			 * are otherwise indistinguishable from inside a game, and the whole point
			 * of asking the device is that the answer can be read rather than guessed.
			 */
			XnaBackend.LogInfo(
				"[wpr-vfmt] device vertex formats — Byte4=" + Describe(byte4Supported) +
				" Short2=" + Describe(short2Supported) +
				" Short4=" + Describe(short4Supported) +
				" (" + detail + "); expansion " +
				(Enabled ? "ENABLED" : "not needed")
			);
		}

		private static string Describe(bool supported)
		{
			return supported ? "ok" : "UNSUPPORTED";
		}

		/// <summary>Restores the defaults on device teardown, so one game run cannot
		/// leave the next launch in this process trusting a previous device's answer.</summary>
		internal static void ClearDeviceSupport()
		{
			expandByte4 = true;
			expandShort2 = true;
			expandShort4 = true;
		}

		#endregion

		#region Format Facts

		/// <summary>Bytes one element of this format occupies in a vertex.</summary>
		internal static int SizeOf(VertexElementFormat format)
		{
			switch (format)
			{
				case VertexElementFormat.Single:				return 4;
				case VertexElementFormat.Vector2:				return 8;
				case VertexElementFormat.Vector3:				return 12;
				case VertexElementFormat.Vector4:				return 16;
				case VertexElementFormat.Color:					return 4;
				case VertexElementFormat.Byte4:					return 4;
				case VertexElementFormat.Short2:				return 4;
				case VertexElementFormat.Short4:				return 8;
				case VertexElementFormat.NormalizedShort2:		return 4;
				case VertexElementFormat.NormalizedShort4:		return 8;
				case VertexElementFormat.HalfVector2:			return 4;
				case VertexElementFormat.HalfVector4:			return 8;
				default:										return 4;
			}
		}

		/// <summary>
		/// True for the formats that map to Vulkan's optional <c>*_SCALED</c> types.
		/// The normalized and half-float variants are NOT here: they map to
		/// <c>_SNORM</c> / <c>_SFLOAT</c>, which are supported.
		/// </summary>
		internal static bool NeedsExpansion(VertexElementFormat format)
		{
			switch (format)
			{
				case VertexElementFormat.Byte4:		return expandByte4;
				case VertexElementFormat.Short2:	return expandShort2;
				case VertexElementFormat.Short4:	return expandShort4;
				default:							return false;
			}
		}

		/// <summary>The float format carrying the same component count.</summary>
		private static VertexElementFormat Expanded(VertexElementFormat format)
		{
			switch (format)
			{
				case VertexElementFormat.Byte4:		return VertexElementFormat.Vector4;
				case VertexElementFormat.Short2:	return VertexElementFormat.Vector2;
				case VertexElementFormat.Short4:	return VertexElementFormat.Vector4;
				default:							return format;
			}
		}

		#endregion

		#region Declaration Translation

		/// <summary>
		/// Builds the declaration the backend should see, or returns null when the
		/// original is already fine — which is the common case, and costs one scan.
		/// </summary>
		internal static VertexDeclaration TryTranslate(VertexDeclaration original)
		{
			if (original == null || !Enabled)
			{
				return null;
			}

			VertexElement[] src = original.elements;
			bool any = false;
			for (int i = 0; i < src.Length; i += 1)
			{
				if (NeedsExpansion(src[i].VertexElementFormat))
				{
					any = true;
					break;
				}
			}
			if (!any)
			{
				return null;
			}

			/* Offsets are recomputed by packing the elements in their original
			 * order. Keeping the source offsets is not an option once anything
			 * ahead of an element has grown, and any padding the original layout
			 * carried is not something the GPU needs to see.
			 */
			VertexElement[] dst = new VertexElement[src.Length];
			int offset = 0;
			for (int i = 0; i < src.Length; i += 1)
			{
				VertexElementFormat fmt = Expanded(src[i].VertexElementFormat);
				dst[i] = new VertexElement(
					offset,
					fmt,
					src[i].VertexElementUsage,
					src[i].UsageIndex
				);
				offset += SizeOf(fmt);
			}

			return new VertexDeclaration(offset, dst);
		}

		#endregion

		#region Data Conversion

		/// <summary>
		/// Converts <paramref name="vertexCount"/> vertices from the original layout
		/// to the translated one.
		/// </summary>
		/// <remarks>
		/// Element by element rather than whole-vertex: the source layout may carry
		/// padding between elements that the translated one does not, so copying
		/// spans would shift everything after the first gap.
		/// </remarks>
		internal static unsafe void Convert(
			IntPtr source,
			int sourceStride,
			VertexElement[] sourceElements,
			IntPtr destination,
			int destinationStride,
			VertexElement[] destinationElements,
			int vertexCount
		) {
			byte* src = (byte*) source;
			byte* dst = (byte*) destination;

			for (int v = 0; v < vertexCount; v += 1)
			{
				byte* srcVertex = src + ((long) v * sourceStride);
				byte* dstVertex = dst + ((long) v * destinationStride);

				for (int e = 0; e < sourceElements.Length; e += 1)
				{
					byte* s = srcVertex + sourceElements[e].Offset;
					byte* d = dstVertex + destinationElements[e].Offset;

					switch (sourceElements[e].VertexElementFormat)
					{
						case VertexElementFormat.Byte4:
							/* Unsigned bytes carrying whole numbers - bone
							 * indices, most often. USCALED semantics: the value
							 * is the integer, not a 0..1 fraction. */
							((float*) d)[0] = s[0];
							((float*) d)[1] = s[1];
							((float*) d)[2] = s[2];
							((float*) d)[3] = s[3];
							break;

						case VertexElementFormat.Short2:
							((float*) d)[0] = ((short*) s)[0];
							((float*) d)[1] = ((short*) s)[1];
							break;

						case VertexElementFormat.Short4:
							((float*) d)[0] = ((short*) s)[0];
							((float*) d)[1] = ((short*) s)[1];
							((float*) d)[2] = ((short*) s)[2];
							((float*) d)[3] = ((short*) s)[3];
							break;

						default:
						{
							int size = SizeOf(sourceElements[e].VertexElementFormat);
							Buffer.MemoryCopy(s, d, size, size);
							break;
						}
					}
				}
			}
		}

		#endregion
	}
}
