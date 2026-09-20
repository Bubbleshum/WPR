using System;
using System.Runtime.InteropServices;
using SDL2;
using F3D = Microsoft.Xna.Framework.Graphics.FNA3D;

namespace WPR.Backend.FNA
{
	/// <summary>
	/// Asks the Vulkan device WPR is actually running on whether it accepts the three
	/// vertex formats FNA3D maps to Vulkan's optional <c>*_SCALED</c> types.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why ask rather than assume.</b> <c>VK_FORMAT_FEATURE_VERTEX_BUFFER_BIT</c> is
	/// optional for the <c>*_SCALED</c> formats, so whether they work is a property of
	/// the GPU, not of the driver or the platform. Adreno does not implement them, which
	/// is what T-posed Mirror's Edge; plenty of other Vulkan implementations do, and on
	/// those the expansion in <c>VertexFormatExpansion</c> is a rewrite of the game's
	/// vertex layout that buys nothing. The device is the only thing that knows.
	/// </para>
	/// <para>
	/// <b>This needs no change to FNA3D and no native rebuild.</b>
	/// <c>FNA3D_GetSysRendererEXT</c> hands back the very <c>VkInstance</c> and
	/// <c>VkPhysicalDevice</c> the driver chose, and SDL already exposes
	/// <c>vkGetInstanceProcAddr</c>, so the query is three calls over handles we are
	/// given. Creating our own instance to ask would have been the obvious alternative
	/// and is worse: it would answer for whichever physical device *we* picked, which on
	/// a multi-GPU machine need not be the one FNA3D is rendering with.
	/// </para>
	/// <para>
	/// <b>Every failure answers "unsupported".</b> A missing export, a driver that is not
	/// Vulkan, a null handle, a loader that will not resolve the function — all of it
	/// means we could not prove the format works, and the expansion is lossless, so the
	/// safe answer is to expand. The cost of being wrong that way is a wider vertex
	/// buffer; the cost of the opposite is a character stuck in bind pose.
	/// </para>
	/// </remarks>
	internal static class VulkanVertexFormatSupport
	{
		/* VkFormat. These three are exactly what XNAToVK_VertexAttribType maps
		 * Byte4 / Short2 / Short4 onto in FNA3D_Driver_Vulkan.c.
		 */
		private const uint VK_FORMAT_R8G8B8A8_USCALED = 39;
		private const uint VK_FORMAT_R16G16_SSCALED = 80;
		private const uint VK_FORMAT_R16G16B16A16_SSCALED = 94;

		/* VkFormatFeatureFlagBits */
		private const uint VK_FORMAT_FEATURE_VERTEX_BUFFER_BIT = 0x00000040;

		[StructLayout(LayoutKind.Sequential)]
		private struct VkFormatProperties
		{
			public uint linearTilingFeatures;
			public uint optimalTilingFeatures;
			public uint bufferFeatures;
		}

		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		private delegate IntPtr vkGetInstanceProcAddrDelegate(
			IntPtr instance,
			[MarshalAs(UnmanagedType.LPStr)] string name
		);

		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		private delegate void vkGetPhysicalDeviceFormatPropertiesDelegate(
			IntPtr physicalDevice,
			uint format,
			out VkFormatProperties properties
		);

		/// <summary>
		/// Answers, for this device, whether each of the three formats may be used in a
		/// vertex buffer. A format that cannot be proved usable is reported unusable.
		/// </summary>
		internal static void Query(
			IntPtr device,
			out bool byte4,
			out bool short2,
			out bool short4,
			out string detail
		) {
			byte4 = false;
			short2 = false;
			short4 = false;

			try
			{
				/* FNA3D_GetSysRendererEXT returns silently without writing a byte when
				 * version does not match, so the sentinel is what tells a genuine answer
				 * from a call that was ignored. Without it an ignored call would read as
				 * rendererType 0 — OpenGL — and report every format usable, which on an
				 * Adreno is exactly the T-pose this whole mechanism exists to prevent.
				 */
				const F3D.FNA3D_SysRendererTypeEXT NotWritten = (F3D.FNA3D_SysRendererTypeEXT) (-1);

				F3D.FNA3D_SysRendererEXT sys = default;
				sys.version = F3D.FNA3D_SYSRENDERER_VERSION_EXT;
				sys.rendererType = NotWritten;
				F3D.FNA3D_GetSysRendererEXT(device, ref sys);

				if (sys.rendererType == NotWritten)
				{
					detail = "FNA3D declined the sysrenderer query";
					return;
				}

				if (sys.rendererType != F3D.FNA3D_SysRendererTypeEXT.FNA3D_RENDERER_TYPE_VULKAN_EXT)
				{
					/* Not Vulkan, so the *_SCALED problem does not exist and every
					 * format is usable as-is. Reading the union's Vulkan arm would be
					 * meaningless here, which is why this is tested before anything
					 * touches those handles.
					 */
					byte4 = true;
					short2 = true;
					short4 = true;
					detail = "not a Vulkan device";
					return;
				}

				if (sys.instance == IntPtr.Zero || sys.physicalDevice == IntPtr.Zero)
				{
					detail = "Vulkan handles unavailable";
					return;
				}

				IntPtr getProcAddr = SDL.SDL_Vulkan_GetVkGetInstanceProcAddr();
				if (getProcAddr == IntPtr.Zero)
				{
					detail = "SDL could not supply vkGetInstanceProcAddr";
					return;
				}

				vkGetInstanceProcAddrDelegate gipa =
					Marshal.GetDelegateForFunctionPointer<vkGetInstanceProcAddrDelegate>(getProcAddr);

				IntPtr fn = gipa(sys.instance, "vkGetPhysicalDeviceFormatProperties");
				if (fn == IntPtr.Zero)
				{
					detail = "vkGetPhysicalDeviceFormatProperties not resolvable";
					return;
				}

				vkGetPhysicalDeviceFormatPropertiesDelegate getProperties =
					Marshal.GetDelegateForFunctionPointer<vkGetPhysicalDeviceFormatPropertiesDelegate>(fn);

				byte4 = SupportsVertexBuffer(getProperties, sys.physicalDevice, VK_FORMAT_R8G8B8A8_USCALED);
				short2 = SupportsVertexBuffer(getProperties, sys.physicalDevice, VK_FORMAT_R16G16_SSCALED);
				short4 = SupportsVertexBuffer(getProperties, sys.physicalDevice, VK_FORMAT_R16G16B16A16_SSCALED);
				detail = "queried";
			}
			catch (Exception e)
			{
				/* EntryPointNotFoundException from an older FNA3D is the case worth
				 * naming: it leaves all three false, i.e. expand, which is exactly the
				 * behaviour this code replaced.
				 */
				byte4 = false;
				short2 = false;
				short4 = false;
				detail = "query failed (" + e.GetType().Name + ")";
			}
		}

		private static bool SupportsVertexBuffer(
			vkGetPhysicalDeviceFormatPropertiesDelegate getProperties,
			IntPtr physicalDevice,
			uint format
		) {
			VkFormatProperties properties;
			getProperties(physicalDevice, format, out properties);
			return (properties.bufferFeatures & VK_FORMAT_FEATURE_VERTEX_BUFFER_BIT) != 0;
		}
	}
}
