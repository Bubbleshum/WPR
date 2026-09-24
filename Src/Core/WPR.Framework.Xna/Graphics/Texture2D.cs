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
using System.IO;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	public class Texture2D : Texture
	{
		#region Public Properties

		public int Width
		{
			get;
			private set;
		}

		public int Height
		{
			get;
			private set;
		}

		public Rectangle Bounds
		{
			get
			{
				return new Rectangle(0, 0, Width, Height);
			}
		}

		#endregion

		#region Public Constructors

		public Texture2D(
			GraphicsDevice graphicsDevice,
			int width,
			int height
		) : this(
			graphicsDevice,
			width,
			height,
			false,
			SurfaceFormat.Color
		) {
		}

		public Texture2D(
			GraphicsDevice graphicsDevice,
			int width,
			int height,
			bool mipMap,
			SurfaceFormat format
		) {
			if (graphicsDevice == null)
			{
				throw new ArgumentNullException("graphicsDevice");
			}

			GraphicsDevice = graphicsDevice;

			//RnD
			//if (format != SurfaceFormat.Color)
			//{
				//if (width > 640) //800
				//	width = 640;
				//if (height > 480) 
				//	width = 480;
			//}

            Width = width;
			Height = height;
			LevelCount = mipMap ? CalculateMipLevels(width, height) : 1;

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

			/* What the device is actually given. Format above stays whatever the game asked for,
			 * because the game sizes its own arrays from it; this is the one the driver sees.
			 *
			 * This replaced an "Experimental! RnD / TEMP" block that rewrote Bgra4444 to Color
			 * without converting a single pixel. Because it changed Format itself, SetData's
			 * requiredBytes check then measured the game's 2-bytes-per-pixel data against Color's
			 * 4 and bailed out — so every Bgra4444 texture was silently never uploaded, on both
			 * platforms and all three drivers. See TextureFormatShim. */
			storageFormat = TextureFormatShim.StorageFormatFor(Format);

			try
			{
				texture = XnaBackend.Graphics.CreateTexture2D(
					GraphicsDevice.GLDevice,
					storageFormat,
					Width,
					Height,
					LevelCount,
					(byte)((this is IRenderTarget) ? 1 : 0)
				);
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[ex] XnaBackend.Graphics.CreateTexture2D ex:" + ex.Message);
			}
		}

		#endregion

		#region Public SetData Methods

		public void SetData<T>(T[] data) where T : struct
		{
			SetData(
				0,
				null,
				data,
				0,
				data.Length
			);
		}

		public void SetData<T>(
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			SetData(
				0,
				null,
				data,
				startIndex,
				elementCount
			);
		}

		public void SetData<T>
		(
			int level,
			Rectangle? rect,
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct 
		{
			if (data == null)
			{
				throw new ArgumentNullException("data");
			}
			if (startIndex < 0)
			{
				throw new ArgumentOutOfRangeException("startIndex");
			}
			if (data.Length < (elementCount + startIndex))
			{
				throw new ArgumentOutOfRangeException("elementCount");
			}

			int x, y, w, h;
			if (rect.HasValue)
			{
				x = rect.Value.X;
				y = rect.Value.Y;
				w = rect.Value.Width;
				h = rect.Value.Height;
			}
			else
			{
				x = 0;
				y = 0;
				w = Math.Max(Width >> level, 1);
				h = Math.Max(Height >> level, 1);
			}
			int elementSize = Marshal.SizeOf(typeof(T));
			
			int requiredBytes = (w * h * GetFormatSize(Format)) / GetBlockSizeSquared(Format);
			
			int availableBytes = elementCount * elementSize;
			
			if (requiredBytes > availableBytes)
			{
                Debug.WriteLine("[warn] The region you are trying to upload is larger " +
                    "than the amount of data you provided.");

                //throw new ArgumentOutOfRangeException("rect", 
				//	"The region you are trying to upload is larger " +
				//	"than the amount of data you provided.");
				
				return;				
			}

			

            try
			{
				// 1. try to alloc mem...
                GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);

                IntPtr source = handle.AddrOfPinnedObject() + startIndex * elementSize;
                int sourceLength = elementCount * elementSize;

                /* This device may be storing the texture in a format wider than the one the game
                 * asked for, in which case its pixels have to be converted on the way in. The
                 * staging buffer is sized from the rect rather than the whole surface, so a
                 * partial SetData(level, rect, ...) needs no special handling — the destination
                 * rect is untouched. See TextureFormatShim. */
                GCHandle staging = default(GCHandle);
                if (storageFormat != Format)
                {
                    byte[] converted = new byte[w * h * GetFormatSize(storageFormat)];
                    staging = GCHandle.Alloc(converted, GCHandleType.Pinned);
                    TextureFormatShim.Encode(
                        Format,
                        source,
                        staging.AddrOfPinnedObject(),
                        w * h
                    );
                    source = staging.AddrOfPinnedObject();
                    sourceLength = converted.Length;
                }

				// 2. try to set texture 2D-data...
                try
                {
                    XnaBackend.Graphics.SetTextureData2D(
                    GraphicsDevice.GLDevice,
                    texture,
                    x,
                    y,
                    w,
                    h,
                    level,
                    source,
                    sourceLength
                );
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine("Texture2D ex: " + ex2.Message);
                }

				// 3. free mem...
                if (staging.IsAllocated)
                {
                    staging.Free();
                }
                handle.Free();


            }
			catch (Exception ex)
			{
                Debug.WriteLine("[warn] Cannot allocate the amount of data you provided: " 
					+ ex.Message);
                return;
			}		
			
		}


		public void SetDataPointerEXT(
			int level,
			Rectangle? rect,
			IntPtr data,
			int dataLength
		) {
			if (data == IntPtr.Zero)
			{
				throw new ArgumentNullException("data");
			}

			int x, y, w, h;
			if (rect.HasValue)
			{
				x = rect.Value.X;
				y = rect.Value.Y;
				w = rect.Value.Width;
				h = rect.Value.Height;
			}
			else
			{
				x = 0;
				y = 0;
				w = Math.Max(Width >> level, 1);
				h = Math.Max(Height >> level, 1);
			}

			/* Same conversion as SetData<T>: this is the path Texture2D.FromStream takes, so
			 * leaving it out would make every stream-loaded texture of a substituted format an
			 * undefined surface. See TextureFormatShim. */
			if (storageFormat != Format)
			{
				byte[] converted = new byte[w * h * GetFormatSize(storageFormat)];
				GCHandle staging = GCHandle.Alloc(converted, GCHandleType.Pinned);
				try
				{
					TextureFormatShim.Encode(
						Format,
						data,
						staging.AddrOfPinnedObject(),
						w * h
					);
					XnaBackend.Graphics.SetTextureData2D(
						GraphicsDevice.GLDevice,
						texture,
						x,
						y,
						w,
						h,
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

			XnaBackend.Graphics.SetTextureData2D(
				GraphicsDevice.GLDevice,
				texture,
				x,
				y,
				w,
				h,
				level,
				data,
				dataLength
			);
		}

		#endregion

		#region Public GetData Methods

		public void GetData<T>(T[] data) where T : struct
		{
			GetData(
				0,
				null,
				data,
				0,
				data.Length
			);
		}

		public void GetData<T>(
			T[] data,
			int startIndex,
			int elementCount
		) where T : struct {
			GetData(
				0,
				null,
				data,
				startIndex,
				elementCount
			);
		}

		public void GetData<T>(
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
				subW = Width >> level;
				subH = Height >> level;
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

			/* Some drivers cannot answer this read at all, and the way they fail is not a
			 * throw — it is a NULL call or an overrun of the array pinned below. Refuse first.
			 * See TextureReadback. */
			if (!TextureReadback.CanServe2D(storageFormat, level))
			{
				Array.Clear(data, startIndex, elementCount);
				TextureReadback.ReportRefusal(
					"Texture2D " + Width + "x" + Height + " " + Format + " level " + level
				);
				return;
			}

			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				if (storageFormat != Format)
				{
					/* Stored wider than the game asked for, so read the stored pixels into a
					 * staging buffer and convert back. Round-trip is bit-exact, so a game that
					 * reads, edits and writes again sees exactly what it wrote. */
					byte[] stored = new byte[subW * subH * GetFormatSize(storageFormat)];
					GCHandle staging = GCHandle.Alloc(stored, GCHandleType.Pinned);
					try
					{
						XnaBackend.Graphics.GetTextureData2D(
							GraphicsDevice.GLDevice,
							texture,
							subX,
							subY,
							subW,
							subH,
							level,
							staging.AddrOfPinnedObject(),
							stored.Length
						);

						/* Never decode more pixels than the caller has room for — elementCount
						 * is the game's promise about its own array, and the rect is ours. */
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
					XnaBackend.Graphics.GetTextureData2D(
						GraphicsDevice.GLDevice,
						texture,
						subX,
						subY,
						subW,
						subH,
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

		#region Public Texture2D Save Methods

		/// <summary>
		/// Reads level 0 of the whole surface into <paramref name="data"/>, in whatever format the
		/// device stores it. False when this driver cannot serve the read, in which case nothing
		/// has been written and the caller must not encode the buffer — the image writers take a
		/// raw pointer, so handing them an unwritten allocation would encode uninitialised heap.
		/// </summary>
		private bool ReadWholeSurface(IntPtr data, int length)
		{
			if (!TextureReadback.CanServe2D(storageFormat, 0))
			{
				TextureReadback.ReportRefusal(
					"Texture2D " + Width + "x" + Height + " " + Format + " (SaveAs*)"
				);
				return false;
			}

			XnaBackend.Graphics.GetTextureData2D(
				GraphicsDevice.GLDevice,
				texture,
				0,
				0,
				Width,
				Height,
				0,
				data,
				length
			);
			return true;
		}

		public void SaveAsJpeg(Stream stream, int width, int height)
		{
			/* The source read is the whole texture; width/height are the size to ENCODE at and
			 * belong to WriteJPGStream alone. Passing the caller's height to the readback (which
			 * this used to do) sized the buffer from Height and read `height` rows into it, so a
			 * scaled thumbnail encoded uninitialised heap below the last row read. */
			int len = Width * Height * GetFormatSize(storageFormat);
			IntPtr data = Marshal.AllocHGlobal(len);
			if (!ReadWholeSurface(data, len))
			{
				Marshal.FreeHGlobal(data);
				return;
			}

			XnaBackend.Graphics.WriteJPGStream(
				stream,
				Width,
				Height,
				width,
				height,
				data,
				100 // FIXME: What does XNA pick for quality? -flibit
			);

			Marshal.FreeHGlobal(data);
		}

		public void SaveAsPng(Stream stream, int width, int height)
		{
			/* See SaveAsJpeg: the readback is always the whole surface. */
			int len = Width * Height * GetFormatSize(storageFormat);
			IntPtr data = Marshal.AllocHGlobal(len);
			if (!ReadWholeSurface(data, len))
			{
				Marshal.FreeHGlobal(data);
				return;
			}

			XnaBackend.Graphics.WritePNGStream(
				stream,
				Width,
				Height,
				width,
				height,
				data
			);

			Marshal.FreeHGlobal(data);
		}

		#endregion

		#region Public Static Texture2D Load Methods

		public static Texture2D FromStream(GraphicsDevice graphicsDevice, Stream stream)
		{
			if (stream.CanSeek && stream.Position == stream.Length)
			{
				stream.Seek(0, SeekOrigin.Begin);
			}

			int width, height, len;
			IntPtr pixels = XnaBackend.Graphics.ReadImageStream(
				stream,
				out width,
				out height,
				out len
			);

			Texture2D result = new Texture2D(
				graphicsDevice,
				width,
				height
			);
			result.SetDataPointerEXT(
				0,
				null,
				pixels,
				len
			);

			XnaBackend.Graphics.FreeImage(pixels);
			return result;
		}

		public static Texture2D FromStream(
			GraphicsDevice graphicsDevice,
			Stream stream,
			int width,
			int height,
			bool zoom
		) {
			if (stream.CanSeek && stream.Position == stream.Length)
			{
				stream.Seek(0, SeekOrigin.Begin);
			}

			int realWidth, realHeight, len;
			IntPtr pixels = XnaBackend.Graphics.ReadImageStream(
				stream,
				out realWidth,
				out realHeight,
				out len,
				width,
				height,
				zoom
			);

			Texture2D result = new Texture2D(
				graphicsDevice,
				realWidth,
				realHeight
			);
			result.SetDataPointerEXT(
				0,
				null,
				pixels,
				len
			);

			XnaBackend.Graphics.FreeImage(pixels);
			return result;
		}

		#endregion

		#region Public Static Texture2D Extensions

		/// <summary>
		/// Loads image data from a given stream.
		/// </summary>
		/// <remarks>
		/// This is an extension of XNA 4 and is not compatible with XNA. It exists to help with dynamically reloading
		/// textures while games are running. Games can use this method to read a stream into memory and then call
		/// SetData on a texture with that data, rather than having to dispose the texture and recreate it entirely.
		/// </remarks>
		/// <param name="stream">The stream from which to read the image data.</param>
		/// <param name="width">Outputs the width of the image.</param>
		/// <param name="height">Outputs the height of the image.</param>
		/// <param name="pixels">Outputs the pixel data of the image, in non-premultiplied RGBA format.</param>
		/// <param name="requestedWidth">Preferred width of the resulting image data</param>
		/// <param name="requestedHeight">Preferred height of the resulting image data</param>
		/// <param name="zoom">false to maintain aspect ratio, true to crop image</param>
		public static void TextureDataFromStreamEXT(
			Stream stream,
			out int width,
			out int height,
			out byte[] pixels,
			int requestedWidth = -1,
			int requestedHeight = -1,
			bool zoom = false
		) {
			if (stream.CanSeek && stream.Position == stream.Length)
			{
				stream.Seek(0, SeekOrigin.Begin);
			}

			int len;
			IntPtr pixPtr = XnaBackend.Graphics.ReadImageStream(
				stream,
				out width,
				out height,
				out len,
				requestedWidth,
				requestedHeight,
				zoom
			);

			pixels = new byte[len];
			Marshal.Copy(pixPtr, pixels, 0, len);

			XnaBackend.Graphics.FreeImage(pixPtr);
		}

		public static Texture2D DDSFromStreamEXT(
			GraphicsDevice graphicsDevice,
			Stream stream
		) {
			Texture2D result;

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

			if (isCube)
			{
				throw new FormatException("This file contains cube map data!");
			}

			// Allocate/Load texture
			result = new Texture2D(
				graphicsDevice,
				width,
				height,
				levels > 1,
				format
			);

			byte[] tex = null;
			if (	ImageStreamHelper.TryGetBuffer(stream, out tex)	)
			{
				for (int i = 0; i < levels; i += 1)
				{
					int levelSize = Texture.CalculateDDSLevelSize(
						width >> i,
						height >> i,
						format
					);
					result.SetData(
						i,
						null,
						tex,
						(int) stream.Seek(0, SeekOrigin.Current),
						levelSize
					);
					stream.Seek(
						levelSize,
						SeekOrigin.Current
					);
				}
			}
			else
			{
				for (int i = 0; i < levels; i += 1)
				{
					tex = reader.ReadBytes(Texture.CalculateDDSLevelSize(
						width >> i,
						height >> i,
						format
					));
					result.SetData(
						i,
						null,
						tex,
						0,
						tex.Length
					);
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
