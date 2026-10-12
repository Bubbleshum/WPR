using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Something the HOST shows in place of the game for a while — today, a movie a title opened
	/// with <c>MediaPlayerLauncher</c>. While one is up, <see cref="Game.Tick"/> runs the overlay
	/// instead of the game's own <c>Update</c>/<c>Draw</c>.
	///
	/// <para>That is WP7's own model: <c>MediaPlayerLauncher.Show</c> handed the screen to the
	/// system player and the app stopped running until the player returned. Taking over the loop
	/// rather than drawing over the game also means the title cannot advance underneath, which it
	/// never could on the phone.</para>
	/// </summary>
	public interface IHostOverlay
	{
		/// <summary>One frame of input and timing. False when the overlay has finished; it is then
		/// removed and <see cref="Finish"/> runs, and the game resumes on the next tick.</summary>
		bool Update();

		/// <summary>Draws the frame. Runs between the game's BeginDraw and EndDraw, so it is
		/// presented like any game frame.</summary>
		void Draw(GraphicsDevice device);

		/// <summary>Releases what the overlay holds and, when <paramref name="completed"/>, tells
		/// whoever started it. Runs once: <paramref name="completed"/> is true when the overlay
		/// ended, was skipped, threw or was replaced, and false only at the host's teardown, where
		/// nothing may be posted to a game that is going away.</summary>
		void Finish(bool completed);
	}

	/// <summary>The one host overlay slot. Process-wide; cleared by <see cref="Clear"/> at teardown.</summary>
	public static class HostOverlay
	{
		private static IHostOverlay? _current;

		public static IHostOverlay? Current => _current;

		/// <summary>Shows <paramref name="overlay"/> from the next tick. Replaces (and finishes) any
		/// overlay already up.</summary>
		public static void Show(IHostOverlay overlay)
		{
			IHostOverlay? previous = _current;
			_current = overlay ?? throw new ArgumentNullException(nameof(overlay));
			if (previous != null && !ReferenceEquals(previous, overlay))
			{
				FinishSafely(previous, true);
			}
		}

		/// <summary>Ends the current overlay, if any. Called by the loop when it finishes, and by
		/// the host's teardown (with <c>completed: false</c>) so a game that exits mid-movie does not
		/// leave one behind.</summary>
		public static void Clear(bool completed = true)
		{
			IHostOverlay? previous = _current;
			_current = null;
			if (previous != null)
			{
				FinishSafely(previous, completed);
			}
		}

		private static void FinishSafely(IHostOverlay overlay, bool completed)
		{
			try
			{
				overlay.Finish(completed);
			}
			catch (Exception ex)
			{
				XnaBackend.LogWarn("[wpr-overlay] Finish threw: " + ex);
			}
		}
	}
}
