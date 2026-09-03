#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Head-supplied policy for emulating a phone's input devices from a desktop keyboard.
	///
	/// <para>Renamed from ITiltEmulationHost on 2026-09-03. It began as tilt only, but the same
	/// keyboard now also stands in for the WP7 hardware Back button, and the name had started to
	/// lie about the contract. One host, one module, several emulated devices.</para>
	///
	/// <para><b>Why a seam at all.</b> The emulator's two moving parts are XNA
	/// <c>GameComponent</c>s: they have to be attached to the running <c>Game</c> and ticked by
	/// its loop, so they must derive from the spine types and therefore must live in the backend.
	/// But the *policy* they need — which key means "tilt left", whether the overlay is on, where
	/// the synthesized reading goes — is head state: it comes out of <c>Configuration</c>, and the
	/// binding table is shared with the Silverlight host, which resolves the same persisted key
	/// names against <c>Avalonia.Input.Key</c>. Moving that into the backend would put Avalonia in
	/// a graphics backend. So the components moved down and the policy stayed up, with this
	/// between them.</para>
	///
	/// <para><b>The split is "mechanism below, meaning above".</b> The backend does the
	/// XNA-mechanical work — poll <c>Keyboard.GetState</c> on the game's own tick, resolve the
	/// display orientation, draw the dial — and reports raw facts here. The head does everything
	/// that requires knowing what a key *means*: binding lookup, edge detection, and feeding its
	/// own accelerometer host. Nothing in this contract names a WPR-owned tilt vocabulary, which
	/// is what keeps the backend from needing the head's <c>TiltDirection</c> enum.</para>
	///
	/// <para>Optional, and unset is the normal case on a phone: Android has a real accelerometer
	/// and no keyboard, so its head registers nothing and the backend attaches no components.
	/// Same degradation as <see cref="XnaBackend.Achievements"/> — absent means "this feature is
	/// not available here", never an exception.</para>
	/// </summary>
	public interface IKeyboardEmulationHost
	{
		/// <summary>
		/// Called once per launch, before the components are attached, so the head can refresh
		/// its runtime knobs from configuration. Kept explicit rather than folded into the
		/// property getters because the existing behaviour reads config exactly once at attach
		/// time, and a per-tick re-read would be a silent behaviour change.
		/// </summary>
		void PrepareForLaunch();

		/// <summary>Whether to attach the on-screen tilt dial as well as the input component.
		/// Read once, at attach time.</summary>
		bool IsOverlayEnabled { get; }

		/// <summary>
		/// The orientation the game is actually presenting at, resolved by the backend once per
		/// tick. The head needs it to rotate screen-relative key intent ("W = tilt away from me")
		/// into the device-frame axis a landscape game reads.
		/// </summary>
		void ReportOrientation(DisplayOrientation orientation);

		/// <summary>
		/// Every key currently held, once per tick, straight from <c>Keyboard.GetState</c>. The
		/// head resolves these against its binding table and does its own edge detection — which
		/// is why this reports the whole set rather than transitions: only the head knows which
		/// keys are interesting, so only the head can say when one changed.
		/// </summary>
		void ReportPressedKeys(Keys[] pressedKeys);

		/// <summary>
		/// The current synthesized acceleration in <b>screen</b> space, for the overlay dial —
		/// deliberately not the device-frame reading the game sees, so the dot mirrors the user's
		/// key presses 1:1 regardless of how the game is rotated.
		///
		/// <para><c>System.Numerics.Vector3</c>, not the XNA one, matching
		/// <c>WPR.Engine.Sensors.IAccelerometerProvider</c>: this is a raw motion sample, and the
		/// head that produces it already speaks the neutral type. The overlay reads two floats off
		/// it, so converting would cost a conversion and buy nothing.</para>
		/// </summary>
		System.Numerics.Vector3 ScreenAcceleration { get; }

		/// <summary>
		/// Is this key the configured desktop stand-in for the WP7 hardware Back button?
		///
		/// <para>Asked by <c>SDL2_FNAPlatform.PollEvents</c> on each non-repeat key <b>down
		/// event</b>, right beside the hardcoded <c>SDLK_AC_BACK</c> test. Answering here rather
		/// than hardcoding Escape is what makes the key rebindable
		/// (<c>Configuration.BackKey</c>); the phone's own Back keycode is not a preference and
		/// still bypasses this entirely, so Android — which registers no emulation host — is
		/// unaffected.</para>
		///
		/// <para><b>This has to be event-driven, and that was measured.</b> The obvious
		/// alternative — have the XNA input component test <see cref="ReportPressedKeys"/> and
		/// queue a press — silently drops any tap that goes down and up between two polls, since
		/// <c>Keyboard.GetState()</c> is a per-frame snapshot. A synthetic <c>{ESC}</c> lands
		/// inside one 16 ms frame and was missed every time. A human press spans several frames
		/// and would usually survive, which is exactly what makes that shape a bad bet: it fails
		/// rarely and unreproducibly. The keydown event cannot miss one.</para>
		///
		/// <para>Must be a cheap, side-effect-free predicate: it is called for every key down of
		/// every frame, on the SDL event thread.</para>
		/// </summary>
		bool IsBackKey(Keys key);

		/// <summary>
		/// Tells the host a key went down, so it can start a bound touch gesture. Called from
		/// <c>SDL2_FNAPlatform.PollEvents</c> on each non-repeat key down, for the same reason
		/// <see cref="IsBackKey"/> is answered there: a tap that begins and ends inside one frame
		/// is invisible to <see cref="ReportPressedKeys"/>, which is a per-frame snapshot.
		///
		/// <para>Returns true if a gesture was started, purely so the caller can trace it.</para>
		/// </summary>
		bool NotifyKeyDown(Keys key);

		/// <summary>
		/// Advances any gesture in flight by one tick and returns what the synthetic finger should
		/// be doing right now, or <see cref="SyntheticTouchSample.Inactive"/> when nothing is
		/// running. Called exactly once per tick by the backend's injector.
		///
		/// <para>The animation lives on this side, not in the backend, for the same reason the
		/// tilt smoothing does: the backend supplies mechanism, the host supplies meaning, and a
		/// swipe's shape (where it starts, where it ends, how long it takes) is entirely
		/// binding-derived. The backend only needs to know where to put a finger this frame.</para>
		/// </summary>
		SyntheticTouchSample AdvanceSyntheticTouch();
	}
}
