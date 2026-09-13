#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	public class VertexBuffer : GraphicsResource
	{
		#region Public Properties

		public BufferUsage BufferUsage
		{
			get;
			private set;
		}

		public int VertexCount
		{
			get;
			private set;
		}

		public VertexDeclaration VertexDeclaration
		{
			get;
			private set;
		}

		#endregion

		#region Internal FNA3D Variables

		internal IntPtr buffer;

		#endregion

		#region Internal CPU Shadow

		/* Serves GetData without a GPU readback, because FNA3D's OpenGL driver
		 * implements that with glGetBufferSubData — a NonES3 entry point that is
		 * NULL under OpenGL ES. See GpuBufferShadow for the full story.
		 *
		 * Null for a BufferUsage.WriteOnly buffer: GetData below throws on one
		 * before it could ever reach the readback, so shadowing it would be pure
		 * memory cost. That matters here and not in IndexBuffer — vertex data is
		 * the bulk of a game's buffer memory, and most vertex buffers are WriteOnly.
		 */
		internal readonly GpuBufferShadow shadow;

		#endregion

		#region Internal Format Translation

		/* Non-null only when some element format needs expanding. */
		private readonly VertexDeclaration translatedDeclaration;

		/// <summary>
		/// The declaration the graphics backend is bound with — the translated one
		/// where the original names a format the GPU may reject, otherwise the
		/// original itself. Games always see <see cref="VertexDeclaration"/>.
		/// </summary>
		internal VertexDeclaration BindingDeclaration
		{
			get { return translatedDeclaration ?? VertexDeclaration; }
		}

		/// <summary>
		/// Hands vertex data to the backend, converting it into the translated
		/// layout first when there is one. Everything that writes vertex data goes
		/// through here so the conversion cannot be forgotten on one overload.
		/// </summary>
		internal unsafe void SubmitVertexData(
			int offsetInBytes,
			IntPtr source,
			int elementCount,
			int elementSizeInBytes,
			int vertexStride,
			SetDataOptions options
		) {
			if (translatedDeclaration == null)
			{
				XnaBackend.Graphics.SetVertexBufferData(
					GraphicsDevice.GLDevice,
					buffer,
					offsetInBytes,
					source,
					elementCount,
					elementSizeInBytes,
					vertexStride,
					options
				);
				return;
			}

			int translatedStride = translatedDeclaration.VertexStride;

			/* offsetInBytes is a byte offset the game computed against the
			 * ORIGINAL stride, so it has to be rescaled. A partial-vertex offset
			 * has no meaning in the translated layout; leave it alone and let the
			 * write land where it would have, rather than inventing a position.
			 */
			int translatedOffset = offsetInBytes;
			if (offsetInBytes != 0 && vertexStride > 0)
			{
				if ((offsetInBytes % vertexStride) == 0)
				{
					translatedOffset = (offsetInBytes / vertexStride) * translatedStride;
				}
				else
				{
					XnaBackend.LogWarn(
						"[wpr-vfmt] vertex write at a partial-vertex offset (" +
						offsetInBytes + " into stride " + vertexStride +
						") cannot be translated; the GPU copy may be misaligned."
					);
				}
			}

			byte[] staging = new byte[(long) elementCount * translatedStride];
			fixed (byte* dst = staging)
			{
				VertexFormatExpansion.Convert(
					source,
					vertexStride,
					VertexDeclaration.elements,
					(IntPtr) dst,
					translatedStride,
					translatedDeclaration.elements,
					elementCount
				);

				XnaBackend.Graphics.SetVertexBufferData(
					GraphicsDevice.GLDevice,
					buffer,
					translatedOffset,
					(IntPtr) dst,
					elementCount,
					translatedStride,
					translatedStride,
					options
				);
			}
		}

		#endregion

		#region Public Constructors

		public VertexBuffer(
			GraphicsDevice graphicsDevice,
			VertexDeclaration vertexDeclaration,
			int vertexCount,
			BufferUsage bufferUsage
		) : this(
			graphicsDevice,
			vertexDeclaration,
			vertexCount,
			bufferUsage,
			false
		) {
		}

		public VertexBuffer(
			GraphicsDevice graphicsDevice,
			Type type,
			int vertexCount,
			BufferUsage bufferUsage
		) : this(
			graphicsDevice,
			VertexDeclaration.FromType(type),
			vertexCount,
			bufferUsage,
			false
		) {
		}

		#endregion

		#region Protected Constructor

		protected VertexBuffer(
			GraphicsDevice graphicsDevice,
			VertexDeclaration vertexDeclaration,
			int vertexCount,
			BufferUsage bufferUsage,
			bool dynamic
		) {
			if (graphicsDevice == null)
			{
				throw new ArgumentNullException("graphicsDevice");
			}

			GraphicsDevice = graphicsDevice;
			VertexDeclaration = vertexDeclaration;
			VertexCount = vertexCount;
			BufferUsage = bufferUsage;

			// Make sure the graphics device is assigned in the vertex declaration.
			if (vertexDeclaration.GraphicsDevice != graphicsDevice)
			{
				vertexDeclaration.GraphicsDevice = graphicsDevice;
			}

			/* If any element uses a format the GPU may not accept, everything the
			 * backend sees - the declaration, the stride, the data - is the
			 * translated one. See VertexFormatExpansion.
			 */
			translatedDeclaration = VertexFormatExpansion.TryTranslate(vertexDeclaration);

			if (bufferUsage != BufferUsage.WriteOnly)
			{
				/* Sized and filled in the ORIGINAL layout: GetData must hand back
				 * what the game wrote, not what the backend was given.
				 */
				shadow = new GpuBufferShadow(
					VertexCount * VertexDeclaration.VertexStride
				);
			}

			buffer = XnaBackend.Graphics.GenVertexBuffer(
				GraphicsDevice.GLDevice,
				(byte) (dynamic ? 1 : 0),
				bufferUsage,
				VertexCount * BindingDeclaration.VertexStride
			);
		}

		#endregion

		#region Destructor
		~VertexBuffer()
		{
			Dispose(false);
		}

		#endregion

		#region Protected Dispose Method

		protected override void Dispose(bool disposing)
		{
			if (!IsDisposed)
			{
				XnaBackend.Graphics.AddDisposeVertexBuffer(
					GraphicsDevice.GLDevice,
					buffer
				);
			}
			base.Dispose(disposing);
		}

		#endregion

		#region Public GetData Methods

		public void GetData<T>(T[] data) where T : struct
		{
			GetData<T>(
				0,
				data,
				0,
				data.Length,
				Marshal.SizeOf(typeof(T))
			);
		}

		public void GetData<T>(
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			GetData<T>(
				0,
				data,
				startIndex,
				elementCount,
				Marshal.SizeOf(typeof(T))
			);
		}

		public void GetData<T>(
			int offsetInBytes,
			T[] data,
			int startIndex,
			int elementCount,
			int vertexStride
		) where T : struct {
			if (data == null)
			{
				throw new ArgumentNullException("data");
			}
			if (data.Length < (startIndex + elementCount))
			{
				//throw new ArgumentOutOfRangeException(
				//	"elementCount",
				//	"This parameter must be a valid index within the array.");
                Debug.WriteLine(
                    "[ex] FNACore - VertexBuffer elementCount error: " +
                    "This parameter must be a valid index within the array."
                );
            }
			if (BufferUsage == BufferUsage.WriteOnly)
			{
				throw new NotSupportedException(
					"Calling GetData on a resource that was created with " +
					"BufferUsage.WriteOnly is not supported.");
			}

			int elementSizeInBytes = Marshal.SizeOf(typeof(T));
			if (vertexStride == 0)
			{
				vertexStride = elementSizeInBytes;
			}
			else if (vertexStride < elementSizeInBytes)
			{
				throw new ArgumentOutOfRangeException(
					"vertexStride",
					"The vertex stride is too small for the type of data requested. " +
					"This is not allowed."
				);
			}
			if (	elementCount > 1 &&
				(elementCount * vertexStride) > (VertexCount * VertexDeclaration.VertexStride)	)
			{
				throw new InvalidOperationException(
					"The array is not the correct size for the amount of data requested.");
			}

			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			IntPtr destination =
				handle.AddrOfPinnedObject() + (startIndex * elementSizeInBytes);
			if (	shadow == null ||
				!shadow.ReadStrided(
					offsetInBytes,
					destination,
					elementCount,
					elementSizeInBytes,
					vertexStride
				)	)
			{
				XnaBackend.Graphics.GetVertexBufferData(
					GraphicsDevice.GLDevice,
					buffer,
					offsetInBytes,
					destination,
					elementCount,
					elementSizeInBytes,
					vertexStride
				);
			}
			handle.Free();
		}

		#endregion

		#region Public SetData Methods

		public void SetData<T>(T[] data) where T : struct
		{
			SetData(
				0,
				data,
				0,
				data.Length,
				Marshal.SizeOf(typeof(T))
			);
		}

		public void SetData<T>(
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			SetData(
				0,
				data,
				startIndex,
				elementCount,
				Marshal.SizeOf(typeof(T))
			);
		}

		public void SetData<T>(
			int offsetInBytes,
			T[] data,
			int startIndex,
			int elementCount,
			int vertexStride
		) where T : struct {
			ErrorCheck(data, startIndex, elementCount, vertexStride);

			int elementSizeInBytes = Marshal.SizeOf(typeof(T));
			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			IntPtr source =
				handle.AddrOfPinnedObject() + (startIndex * elementSizeInBytes);
			SubmitVertexData(
				offsetInBytes,
				source,
				elementCount,
				elementSizeInBytes,
				vertexStride,
				SetDataOptions.None
			);
			if (shadow != null)
			{
				/* The ORIGINAL bytes, before any translation: GetData is defined
				 * in terms of what the game wrote. FNA3D consumes
				 * elementCount * vertexStride bytes contiguously. */
				shadow.Write(
					offsetInBytes,
					source,
					elementCount * vertexStride
				);
			}
			handle.Free();
		}

		#endregion

		#region Public Extensions

		public void SetDataPointerEXT(
			int offsetInBytes,
			IntPtr data,
			int dataLength,
			SetDataOptions options
		) {
			/* A raw byte blob with no element structure, so there is nothing to
			 * translate against - element size and stride are both 1. A game using
			 * this on a buffer whose declaration needed expanding would be writing
			 * untranslated bytes, but nothing in the WP7 catalogue does: this is an
			 * FNA extension, and the stock effects all go through SetData<T>. */
			XnaBackend.Graphics.SetVertexBufferData(
				GraphicsDevice.GLDevice,
				buffer,
				offsetInBytes,
				data,
				dataLength,
				1,
				1,
				options
			);
			if (shadow != null)
			{
				if (options == SetDataOptions.Discard)
				{
					shadow.Discard();
				}
				/* elementCount = dataLength with a stride of 1. */
				shadow.Write(offsetInBytes, data, dataLength);
			}
		}

		#endregion

		#region Internal Methods

		[System.Diagnostics.Conditional("DEBUG")]
		internal void ErrorCheck<T>(
			T[] data,
			int startIndex,
			int elementCount,
			int vertexStride
		) where T : struct {
			if (data == null)
			{
				throw new ArgumentNullException("data");
			}
			if ((startIndex + elementCount > data.Length) || elementCount <= 0)
			{
				throw new InvalidOperationException(
					"The array specified in the data parameter" +
					" is not the correct size for the amount of" +
					" data requested."
				);
			}
			if (	elementCount > 1 &&
				(elementCount * vertexStride) > (VertexCount * VertexDeclaration.VertexStride)	)
			{
				throw new InvalidOperationException(
					"The vertex stride is larger than the vertex buffer."
				);
			}

			int elementSizeInBytes = Marshal.SizeOf(typeof(T));
			if (vertexStride == 0)
			{
				vertexStride = elementSizeInBytes;
			}
			if (vertexStride < elementSizeInBytes)
			{
				throw new ArgumentOutOfRangeException(
					"The vertex stride must be greater than" +
					" or equal to the size of the specified data (" +
					elementSizeInBytes.ToString() + ")."
				);
			}
		}

		#endregion

		#region Internal Context Reset Method

		/// <summary>
		/// The GraphicsDevice is resetting, so GPU resources must be recreated.
		/// </summary>
		internal protected override void GraphicsDeviceResetting()
		{
			// FIXME: Do we even want to bother with DeviceResetting for GL? -flibit
		}

		#endregion
	}
}
