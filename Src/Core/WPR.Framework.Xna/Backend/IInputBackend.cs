using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// The XNA input-device seam — Stage 5c-5 (Plans/STAGE5C-SCOPE.md). Backs
	/// <c>GamePad</c>/<c>Keyboard</c>/<c>Mouse</c>/<c>TextInputEXT</c>/<c>TouchPanel</c>.
	///
	/// <para><b>Why this is not <c>WPR.Abstractions.Input.IInputProvider</c>.</b> The original plan
	/// had input riding that scaffold, but it hits the same cycle 5c-0 found for graphics: the input
	/// vocabulary <em>is</em> the XNA types (<see cref="GamePadState"/>, <see cref="Keys"/>,
	/// <see cref="TouchPanelCapabilities"/>, <see cref="GamePadDeadZone"/>, …), which live in
	/// <c>WPR.Framework.Xna</c> — so putting the contract in the dependency-free
	/// <c>WPR.Abstractions</c> would force <c>Abstractions → WPR.Framework.Xna</c> while the
	/// framework consumes the contract. It therefore sits beside the other seams in
	/// <c>WPR.Xna.Rhi</c>. <c>IInputProvider</c> stays what it is: the generic host-level input
	/// abstraction, unrelated to the XNA device API.</para>
	///
	/// <para><b>Shape: a 1:1 mirror of FNA's <c>FNAPlatform</c> input delegate table.</b> Unlike
	/// audio, no reshaping was warranted — every one of these already takes and returns WPR-owned
	/// value types, carries no delegates or native structs, and is device-poll-grained (once per
	/// frame at most). Mirroring keeps the moved call sites byte-for-byte and makes an SDL-free
	/// backend implement exactly the operations XNA actually asks for.</para>
	///
	/// <para><b>The pump runs the other way.</b> Only these <em>pull</em> operations cross the seam.
	/// Event delivery still flows from the platform's event loop directly into the moved types'
	/// internals (<c>Keyboard.keys</c>, <c>Mouse.INTERNAL_*</c>, <c>TouchPanel.INTERNAL_onTouchEvent</c>,
	/// <c>TextInputEXT.OnTextInput</c>, …), which the backend reaches through this assembly's
	/// <c>InternalsVisibleTo</c> — the same push-into-moved-types arrangement the spine already uses.</para>
	/// </summary>
	public interface IInputBackend
	{
		// ---- Keyboard ----

		/// <summary>Maps a physical scancode to the layout-dependent key it currently produces.</summary>
		Keys GetKeyFromScancode(Keys scancode);

		// ---- Text input (the IME / on-screen-keyboard channel, XNA's TextInputEXT) ----

		void StartTextInput();
		void StopTextInput();

		/// <summary>Tells the platform where the composition window should sit, in client coordinates.</summary>
		void SetTextInputRectangle(Rectangle rectangle);

		// ---- Mouse ----

		/// <summary>Polls the pointer position and button states for the given window.</summary>
		void GetMouseState(
			IntPtr window,
			out int x,
			out int y,
			out ButtonState left,
			out ButtonState middle,
			out ButtonState right,
			out ButtonState x1,
			out ButtonState x2);

		void SetMousePosition(IntPtr window, int x, int y);

		bool GetRelativeMouseMode();
		void SetRelativeMouseMode(bool enable);

		// ---- GamePad ----

		GamePadCapabilities GetGamePadCapabilities(int index);

		/// <summary>Polls a pad, applying the requested dead-zone treatment.</summary>
		GamePadState GetGamePadState(int index, GamePadDeadZone deadZoneMode);

		/// <summary>Returns false when the pad has no rumble motors (XNA's documented result).</summary>
		bool SetGamePadVibration(int index, float leftMotor, float rightMotor);
		bool SetGamePadTriggerVibration(int index, float leftTrigger, float rightTrigger);

		/// <summary>The pad's stable device GUID, used by games to look up a button-mapping profile.</summary>
		string GetGamePadGUID(int index);

		void SetGamePadLightBar(int index, Color color);

		/// <summary>Reads a motion-capable pad's gyro/accelerometer. False if it has no such sensor.</summary>
		bool GetGamePadGyro(int index, out Vector3 gyro);
		bool GetGamePadAccelerometer(int index, out Vector3 accel);

		// ---- Touch ----

		TouchPanelCapabilities GetTouchCapabilities();

		/// <summary>Drains the platform's pending touch events into <c>TouchPanel</c>'s finger slots.
		/// A push disguised as a pull: the backend calls back into <c>TouchPanel</c>'s internals, and
		/// <c>TouchPanel.Update</c> drives it so the drain happens at a defined point in the frame.</summary>
		void UpdateTouchPanelState();
	}
}
