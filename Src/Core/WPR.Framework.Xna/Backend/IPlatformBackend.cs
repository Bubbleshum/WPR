#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// What the platform layer needs back from the game it is driving — the narrow slice of
	/// <c>Game</c> that <c>SDL2_FNAPlatform</c> actually touches.
	///
	/// <para><b>Why this exists rather than passing <c>Game</c>.</b> The platform seam has to be
	/// declared in <c>WPR.Framework.Xna</c> beside the other RHI seams, and <c>Game</c> is still a
	/// spine type living in the FNA backend — naming it here would mean referencing FNA from the
	/// framework, which is a cycle. Measuring the coupling showed the platform only ever reads
	/// five members off the game, so an interface it implements costs nothing and inverts the
	/// dependency. When <c>Game</c> moves up in step 2 this can stay exactly as it is: an
	/// implemented contract is a better boundary than a concrete parameter either way.</para>
	/// </summary>
	public interface IGameLoopHost
	{
		GameWindow Window { get; }
		GraphicsDevice GraphicsDevice { get; }

		/// <summary>Focus state. The platform SETS this from SDL focus events, which is what
		/// drives the game's Activated/Deactivated (and, on WP7 titles, tombstoning behaviour).</summary>
		bool IsActive { get; set; }

		/// <summary>Forces one Draw+Present outside the normal tick — used while the platform is
		/// blocked inside a window resize or move, where the OS owns the message pump.</summary>
		void RedrawWindow();

		/// <summary>The loop's run flag. The platform CLEARS it on an OS quit request — that is
		/// how a window-close reaches the game loop — so it is settable, not just readable.</summary>
		bool RunApplication { get; set; }
	}

	/// <summary>
	/// The windowing/event-pump seam — the last of the <c>WPR.Xna.Rhi</c> seams, and the one that
	/// lets the XNA <b>spine</b> (<c>Game</c> and friends) stop being backend-owned.
	///
	/// <para><b>Shape.</b> Same rule as the audio seams: this sits above the C ABI and speaks
	/// WPR-owned vocabulary only (<see cref="GameWindow"/>, <see cref="GraphicsAdapter"/>,
	/// <see cref="TouchPanelCapabilities"/> — all already in this assembly). The concrete window
	/// stays behind it: FNA's <c>FNAWindow : GameWindow</c> is <c>internal</c> to the backend, and
	/// nothing above the seam knows SDL exists.</para>
	///
	/// <para><b>What this seam is NOT.</b> It does not decide whether the game gets its own
	/// top-level window or is composited into the launcher shell. That is a product question, and
	/// it is answered by an <em>implementation</em> of this interface (or by a different
	/// <see cref="GameWindow"/> subclass behind the same <see cref="CreateWindow"/>), not by the
	/// contract. Keeping the two separable is deliberate: the migration plan had them fused, which
	/// is why the spine sat blocked on a UX decision it never actually depended on.</para>
	/// </summary>
	public interface IPlatformBackend
	{
		// ---- Window lifetime ----

		/// <summary>Creates the game's window. The returned instance is the backend's own
		/// <see cref="GameWindow"/> subclass; callers only ever see the abstract type.</summary>
		GameWindow CreateWindow();

		void DisposeWindow(GameWindow window);

		/// <summary>Applies the platform's DPI/display scaling to a requested backbuffer size.
		/// <paramref name="invert"/> converts the other way (logical to physical).</summary>
		void ScaleForWindow(System.IntPtr window, bool invert, ref int w, ref int h);

		/// <summary>Whether this platform can actually rotate — false on desktop, true on a phone.
		/// <c>GraphicsDeviceManager</c> uses it to decide whether an orientation request is
		/// meaningful or whether it has to fake the flip by swapping the backbuffer extents.</summary>
		bool SupportsOrientationChanges();

		// ---- Game registration ----

		/// <summary>Registers the running game with the platform and returns the adapter its window
		/// landed on.</summary>
		GraphicsAdapter RegisterGame(IGameLoopHost game);

		void UnregisterGame(IGameLoopHost game);

		// ---- Event pump ----

		/// <summary>
		/// Drains the platform event queue once. <paramref name="currentAdapter"/> is updated when
		/// the window moves to another display; the two text-input parameters carry the repeat
		/// state of the control keys across calls, which is why they are by-ref rather than owned
		/// here — the loop owns that state, not the platform.
		/// </summary>
		void PollEvents(
			IGameLoopHost game,
			ref GraphicsAdapter currentAdapter,
			bool[] textInputControlDown,
			ref bool textInputSuppress);

		/// <summary>
		/// True where the OS insists on owning the main loop (a UIKit/Android-style platform)
		/// rather than letting the game spin its own. When true the loop hands control to
		/// <see cref="RunPlatformMainLoop"/> and is called back per frame instead.
		/// </summary>
		bool NeedsPlatformMainLoop();

		void RunPlatformMainLoop(IGameLoopHost game);

		// ---- Misc platform services the loop needs ----

		void OnIsMouseVisibleChanged(bool visible);

		TouchPanelCapabilities GetTouchCapabilities();

		/// <summary>
		/// How many text-input control characters this platform handles (Home/End/Backspace/Tab/
		/// Enter/Delete/Paste — seven today). The loop sizes its key-repeat state array from this.
		///
		/// <para>On the seam rather than a constant in the loop because it is a genuine platform
		/// limit, not an XNA one — FNA's own comment on the array reads "Only 7 control keys
		/// supported at this time". Hardcoding 7 above the seam would silently mis-size the array
		/// for a platform that handled more.</para>
		/// </summary>
		int TextInputControlCharacterCount { get; }

		/// <summary>Shows a native error dialog. Used for the "unhandled exception escaped the
		/// game loop" path, where there is no game left to draw one.</summary>
		void ShowRuntimeError(string title, string message);
	}
}
