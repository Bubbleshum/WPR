using System;
using System.IO;

namespace Microsoft.Xna.Framework.Graphics
{
	// WPR (Stage 5c): extracted from Texture2D/TextureCube's inline MemoryStream fast-path.
	// BCL MemoryStream.TryGetBuffer yields an ArraySegment&lt;byte&gt;; the DDS loaders want the
	// raw byte[]. (FNA's source assumed a byte[] out-param that doesn't match modern BCL.)
	internal static class ImageStreamHelper
	{
		internal static bool TryGetBuffer(Stream stream, out byte[] buffer)
		{
			if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg))
			{
				buffer = seg.Array;
				return true;
			}
			buffer = null;
			return false;
		}
	}
}
