using System;
using Microsoft.Xna.Framework;
using WPR.Common;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Where the backbuffer lands inside the host window when a desktop game runs fullscreen.
	///
	/// <para>A WP7 game draws into a fixed 480x800 or 800x480 backbuffer. Windowed, the host window
	/// is exactly that size, so the backbuffer fills it. Fullscreen, the window is the whole monitor,
	/// and stretching a portrait backbuffer across a 16:9 panel would squash every game flat. So the
	/// backbuffer is fitted to the largest rectangle of its own aspect ratio, centred, and the rest
	/// is black: side bars for a portrait game, top and bottom bars for a landscape one on a panel
	/// narrower than 5:3.</para>
	///
	/// <para><see cref="Destination"/> is set by the platform (it knows the drawable size) and read
	/// by <see cref="Microsoft.Xna.Framework.Graphics.GraphicsDevice.Present()"/>. Null means "fill
	/// the drawable", which is the windowed case and every mobile platform: a phone stretches to its
	/// panel exactly as it did before this existed.</para>
	///
	/// <para>The bars are never drawn. They stay black because FNA3D's D3D11 swapchain is resized
	/// when the backbuffer is refreshed for fullscreen, and new video memory comes back zeroed. The
	/// vendored FNA3D does not clear outside the destination rectangle, and the Windows
	/// <c>FNA3D.dll</c> cannot be rebuilt here, so this is the one lever available.</para>
	/// </summary>
	internal static class PresentationLetterbox
	{
		/// <summary>The backbuffer's destination in drawable pixels, or null to fill the drawable.</summary>
		internal static Rectangle? Destination { get; set; }

		/// <summary>The player's saved choice. Read live, so a launch picks up the last toggle.</summary>
		internal static bool FullscreenPreferred => Configuration.Current?.GameFullscreen == true;

		/// <summary>Remembers the player's choice so the next game opens the same way.</summary>
		internal static void SavePreference(bool fullscreen)
		{
			Configuration? config = Configuration.Current;
			if (config == null || config.GameFullscreen == fullscreen)
			{
				return;
			}

			config.GameFullscreen = fullscreen;
			try
			{
				config.Save();
			}
			catch (Exception e)
			{
				System.Diagnostics.Trace.WriteLine("[wpr-fullscreen] could not save the setting: " + e.Message);
			}
		}

		/// <summary>
		/// The largest rectangle with <paramref name="innerW"/>:<paramref name="innerH"/>'s aspect
		/// ratio that fits in <paramref name="outerW"/>x<paramref name="outerH"/>, centred.
		/// </summary>
		internal static Rectangle Fit(int outerW, int outerH, int innerW, int innerH)
		{
			if (outerW <= 0 || outerH <= 0 || innerW <= 0 || innerH <= 0)
			{
				return new Rectangle(0, 0, Math.Max(outerW, 0), Math.Max(outerH, 0));
			}

			// Compare outerW/outerH against innerW/innerH without dividing.
			int w, h;
			if ((long) outerW * innerH > (long) outerH * innerW)
			{
				// Outer is wider: full height, bars at the sides.
				h = outerH;
				w = (int) Math.Round((double) outerH * innerW / innerH);
			}
			else
			{
				// Outer is taller (or equal): full width, bars top and bottom.
				w = outerW;
				h = (int) Math.Round((double) outerW * innerH / innerW);
			}

			return new Rectangle((outerW - w) / 2, (outerH - h) / 2, w, h);
		}
	}
}
