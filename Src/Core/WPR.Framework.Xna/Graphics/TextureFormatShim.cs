using System;
using WPR.Xna.Rhi;

namespace Microsoft.Xna.Framework.Graphics
{
	/// <summary>
	/// Stores a texture in a format the device can actually take, converting the game's pixels on
	/// the way in and back on the way out, when the requested format is one FNA3D maps onto a GL
	/// enum that does not exist in OpenGL ES.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>What is wrong.</b> <c>FNA3D_Driver_OpenGL.c</c>'s translation tables are written for
	/// desktop GL and three of their entries have no OpenGL ES equivalent:
	/// </para>
	/// <list type="bullet">
	/// <item><description>
	/// <c>Bgra5551</c> and <c>Bgra4444</c> upload as <c>GL_BGRA</c> with
	/// <c>GL_UNSIGNED_SHORT_1_5_5_5_REV</c> / <c>GL_UNSIGNED_SHORT_4_4_4_4_REV</c>. Neither the
	/// format nor the packed types exist in any version of OpenGL ES, and no extension adds them —
	/// <c>GL_EXT_texture_format_BGRA8888</c> covers 8888 only.
	/// </description></item>
	/// <item><description>
	/// <c>Bgr565</c> uploads as internal format <c>GL_RGB8</c> with type
	/// <c>GL_UNSIGNED_SHORT_5_6_5</c>. Each is individually legal in ES3 but the <i>pairing</i> is
	/// not: ES 3.0 admits <c>GL_UNSIGNED_SHORT_5_6_5</c> only against <c>GL_RGB565</c>.
	/// </description></item>
	/// <item><description>
	/// <c>ColorBgraEXT</c> uploads as <c>GL_BGRA</c> + <c>GL_UNSIGNED_BYTE</c>, which ES supports
	/// only with <c>GL_EXT_texture_format_BGRA8888</c> — so that one is asked separately and is
	/// usually available.
	/// </description></item>
	/// </list>
	/// <para>
	/// There is no extension check anywhere in the driver, so the failure is silent: both
	/// <c>glTexImage2D</c> at create time and <c>glTexSubImage2D</c> at upload time are rejected,
	/// the texture never gets storage, and the game draws whatever undefined content the sampler
	/// returns. Storing as <c>Color</c> sidesteps all of it.
	/// </para>
	/// <para>
	/// <b>Why here and not in FNA3D.</b> The C fix is a table edit plus a component rotate — the
	/// ES3-legal spellings are <c>GL_RGBA</c> + <c>GL_UNSIGNED_SHORT_4_4_4_4</c> /
	/// <c>GL_UNSIGNED_SHORT_5_5_5_1</c>, whose component order is a 16-bit rotate away from XNA's
	/// (<c>rotl 4</c> and <c>rotl 1</c> respectively) — but the GL upload path has no staging
	/// machinery to rotate into, it would have to be conditional on <c>useES3</c> so as not to
	/// break desktop GL, and <c>FNA3D.dll</c> cannot be rebuilt on this machine while
	/// <c>libFNA3D.so</c> can. That would leave the two platforms running different control flow,
	/// which the swizzle-table binary patch trick cannot reconcile. Managed keeps them identical.
	/// </para>
	/// <para>
	/// <b><see cref="Texture2D.Format"/> keeps reporting what the game asked for.</b> This is the
	/// opposite of what <c>Texture2DReader</c> does when it decompresses DXT to <c>Color</c>, and
	/// deliberately so: that reader owns the whole lifecycle of a content texture — it creates it,
	/// performs the only <c>SetData</c>, and nothing ever reads it back — whereas a texture the
	/// game constructed has the game sizing its own arrays from <c>Format</c>. Reporting
	/// <c>Color</c> there is precisely what caused the bug this replaces: an
	/// <c>//Experimental! RnD / TEMP</c> block in <see cref="Texture2D"/>'s constructor rewrote
	/// <c>Bgra4444</c> to <c>Color</c> without converting anything, so <c>SetData</c>'s
	/// <c>requiredBytes</c> check saw 4 bytes per pixel against the game's 2, bailed out, and
	/// uploaded nothing — on every driver and both platforms.
	/// </para>
	/// <para>
	/// Round-trip is bit-exact, which is the property that matters for a game that reads a texture
	/// back, edits it and writes it again: 4-bit channels expand by <c>n * 17</c> (that is
	/// <c>n &lt;&lt; 4 | n</c>, so <c>&gt;&gt; 4</c> recovers <c>n</c>), 5-bit by
	/// <c>x &lt;&lt; 3 | x &gt;&gt; 2</c>, and the 1-bit alpha to 0 or 255.
	/// </para>
	/// </remarks>
	internal static class TextureFormatShim
	{
		/* Default to converting, for the same reason VertexFormatExpansion defaults to expanding:
		 * until the device has been asked we cannot prove the format works, and being wrong this
		 * way costs memory while being wrong the other way costs a texture full of undefined
		 * content. In practice the device is always asked inside CreateDevice, which runs before
		 * any Texture2D can exist.
		 */
		private static bool convertPackedShorts = true;
		private static bool convertBgra8888 = true;

		/// <summary>
		/// True when at least one format still needs converting on this device, so the common
		/// "nothing to do" case costs one bool rather than a switch.
		/// </summary>
		internal static bool Enabled
		{
			get { return convertPackedShorts || convertBgra8888; }
		}

		/// <summary>
		/// Records what the device said. Called by the graphics backend from the same call that
		/// creates the device — after the driver has come up, so there is a real context to ask,
		/// and before any <see cref="Texture2D"/> can exist.
		/// </summary>
		internal static void SetDeviceSupport(
			bool packedBgraUsable,
			bool bgra8888Usable,
			string detail
		) {
			convertPackedShorts = !packedBgraUsable;
			convertBgra8888 = !bgra8888Usable;

			/* One line per launch, unconditionally including the "nothing to convert" case:
			 * "this conversion did not run" and "it ran and did nothing" are otherwise
			 * indistinguishable from inside a game. Same rule as [wpr-vfmt].
			 */
			XnaBackend.LogInfo(
				"[wpr-texfmt] device texture formats — packed 16-bit (Bgr565/Bgra5551/Bgra4444)=" +
				Describe(packedBgraUsable) +
				" BGRA8888=" + Describe(bgra8888Usable) +
				" (" + detail + "); conversion " +
				(Enabled ? "ENABLED" : "not needed")
			);
		}

		private static string Describe(bool supported)
		{
			return supported ? "ok" : "UNSUPPORTED";
		}

		/// <summary>
		/// Restores the defaults on device teardown, so one game run cannot leave the next launch
		/// in this process trusting a previous device's answer.
		/// </summary>
		internal static void ClearDeviceSupport()
		{
			convertPackedShorts = true;
			convertBgra8888 = true;
		}

		/// <summary>
		/// The format the texture should actually be created in. Returns <paramref name="requested"/>
		/// unchanged whenever the device can take it, which is every driver but GLES.
		/// </summary>
		internal static SurfaceFormat StorageFormatFor(SurfaceFormat requested)
		{
			switch (requested)
			{
				case SurfaceFormat.Bgr565:
				case SurfaceFormat.Bgra5551:
				case SurfaceFormat.Bgra4444:
					return convertPackedShorts ? SurfaceFormat.Color : requested;

				case SurfaceFormat.ColorBgraEXT:
					return convertBgra8888 ? SurfaceFormat.Color : requested;

				default:
					return requested;
			}
		}

		/// <summary>
		/// True when a texture of <paramref name="requested"/> is stored as something else, i.e.
		/// its pixels have to be converted on every upload and readback.
		/// </summary>
		internal static bool NeedsConversion(SurfaceFormat requested)
		{
			return StorageFormatFor(requested) != requested;
		}

		/// <summary>
		/// Converts <paramref name="pixelCount"/> pixels of <paramref name="requested"/> at
		/// <paramref name="source"/> into 32-bit <see cref="SurfaceFormat.Color"/> (R, G, B, A
		/// bytes) at <paramref name="destination"/>.
		/// </summary>
		internal static unsafe void Encode(
			SurfaceFormat requested,
			IntPtr source,
			IntPtr destination,
			int pixelCount
		) {
			byte* dst = (byte*) destination;

			if (requested == SurfaceFormat.ColorBgraEXT)
			{
				/* B, G, R, A in memory -> R, G, B, A. Same width either way, so this is a
				 * byte swap rather than an expansion, and it is safe in place.
				 */
				byte* src = (byte*) source;
				for (int i = 0; i < pixelCount; i += 1)
				{
					byte b = src[0];
					byte g = src[1];
					byte r = src[2];
					byte a = src[3];
					dst[0] = r;
					dst[1] = g;
					dst[2] = b;
					dst[3] = a;
					src += 4;
					dst += 4;
				}
				return;
			}

			ushort* packed = (ushort*) source;
			for (int i = 0; i < pixelCount; i += 1)
			{
				ushort v = packed[i];
				byte r, g, b, a;

				if (requested == SurfaceFormat.Bgra4444)
				{
					/* A<<12 | R<<8 | G<<4 | B — see PackedVector/Bgra4444.cs. */
					a = Expand4((v >> 12) & 0xF);
					r = Expand4((v >> 8) & 0xF);
					g = Expand4((v >> 4) & 0xF);
					b = Expand4(v & 0xF);
				}
				else if (requested == SurfaceFormat.Bgra5551)
				{
					/* A<<15 | R<<10 | G<<5 | B — see PackedVector/Bgra5551.cs. */
					a = (byte) (((v >> 15) & 0x1) * 255);
					r = Expand5((v >> 10) & 0x1F);
					g = Expand5((v >> 5) & 0x1F);
					b = Expand5(v & 0x1F);
				}
				else
				{
					/* Bgr565: R<<11 | G<<5 | B, opaque. */
					a = 255;
					r = Expand5((v >> 11) & 0x1F);
					g = Expand6((v >> 5) & 0x3F);
					b = Expand5(v & 0x1F);
				}

				dst[0] = r;
				dst[1] = g;
				dst[2] = b;
				dst[3] = a;
				dst += 4;
			}
		}

		/// <summary>
		/// The inverse of <see cref="Encode"/>: converts <paramref name="pixelCount"/> pixels of
		/// 32-bit <see cref="SurfaceFormat.Color"/> back into <paramref name="requested"/>, so a
		/// game reads back exactly the bits it wrote.
		/// </summary>
		internal static unsafe void Decode(
			SurfaceFormat requested,
			IntPtr source,
			IntPtr destination,
			int pixelCount
		) {
			byte* src = (byte*) source;

			if (requested == SurfaceFormat.ColorBgraEXT)
			{
				byte* dst = (byte*) destination;
				for (int i = 0; i < pixelCount; i += 1)
				{
					byte r = src[0];
					byte g = src[1];
					byte b = src[2];
					byte a = src[3];
					dst[0] = b;
					dst[1] = g;
					dst[2] = r;
					dst[3] = a;
					src += 4;
					dst += 4;
				}
				return;
			}

			ushort* packed = (ushort*) destination;
			for (int i = 0; i < pixelCount; i += 1)
			{
				byte r = src[0];
				byte g = src[1];
				byte b = src[2];
				byte a = src[3];
				src += 4;

				if (requested == SurfaceFormat.Bgra4444)
				{
					packed[i] = (ushort) (
						((a >> 4) << 12) |
						((r >> 4) << 8) |
						((g >> 4) << 4) |
						(b >> 4)
					);
				}
				else if (requested == SurfaceFormat.Bgra5551)
				{
					packed[i] = (ushort) (
						((a >> 7) << 15) |
						((r >> 3) << 10) |
						((g >> 3) << 5) |
						(b >> 3)
					);
				}
				else
				{
					packed[i] = (ushort) (
						((r >> 3) << 11) |
						((g >> 2) << 5) |
						(b >> 3)
					);
				}
			}
		}

		/* n * 17 == n << 4 | n, so (v >> 4) recovers n exactly. */
		private static byte Expand4(int n)
		{
			return (byte) ((n << 4) | n);
		}

		/* x << 3 | x >> 2 fills the low bits from the high ones, so (v >> 3) recovers x exactly. */
		private static byte Expand5(int x)
		{
			return (byte) ((x << 3) | (x >> 2));
		}

		private static byte Expand6(int x)
		{
			return (byte) ((x << 2) | (x >> 4));
		}
	}
}
