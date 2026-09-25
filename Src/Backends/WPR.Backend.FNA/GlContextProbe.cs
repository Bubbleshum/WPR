using System;
using System.Runtime.InteropServices;
using SDL2;
using F3D = Microsoft.Xna.Framework.Graphics.FNA3D;

namespace WPR.Backend.FNA
{
	/// <summary>
	/// Asks the GL context WPR is actually running on whether it is an OpenGL ES context, and
	/// whether it accepts the texture formats FNA3D maps onto desktop-only GL enums.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why ask rather than sniff the driver name.</b> Two facts matter here and a name carries
	/// neither. <c>SDL2_FNAPlatform.SelectedDriverName</c> is null whenever automatic selection
	/// wins, and <c>"OpenGL"</c> does not distinguish desktop GL from GLES — which is the whole
	/// question, because desktop GL has <c>GL_BGRA</c>, the <c>*_REV</c> packed types and
	/// <c>glGetTexImage</c>, and GLES has none of them.
	/// <c>GraphicsCapabilities.OffThreadResourceCreationFor</c> is a weak precedent to extend: that
	/// flag is a property of FNA3D's own source (<c>ForceToMainThread</c> exists in one driver
	/// file), genuinely derivable from a name. "Does this context have <c>GL_BGRA</c>" is a
	/// property of the context.
	/// </para>
	/// <para>
	/// <b>This is the same question FNA3D asks itself, answered the same way.</b>
	/// <c>FNA3D_Driver_OpenGL.c</c> sets <c>renderer-&gt;useES3</c> from
	/// <c>SDL_GL_GetAttribute(SDL_GL_CONTEXT_PROFILE_MASK)</c> immediately after creating the
	/// context, so reading that same attribute here gives a bit-identical answer with no change to
	/// FNA3D and no native rebuild. It is gated on <c>FNA3D_GetSysRendererEXT</c> reporting OpenGL
	/// first, because with no GL context the attribute means nothing.
	/// </para>
	/// <para>
	/// <b>The two answers have opposite safe directions, which is why they are separate outs.</b>
	/// Converting a texture format that did not need converting is lossless — it costs memory and
	/// nothing else — so an unproven answer converts. Refusing a readback that would have worked
	/// returns zeros where the game expected pixels, so <see cref="IsGles"/> is reported true only
	/// on a positive identification, never on a failure. Callers must preserve that asymmetry.
	/// </para>
	/// <para>
	/// Deliberately read-only: it reads two attributes and an extension string. Uploading a probe
	/// texture would answer more directly and would perturb FNA3D's <c>renderer-&gt;textures[]</c>
	/// binding cache, which is not worth it for a question the profile mask already settles.
	/// </para>
	/// </remarks>
	internal static class GlContextProbe
	{
		/* glGetString(GL_VERSION), for the launch log. Resolved through SDL rather than
		 * P/Invoked, because the GL entry points live in whichever library SDL loaded.
		 */
		private const uint GL_VERSION = 0x1F02;

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr glGetStringDelegate(uint name);

		/// <summary>
		/// Answers, for this device, whether it is a GLES context and which texture formats it can
		/// take.
		/// </summary>
		/// <param name="isGles">
		/// True only when this is positively identified as an OpenGL ES context. Any failure, and
		/// every non-OpenGL driver, answers false — see the asymmetry note above.
		/// </param>
		/// <param name="packedBgraUsable">
		/// Whether <c>Bgra5551</c> and <c>Bgra4444</c> can be uploaded as FNA3D maps them
		/// (<c>GL_BGRA</c> plus a <c>*_REV</c> packed short). False on GLES, where neither enum
		/// exists and no extension supplies them.
		/// </param>
		/// <param name="bgra8888Usable">
		/// Whether <c>ColorBgraEXT</c> can be uploaded as <c>GL_BGRA</c> + <c>GL_UNSIGNED_BYTE</c>.
		/// On GLES that needs <c>GL_EXT_texture_format_BGRA8888</c>, which many devices do have —
		/// hence a separate answer from the packed one rather than one "BGRA works" flag.
		/// </param>
		internal static void Query(
			IntPtr device,
			out bool isGles,
			out bool packedBgraUsable,
			out bool bgra8888Usable,
			out string detail
		) {
			isGles = false;
			packedBgraUsable = false;
			bgra8888Usable = false;

			try
			{
				/* FNA3D_GetSysRendererEXT returns silently without writing a byte when version
				 * does not match, so the sentinel is what tells a genuine answer from a call that
				 * was ignored. Without it an ignored call reads as rendererType 0 — which is
				 * OpenGL — and we would go on to read a profile mask for a context that does not
				 * exist. Same discipline as VulkanVertexFormatSupport.
				 */
				const F3D.FNA3D_SysRendererTypeEXT NotWritten = (F3D.FNA3D_SysRendererTypeEXT) (-1);

				F3D.FNA3D_SysRendererEXT sys = default;
				sys.version = F3D.FNA3D_SYSRENDERER_VERSION_EXT;
				sys.rendererType = NotWritten;
				F3D.FNA3D_GetSysRendererEXT(device, ref sys);

				if (sys.rendererType == NotWritten)
				{
					/* Cannot prove anything. Convert (lossless), do not guard (would return
					 * zeros from a readback that may well work).
					 */
					detail = "FNA3D declined the sysrenderer query";
					return;
				}

				if (sys.rendererType != F3D.FNA3D_SysRendererTypeEXT.FNA3D_RENDERER_TYPE_OPENGL_EXT)
				{
					/* D3D11 and Vulkan both take these formats, and both read textures back
					 * without the desktop-only entry points GLES lacks. Nothing below applies.
					 */
					packedBgraUsable = true;
					bgra8888Usable = true;
					detail = "not an OpenGL device";
					return;
				}

				int profileMask;
				if (SDL.SDL_GL_GetAttribute(
					SDL.SDL_GLattr.SDL_GL_CONTEXT_PROFILE_MASK,
					out profileMask
				) != 0) {
					detail = "SDL could not report the GL profile mask";
					return;
				}

				isGles = (profileMask & (int) SDL.SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_ES) != 0;
				string version = GetVersionString();

				if (!isGles)
				{
					/* Desktop GL. GL_BGRA, GL_UNSIGNED_SHORT_4_4_4_4_REV and
					 * GL_UNSIGNED_SHORT_1_5_5_5_REV are all core here, and glGetTexImage
					 * resolves, so FNA3D's tables are correct as they stand.
					 */
					packedBgraUsable = true;
					bgra8888Usable = true;
					detail = "desktop OpenGL (" + version + ")";
					return;
				}

				/* GLES. There is no GL_BGRA format and no *_REV packed short in any version of
				 * OpenGL ES, and no extension adds them — GL_EXT_texture_format_BGRA8888 covers
				 * 8888 only, which is why that one is asked separately.
				 */
				packedBgraUsable = false;
				bgra8888Usable =
					SDL.SDL_GL_ExtensionSupported("GL_EXT_texture_format_BGRA8888") == SDL.SDL_bool.SDL_TRUE ||
					SDL.SDL_GL_ExtensionSupported("GL_APPLE_texture_format_BGRA8888") == SDL.SDL_bool.SDL_TRUE;
				detail = "OpenGL ES (" + version + ")";
			}
			catch (Exception e)
			{
				/* EntryPointNotFoundException from an older FNA3D is the case worth naming. It
				 * leaves isGles false (do not guard) and both format flags false (convert), which
				 * is the pair of safe directions described above.
				 */
				isGles = false;
				packedBgraUsable = false;
				bgra8888Usable = false;
				detail = "probe failed (" + e.GetType().Name + ")";
			}
		}

		private static string GetVersionString()
		{
			try
			{
				IntPtr fn = SDL.SDL_GL_GetProcAddress("glGetString");
				if (fn == IntPtr.Zero)
				{
					return "version unknown";
				}

				glGetStringDelegate getString =
					Marshal.GetDelegateForFunctionPointer<glGetStringDelegate>(fn);
				IntPtr str = getString(GL_VERSION);
				if (str == IntPtr.Zero)
				{
					return "version unknown";
				}

				return Marshal.PtrToStringAnsi(str) ?? "version unknown";
			}
			catch (Exception)
			{
				/* The version is for the log line alone. Never let it decide anything, and never
				 * let it fail the probe.
				 */
				return "version unknown";
			}
		}
	}
}
