using System;
using WPR.Xna.Rhi;

namespace Microsoft.Xna.Framework.Graphics
{
	/// <summary>
	/// Decides whether this device can serve a texture readback at all, so that a
	/// <c>GetData</c> the driver cannot answer fails as a logged no-op instead of taking the
	/// process down or corrupting the caller's array.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>What FNA3D's OpenGL driver actually does on OpenGL ES</b>, which is not what the
	/// <c>SDL_assert(renderer-&gt;supports_NonES3)</c> at the top of
	/// <c>OPENGL_GetTextureData2D</c> suggests. That assert is compiled out of the release
	/// <c>libFNA3D.so</c> we ship, and just below it the level-0 case is diverted into
	/// <c>OPENGL_INTERNAL_ReadTargetIfApplicable</c>, which opens with
	/// <c>if (texUnbound &amp;&amp; !renderer-&gt;useES3) return 0;</c> — so on GLES it never
	/// declines. It attaches any texture to an FBO and reads it with <c>glReadPixels</c>. The
	/// result is four different behaviours, only one of which is the crash the assert implies:
	/// </para>
	/// <list type="bullet">
	/// <item><description>
	/// <b>level 0, a 32-bit colour format</b> — works. The FBO path never reaches
	/// <c>glGetTexImage</c>, so nothing here needs to intervene.
	/// </description></item>
	/// <item><description>
	/// <b>level 0, a narrower format</b> — <c>glReadPixels</c> is hardcoded to
	/// <c>GL_RGBA, GL_UNSIGNED_BYTE</c> ("FIXME: Assumption!") and ignores <c>dataLength</c>
	/// entirely, so it writes <c>w * h * 4</c> bytes into whatever the caller pinned. For a
	/// 16-bit texture that is a two-fold overrun of a managed array. <b>Heap corruption, silent
	/// and delayed</b> — worse than the crash, and the reason this guard tests the format and
	/// not just the level.
	/// </description></item>
	/// <item><description>
	/// <b>level &gt; 0</b> — falls past the diversion to <c>glGetTexImage</c>, which is declared
	/// <c>GL_PROC(NonES3, …)</c> and therefore never resolved under ES. The pointer is NULL:
	/// SIGSEGV at pc 0, no log, process gone.
	/// </description></item>
	/// <item><description>
	/// <b><see cref="TextureCube"/>, any level</b> — <c>OPENGL_GetTextureDataCube</c> has no
	/// diversion at all and goes straight to <c>glGetTexImage</c>, so it is the NULL call
	/// unconditionally.
	/// </description></item>
	/// </list>
	/// <para>
	/// <b>Why a guard and not a CPU mirror of every texture.</b> The obvious fix is the texture
	/// twin of <see cref="GpuBufferShadow"/> — keep what was uploaded and serve reads from it.
	/// The economics are inverted, though: a buffer mirror is cheap and its crash was broad (the
	/// very first <c>GetData</c> on any buffer), whereas a texture mirror would roughly double a
	/// title's texture memory — the bulk of a WP7 game's footprint — on precisely the devices
	/// that fell back to OpenGL because their Vulkan driver was already misbehaving, to serve a
	/// call most titles never make. So this closes the crash and the overrun for nothing, and the
	/// mirror waits for a title that demonstrably needs its pixels back. That is a decision, not
	/// an oversight; when one turns up, mirror only non-render-target, uncompressed textures and
	/// store the bytes the game wrote, before any <see cref="TextureFormatShim"/> conversion.
	/// </para>
	/// <para>
	/// <b>Zeros rather than falling through, which is the opposite of what
	/// <see cref="GpuBufferShadow"/> chose.</b> That class falls back to the driver when it has
	/// nothing, on the sound argument that the read would have been undefined anyway so the
	/// fallback "is no worse than the behaviour it replaces". Here it is strictly worse — the
	/// alternative is a SIGSEGV with no log, or a corrupted managed heap. XNA leaves the contents
	/// of a never-written texture undefined in any case, so zeros are within the contract for the
	/// render-target and content-pipeline cases; the warning is the honest part.
	/// </para>
	/// <para>
	/// <b>The guard arms only on a positive identification of a GLES context</b>, never on a probe
	/// failure and never on <c>#if ANDROID</c>. Refusing a readback that would have worked returns
	/// zeros where a game expected pixels, so a false positive here is a visible regression on the
	/// desktop, while a false negative merely leaves today's behaviour in place.
	/// <see cref="VertexFormatExpansion"/> records what an unmeasured gate cost last time.
	/// </para>
	/// </remarks>
	internal static class TextureReadback
	{
		/* False until a device positively identifies itself as GLES. See the asymmetry note
		 * above: this one defaults to "the driver can cope", unlike TextureFormatShim.
		 */
		private static bool isGles;

		/// <summary>
		/// Records what the device said. Called by the graphics backend from the same call that
		/// creates the device.
		/// </summary>
		internal static void SetDeviceSupport(bool deviceIsGles, string detail)
		{
			isGles = deviceIsGles;
			refusalsReported = 0;

			/* One line per launch, unconditionally including the "nothing to guard" case, for
			 * the same reason as [wpr-vfmt] and [wpr-texfmt].
			 */
			XnaBackend.LogInfo(
				"[wpr-texget] texture readback — " +
				(isGles ? "LIMITED" : "serviceable") +
				" (" + detail + "); guard " +
				(isGles ? "ENABLED" : "not needed")
			);
		}

		/// <summary>Restores the default on device teardown.</summary>
		internal static void ClearDeviceSupport()
		{
			isGles = false;
		}

		/// <summary>
		/// Whether <c>GetTextureData2D</c> will genuinely answer for this texture, given the
		/// format it is <em>stored</em> in (not the one the game asked for) and the mip level.
		/// </summary>
		internal static bool CanServe2D(SurfaceFormat storageFormat, int level)
		{
			if (!isGles)
			{
				return true;
			}

			/* Only the level-0 FBO diversion works, and only for the formats where
			 * glReadPixels' hardcoded GL_RGBA/GL_UNSIGNED_BYTE is both the right size and the
			 * right interpretation. Every other 4-byte format — Single, Rg32, Rgba1010102 and
			 * friends — would come back as bytes read out of a float or packed surface, which
			 * is wrong data rather than a wrong length, so they are refused too.
			 */
			return level == 0 && IsEightBitColour(storageFormat);
		}

		/// <summary>
		/// Whether <c>GetTextureDataCube</c> will answer. On GLES it never can: that entry point
		/// has no render-target diversion and goes straight to the unresolved
		/// <c>glGetTexImage</c>.
		/// </summary>
		internal static bool CanServeCube()
		{
			return !isGles;
		}

		/* A game can poll GetData every frame, and a flood of identical lines would bury the
		 * launch line that says whether the guard is even armed. Eight, then silence.
		 */
		private const int MaxRefusalsReported = 8;
		private static int refusalsReported;

		/// <summary>
		/// Reports one refused readback, rate-limited. Goes through
		/// <see cref="XnaBackend.LogWarn"/> rather than <c>WprDebugTrace</c> so it survives a
		/// Release build — this is the diagnostic for a symptom (a game reading back zeros) that
		/// is otherwise completely silent.
		/// </summary>
		internal static void ReportRefusal(string what)
		{
			if (refusalsReported >= MaxRefusalsReported)
			{
				return;
			}

			refusalsReported += 1;
			XnaBackend.LogWarn(
				"[wpr-texget] GetData refused: " + what +
				" — this driver cannot read it back; returning zeros" +
				(refusalsReported == MaxRefusalsReported
					? " (further refusals will not be reported)"
					: "")
			);
		}

		private static bool IsEightBitColour(SurfaceFormat format)
		{
			return	format == SurfaceFormat.Color ||
				format == SurfaceFormat.ColorBgraEXT ||
				format == SurfaceFormat.ColorSrgbEXT;
		}
	}
}
