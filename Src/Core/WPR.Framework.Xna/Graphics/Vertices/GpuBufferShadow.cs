#region Using Statements
using System;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	/// <summary>
	/// A CPU-side mirror of a GPU vertex/index buffer's contents, used to serve
	/// <c>GetData</c> without asking the driver to read back from the GPU.
	/// </summary>
	/// <remarks>
	/// <para>
	/// FNA3D's OpenGL driver implements <c>OPENGL_GetVertexBufferData</c> and
	/// <c>OPENGL_GetIndexBufferData</c> with <c>glGetBufferSubData</c>, which is
	/// declared <c>GL_PROC(NonES3, ...)</c> in FNA3D_Driver_OpenGL_glfuncs.h and is
	/// therefore never resolved under OpenGL ES — the function pointer stays NULL.
	/// The only guard on the call is an <c>SDL_assert(renderer->supports_NonES3)</c>,
	/// which is compiled out of the prebuilt release libFNA3D.so we ship, and the
	/// driver's own init check only requires <c>supports_NonES3</c> on the non-ES
	/// branch — so device creation succeeds and the first GetData branches to
	/// address 0. On Android, where fna3d.env forces the OpenGL driver, that is a
	/// hard SIGSEGV: "Cause: null pointer dereference, pc 0000000000000000".
	/// </para>
	/// <para>
	/// Rebuilding FNA3D to fix it at source is not an option here: libFNA3D.so ships
	/// prebuilt and checked in, the vendored C under FNA.Platform/lib is not compiled
	/// by any build in this repo, and the build machine has neither an NDK nor cmake.
	/// So the readback is served from a copy of whatever was uploaded instead.
	/// </para>
	/// <para>
	/// This is complete for XNA 4.0 rather than a partial workaround, because a
	/// vertex or index buffer's contents can only ever come from SetData or
	/// SetDataPointerEXT — the API has no GPU-side write path for one. A GetData
	/// with no preceding write would have read uninitialised GPU memory, so that
	/// case deliberately falls back to the driver rather than inventing an answer;
	/// it is no worse than the behaviour it replaces.
	/// </para>
	/// <para>
	/// The backing array is allocated lazily on the first write, so a buffer that is
	/// never written costs nothing.
	/// </para>
	/// </remarks>
	internal sealed class GpuBufferShadow
	{
		#region Private Variables

		private readonly int sizeInBytes;
		private byte[] bytes;

		#endregion

		#region Internal Constructor

		internal GpuBufferShadow(int sizeInBytes)
		{
			this.sizeInBytes = (sizeInBytes > 0) ? sizeInBytes : 0;
		}

		#endregion

		#region Internal Write Methods

		/// <summary>
		/// Mirrors a write that has just been handed to the graphics backend. The
		/// pointer and length must be the same ones the backend was given.
		/// </summary>
		internal void Write(int offsetInBytes, IntPtr source, int lengthInBytes)
		{
			if (	sizeInBytes == 0 ||
				source == IntPtr.Zero ||
				lengthInBytes <= 0 ||
				offsetInBytes < 0 ||
				offsetInBytes >= sizeInBytes	)
			{
				return;
			}

			int count = Math.Min(lengthInBytes, sizeInBytes - offsetInBytes);
			if (count <= 0)
			{
				return;
			}

			if (bytes == null)
			{
				bytes = new byte[sizeInBytes];
			}
			Marshal.Copy(source, bytes, offsetInBytes, count);
		}

		/// <summary>
		/// Mirrors SetDataOptions.Discard, which orphans the GPU buffer and leaves
		/// its contents undefined until they are written again.
		/// </summary>
		internal void Discard()
		{
			if (bytes != null)
			{
				Array.Clear(bytes, 0, bytes.Length);
			}
		}

		#endregion

		#region Internal Read Methods

		/// <summary>
		/// Serves a contiguous read, matching OPENGL_GetIndexBufferData. Returns
		/// false if nothing has been written yet, in which case the caller must fall
		/// back to the graphics backend.
		/// </summary>
		internal bool Read(int offsetInBytes, IntPtr destination, int lengthInBytes)
		{
			/* Nothing to copy is served trivially, even with no backing array —
			 * falling through to the driver for a zero-length read would still
			 * dereference the NULL entry point this class exists to avoid.
			 */
			if (destination == IntPtr.Zero || lengthInBytes <= 0)
			{
				return true;
			}
			if (bytes == null)
			{
				return false;
			}
			if (offsetInBytes < 0 || offsetInBytes >= sizeInBytes)
			{
				return false;
			}

			int count = Math.Min(lengthInBytes, sizeInBytes - offsetInBytes);
			if (count > 0)
			{
				Marshal.Copy(bytes, offsetInBytes, destination, count);
			}
			return true;
		}

		/// <summary>
		/// Serves a strided read, matching OPENGL_GetVertexBufferData: elementCount
		/// elements are taken vertexStride apart in the buffer and packed
		/// elementSizeInBytes apart at the destination.
		/// </summary>
		internal bool ReadStrided(
			int offsetInBytes,
			IntPtr destination,
			int elementCount,
			int elementSizeInBytes,
			int vertexStride
		) {
			/* FNA3D only takes its staging-buffer path when the element is smaller
			 * than the stride; otherwise it is one contiguous copy.
			 */
			if (elementSizeInBytes >= vertexStride)
			{
				return Read(
					offsetInBytes,
					destination,
					elementCount * vertexStride
				);
			}

			if (destination == IntPtr.Zero || elementCount <= 0 || elementSizeInBytes <= 0)
			{
				return true;
			}
			if (bytes == null)
			{
				return false;
			}
			if (offsetInBytes < 0 || offsetInBytes >= sizeInBytes)
			{
				return false;
			}

			IntPtr dst = destination;
			int src = offsetInBytes;
			for (int i = 0; i < elementCount; i += 1)
			{
				if ((src + elementSizeInBytes) > sizeInBytes)
				{
					break;
				}
				Marshal.Copy(bytes, src, dst, elementSizeInBytes);
				dst += elementSizeInBytes;
				src += vertexStride;
			}
			return true;
		}

		#endregion
	}
}
