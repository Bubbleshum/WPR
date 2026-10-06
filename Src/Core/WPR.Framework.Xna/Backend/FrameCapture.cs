#nullable enable
using System;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// A hook that sees every finished frame just before it is presented, while the backbuffer
	/// still holds it.
	///
	/// <para><b>Why this exists.</b> The GPU test matrix (<c>.claude/rules/gpu-matrix-testing.md</c>)
	/// judges a game by what it draws on each driver, and has to do that from a headless harness:
	/// the shell that runs the harness cannot see the game's window, so a desktop screenshot
	/// captures whatever else is on screen. Reading the frame back from inside is also the only
	/// capture that is identical across D3D11, Vulkan and both flavours of OpenGL, which is what
	/// makes two drivers' pictures comparable at all.</para>
	///
	/// <para><b>Why before <c>Present</c>, not after.</b> <see cref="GraphicsDevice.Present()"/>
	/// discards the backbuffer straight after the swap (see
	/// <c>GraphicsDevice.DiscardBackbufferContents</c>), so anything that asks for the frame later —
	/// a posted game-thread action, a component — reads an empty surface.</para>
	///
	/// <para>Unset in every shipped head; one null check per frame. A handler runs on the game
	/// thread with the device current, so it may call
	/// <see cref="GraphicsDevice.GetBackBufferData{T}(T[])"/>. It must not throw — an exception is
	/// caught and dropped so a diagnostic can never take a game down.</para>
	/// </summary>
	public static class FrameCapture
	{
		public static Action<GraphicsDevice>? BeforePresent { get; set; }

		internal static void Raise(GraphicsDevice device)
		{
			Action<GraphicsDevice>? handler = BeforePresent;
			if (handler == null) return;
			try { handler(device); }
			catch (Exception ex) { XnaBackend.LogWarn("[wpr-capture] BeforePresent handler threw: " + ex.Message); }
		}
	}
}
