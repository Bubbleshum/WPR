using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// A game loop that runs INSIDE a page rather than owning the window: on WP8 a
	/// DrawingSurfaceBackgroundGrid handed its content provider a device and a frame callback, and
	/// a library such as the SharpDX Toolkit (WPR.Framework.SharpDX) ran its Game from there. The
	/// Silverlight host drives it from its own tick, under the page.
	/// </summary>
	public interface IEmbeddedGameLoop
	{
		/// <summary>One update. Runs on the game thread, before the page's own update.</summary>
		void Update(GameTime gameTime);

		/// <summary>Draws the frame onto the host's back buffer. Runs between BeginDraw and EndDraw.</summary>
		void Draw(GameTime gameTime, GraphicsDevice device);

		/// <summary>The host gained or lost the foreground (WP8's Activated / Deactivated).</summary>
		void SetActive(bool active);

		/// <summary>True once the embedded game asked to exit; the host then ends the game.</summary>
		bool ExitRequested { get; }
	}

	/// <summary>The one embedded-loop slot. Process-wide; cleared at teardown by the host.</summary>
	public static class EmbeddedGameLoop
	{
		public static IEmbeddedGameLoop? Current { get; private set; }

		/// <summary>
		/// The page over the loop is LandscapeLeft (device turned anticlockwise) rather than
		/// LandscapeRight or plain Landscape. The loop draws into the phone's portrait frame
		/// already turned for that side, so it is presented with the opposite turn. Set by the
		/// Silverlight host on every navigation.
		/// </summary>
		public static bool PageLandscapeLeft { get; set; }

		public static void Set(IEmbeddedGameLoop? loop) => Current = loop;

		public static void Clear() => Current = null;
	}
}
