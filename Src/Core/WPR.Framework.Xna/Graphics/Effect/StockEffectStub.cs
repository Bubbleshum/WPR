#region Using Statements
using System;
using System.Text;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	/// <summary>
	/// Recognises a Windows Phone 7 <em>stock-effect reference</em> — a short blob that names one
	/// of XNA's built-in effects instead of carrying any shader bytecode — and hands back the
	/// real effect bytes WPR ships for it.
	///
	/// <para>XNA 4.0 on Windows Phone supported no custom shaders at all, so
	/// <c>Effect(GraphicsDevice, byte[])</c> could only ever name a built-in. The blob is the
	/// ordinary XNA4 container (<c>0xBCF00BCF</c>, payload offset, reserved) wrapping a D3DX
	/// effect magic (<c>0xFEFF0901</c>) followed by a four-character tag naming the effect,
	/// where a real effect would have the offset of its body. MojoShader reads those four bytes
	/// as that offset, finds it past the end of the buffer and reports
	/// <c>Unexpected EOF</c>, which is what a WP7 title passing one of these looks like.</para>
	///
	/// <para>The Treasures of Montezuma is the attested case: its YF.Framework renderer passes a
	/// 20-byte blob tagged <c>spri</c> from its device-created handler, so the game died during
	/// <c>GraphicsDeviceManager.CreateDevice</c> before drawing a frame. <b>Only that tag is
	/// attested;</b> the other five are inferred from the same four-letter rule and will simply
	/// not match if the rule is wrong, leaving the original error in place.</para>
	/// </summary>
	internal static class StockEffectStub
	{
		#region Private Constants

		// The XNA4 content pipeline's wrapper, which MojoShader also knows how to skip.
		private const uint XnaContainerMagic = 0xBCF00BCF;

		// The D3DX9 effect magic that follows it, and the start of a real effect.
		private const uint D3DXEffectMagic = 0xFEFF0901;

		// Header (12) + magic (4) + tag (4). A real effect always has more than this, because
		// the four bytes we read as a tag would be the offset of a body that has to exist.
		private const int StubLength = 20;

		#endregion

		#region Internal Static Methods

		/// <summary>
		/// Returns the stock effect <paramref name="effectCode"/> names, or <c>null</c> when it is
		/// not a stock-effect reference and should be compiled as-is.
		/// </summary>
		internal static byte[] TryResolve(byte[] effectCode)
		{
			if (effectCode == null || effectCode.Length < StubLength)
			{
				return null;
			}

			if (ReadUInt32(effectCode, 0) != XnaContainerMagic)
			{
				return null;
			}

			// The container's second word is the offset of the payload from the start of the
			// blob; anything else is a shape we do not recognise.
			uint payload = ReadUInt32(effectCode, 4);
			if (payload + 8 > effectCode.Length || ReadUInt32(effectCode, (int) payload) != D3DXEffectMagic)
			{
				return null;
			}

			/* Where a real effect has the offset of its body, a reference has a four-character
			 * ASCII tag. Test the shape rather than the length: a tag is letters, and a genuine
			 * offset that happened to be four letters would still have to point inside the blob. */
			int tagAt = (int) payload + 4;
			uint offsetOrTag = ReadUInt32(effectCode, tagAt);
			if (offsetOrTag + tagAt + 4 <= effectCode.Length)
			{
				// It addresses a body that is really there — a normal effect.
				return null;
			}

			for (int i = tagAt; i < tagAt + 4; i += 1)
			{
				byte b = effectCode[i];
				bool letter = (b >= (byte) 'a' && b <= (byte) 'z') || (b >= (byte) 'A' && b <= (byte) 'Z');
				if (!letter)
				{
					return null;
				}
			}

			string tag = Encoding.ASCII.GetString(effectCode, tagAt, 4).ToLowerInvariant();
			switch (tag)
			{
				case "spri": return Resources.SpriteEffect;
				case "basi": return Resources.BasicEffect;
				case "alph": return Resources.AlphaTestEffect;
				case "dual": return Resources.DualTextureEffect;
				case "envi": return Resources.EnvironmentMapEffect;
				case "skin": return Resources.SkinnedEffect;
			}

			return null;
		}

		#endregion

		#region Private Static Methods

		private static uint ReadUInt32(byte[] data, int offset)
		{
			return (uint) (data[offset]
				| (data[offset + 1] << 8)
				| (data[offset + 2] << 16)
				| (data[offset + 3] << 24));
		}

		#endregion
	}
}
