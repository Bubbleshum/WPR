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
using System.IO;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	public class TextureCube : Texture
	{
		#region Public Properties

		/// <summary>
		/// Gets the width and height of the cube map face in pixels.
		/// </summary>
		/// <value>The width and height of a cube map face in pixels.</value>
		public int Size
		{
			get;
			private set;
		}

		#endregion

		#region Public Constructor

		public TextureCube(
			GraphicsDevice graphicsDevice,
			int size,
			bool mipMap,
			SurfaceFormat format
		) {
			if (graphicsDevice == null)
			{
				throw new ArgumentNullException("graphicsDevice");
			}

			GraphicsDevice = graphicsDevice;
			Size = size;
			LevelCount = mipMap ? CalculateMipLevels(size) : 1;

			// TODO: Use QueryRenderTargetFormat!
			if (this is IRenderTarget)
			{
				if (format == SurfaceFormat.ColorSrgbEXT)
				{
					if (XnaBackend.Graphics.SupportsSRGBRenderTargets(GraphicsDevice.GLDevice) == 0)
					{
						// Renderable but not on this device
						Format = SurfaceFormat.Color;
					}
					else
					{
						Format = format;
					}
				}
				else if (	format != SurfaceFormat.Color &&
						format != SurfaceFormat.Rgba1010102 &&
						format != SurfaceFormat.Rg32 &&
						format != SurfaceFormat.Rgba64 &&
						format != SurfaceFormat.Single &&
						format != SurfaceFormat.Vector2 &&
						format != SurfaceFormat.Vector4 &&
						format != SurfaceFormat.HalfSingle &&
						format != SurfaceFormat.HalfVector2 &&
						format != SurfaceFormat.HalfVector4 &&
						format != SurfaceFormat.HdrBlendable	)
				{
					// Not a renderable format period
					Format = SurfaceFormat.Color;
				}
				else
				{
					Format = format;
				}
			}
			else
			{
				Format = format;
			}

			/* What the device is actually given; Format stays the game's view. See
			 * TextureFormatShim, and Texture2D's constructor for the same pair. */
			storageFormat = TextureFormatShim.StorageFormatFor(Format);

			texture = XnaBackend.Graphics.CreateTextureCube(
				GraphicsDevice.GLDevice,
				storageFormat,
				Size,
				LevelCount,
				(byte) ((this is IRenderTarget) ? 1 : 0)
			);
		}

		#endregion

		#region Public SetData Methods

		public void SetData<T>(
			CubeMapFace cubeMapFace,
			T[] data
		) where T : struct {
			SetData(
				cubeMapFace,
				0,
				null,
				data,
				0,
				data.Length
			);
		}

		public void SetData<T>(
			CubeMapFace cubeMapFace,
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			SetData(
				cubeMapFace,
				0,
				null,
				data,
				startIndex,
				elementCount
			);
		}

		public void SetData<T>(
			CubeMapFace cubeMapFace,
			int level,
			Rectangle? rect,
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			if (data == null)
			{
				throw new ArgumentNullException("data");
			}

			int xOffset, yOffset, width, height;
			if (rect.HasValue)
			{
				xOffset = rect.Value.X;
				yOffset = rect.Value.Y;
				width = rect.Value.Width;
				height = rect.Value.Height;
			}
			else
			{
				xOffset = 0;
				yOffset = 0;
				width = Math.Max(1, Size >> level);
				height = Math.Max(1, Size >> level);
			}

			int elementSizeInBytes = Marshal.SizeOf(typeof(T));
			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);

			IntPtr source = handle.AddrOfPinnedObject() + startIndex * elementSizeInBytes;
			int sourceLength = elementCount * elementSizeInBytes;

			/* Same conversion as Texture2D.SetData, for the same reason. */
			GCHandle staging = default(GCHandle);
			if (storageFormat != Format)
			{
				byte[] converted = new byte[width * height * GetFormatSize(storageFormat)];
				staging = GCHandle.Alloc(converted, GCHandleType.Pinned);
				TextureFormatShim.Encode(
					Format,
					source,
					staging.AddrOfPinnedObject(),
					width * height
				);
				source = staging.AddrOfPinnedObject();
				sourceLength = converted.Length;
			}

			XnaBackend.Graphics.SetTextureDataCube(
				GraphicsDevice.GLDevice,
				texture,
				xOffset,
				yOffset,
				width,
				height,
				cubeMapFace,
				level,
				source,
				sourceLength
			);
			if (staging.IsAllocated)
			{
				staging.Free();
			}
			handle.Free();
		}

		public void SetDataPointerEXT(
			CubeMapFace cubeMapFace,
			int level,
			Rectangle? rect,
			IntPtr data,
			int dataLength
		) {
			if (data == IntPtr.Zero)
			{
				throw new ArgumentNullException("data");
			}

			int xOffset, yOffset, width, height;
			if (rect.HasValue)
			{
				xOffset = rect.Value.X;
				yOffset = rect.Value.Y;
				width = rect.Value.Width;
				height = rect.Value.Height;
			}
			else
			{
				xOffset = 0;
				yOffset = 0;
				width = Math.Max(1, Size >> level);
				height = Math.Max(1, Size >> level);
			}

			/* Same conversion as the SetData<T> above; this is the path DDSFromStreamEXT takes. */
			if (storageFormat != Format)
			{
				byte[] converted = new byte[width * height * GetFormatSize(storageFormat)];
				GCHandle staging = GCHandle.Alloc(converted, GCHandleType.Pinned);
				try
				{
					TextureFormatShim.Encode(
						Format,
						data,
						staging.AddrOfPinnedObject(),
						width * height
					);
					XnaBackend.Graphics.SetTextureDataCube(
						GraphicsDevice.GLDevice,
						texture,
						xOffset,
						yOffset,
						width,
						height,
						cubeMapFace,
						level,
						staging.AddrOfPinnedObject(),
						converted.Length
					);
				}
				finally
				{
					staging.Free();
				}
				return;
			}

			XnaBackend.Graphics.SetTextureDataCube(
				GraphicsDevice.GLDevice,
				texture,
				xOffset,
				yOffset,
				width,
				height,
				cubeMapFace,
				level,
				data,
				dataLength
			);
		}
		#endregion

		#region Public GetData Method

		public void GetData<T>(
			CubeMapFace cubeMapFace,
			T[] data
		) where T : struct {
			GetData(
				cubeMapFace,
				0,
				null,
				data,
				0,
				data.Length
			);
		}

		public void GetData<T>(
			CubeMapFace cubeMapFace,
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			GetData(
				cubeMapFace,
				0,
				null,
				data,
				startIndex,
				elementCount
			);
		}

		public void GetData<T>(
			CubeMapFace cubeMapFace,
			int level,
			Rectangle? rect,
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			if (data == null || data.Length == 0)
			{
				throw new ArgumentException("data cannot be null");
			}
			if (data.Length < startIndex + elementCount)
			{
				throw new ArgumentException(
					"The data passed has a length of " + data.Length.ToString() +
					" but " + elementCount.ToString() + " pixels have been requested."
				);
			}

			int subX, subY, subW, subH;
			if (rect == null)
			{
				subX = 0;
				subY = 0;
				subW = Size >> level;
				subH = Size >> level;
			}
			else
			{
				subX = rect.Value.X;
				subY = rect.Value.Y;
				subW = rect.Value.Width;
				subH = rect.Value.Height;
			}

			int elementSizeInBytes = Marshal.SizeOf(typeof(T));
			ValidateGetDataFormat(Format, elementSizeInBytes);

			/* On OpenGL ES this entry point is a NULL call unconditionally — unlike the 2D one it
			 * has no render-target diversion to fall into. See TextureReadback. */
			if (!TextureReadback.CanServeCube())
			{
				Array.Clear(data, startIndex, elementCount);
				TextureReadback.ReportRefusal(
					"TextureCube " + Size + " " + Format + " face " + cubeMapFace +
					" level " + level
				);
				return;
			}

			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				if (storageFormat != Format)
				{
					byte[] stored = new byte[subW * subH * GetFormatSize(storageFormat)];
					GCHandle staging = GCHandle.Alloc(stored, GCHandleType.Pinned);
					try
					{
						XnaBackend.Graphics.GetTextureDataCube(
							GraphicsDevice.GLDevice,
							texture,
							subX,
							subY,
							subW,
							subH,
							cubeMapFace,
							level,
							staging.AddrOfPinnedObject(),
							stored.Length
						);
						int pixels = Math.Min(
							subW * subH,
							(elementCount * elementSizeInBytes) / GetFormatSize(Format)
						);
						TextureFormatShim.Decode(
							Format,
							staging.AddrOfPinnedObject(),
							handle.AddrOfPinnedObject() + (startIndex * elementSizeInBytes),
							pixels
						);
					}
					finally
					{
						staging.Free();
					}
				}
				else
				{
					XnaBackend.Graphics.GetTextureDataCube(
						GraphicsDevice.GLDevice,
						texture,
						subX,
						subY,
						subW,
						subH,
						cubeMapFace,
						level,
						handle.AddrOfPinnedObject() + (startIndex * elementSizeInBytes),
						elementCount * elementSizeInBytes
					);
				}
			}
			finally
			{
				handle.Free();
			}
		}

		#endregion

		#region Public Static TextureCube Extensions

		public static TextureCube DDSFromStreamEXT(
			GraphicsDevice graphicsDevice,
			Stream stream
		) {
			TextureCube result;

			// Begin BinaryReader, ignoring a tab!
			using (BinaryReader reader = new BinaryReader(stream))
			{

			int width, height, levels;
			bool isCube;
			SurfaceFormat format;
			Texture.ParseDDS(
				reader,
				out format,
				out width,
				out height,
				out levels,
				out isCube
			);

			if (!isCube)
			{
				throw new FormatException("This file does not contain cube data!");
			}

			// Allocate/Load texture
			result = new TextureCube(
				graphicsDevice,
				width,
				levels > 1,
				format
			);

			byte[] tex = null;
			if (	ImageStreamHelper.TryGetBuffer(stream, out tex)	)
			{
				for (int face = 0; face < 6; face += 1)
				{
					for (int i = 0; i < levels; i += 1)
					{
						int mipLevelSize = Texture.CalculateDDSLevelSize(
							width >> i,
							width >> i,
							format
						);
						result.SetData(
							(CubeMapFace) face,
							i,
							null,
							tex,
							(int) stream.Seek(0, SeekOrigin.Current),
							mipLevelSize
						);
						stream.Seek(
							mipLevelSize,
							SeekOrigin.Current
						);
					}
				}
			}
			else
			{
				for (int face = 0; face < 6; face += 1)
				{
					for (int i = 0; i < levels; i += 1)
					{
						tex = reader.ReadBytes(Texture.CalculateDDSLevelSize(
							width >> i,
							width >> i,
							format
						));
						result.SetData(
							(CubeMapFace) face,
							i,
							null,
							tex,
							0,
							tex.Length
						);
					}
				}
			}

			// End BinaryReader
			}

			// Finally.
			return result;
		}

		#endregion
	}
}
