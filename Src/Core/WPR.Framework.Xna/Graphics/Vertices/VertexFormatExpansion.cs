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
	/// <b>Why unconditionally, rather than only where unsupported.</b> Expanding to
	/// float is lossless and lands on formats every device supports, so doing it
	/// always is correct everywhere and needs no per-device capability query — which
	/// would otherwise mean a new FNA3D vtable entry, a P/Invoke, and a seam member,
	/// for a decision about three rarely-used formats. The cost is a wider buffer on
	/// devices that would have coped; WP7-era meshes make that immaterial. If it ever
	/// stops being immaterial, add the query and gate <see cref="NeedsExpansion"/>.
	/// </para>
	/// <para>
	/// The game keeps seeing the <see cref="VertexDeclaration"/> it created. Only the
	/// binding path — <c>GraphicsDevice.PrepareVertexBindingArray</c> — is shown the
	/// translated one, so nothing the game can observe changes shape.
	/// </para>
	/// </remarks>
	internal static class VertexFormatExpansion
	{
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
			return	format == VertexElementFormat.Byte4 ||
				format == VertexElementFormat.Short2 ||
				format == VertexElementFormat.Short4;
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
			if (original == null)
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
