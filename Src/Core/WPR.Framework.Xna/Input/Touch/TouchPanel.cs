#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
using WPR.Xna.Rhi;
using System.Collections.Generic;
#endregion

namespace Microsoft.Xna.Framework.Input.Touch
{
	// https://msdn.microsoft.com/en-us/library/microsoft.xna.framework.input.touch.touchpanel.aspx
	public static class TouchPanel
	{
		#region Internal Constants

		// The maximum number of simultaneous touches allowed by XNA.
		internal const int MAX_TOUCHES = 8;

		// The value that represents the absence of a finger.
		internal const int NO_FINGER = -1;

		#endregion

		#region Public Static Properties

		public static int DisplayWidth
		{
			get;
			set;
		}

		public static int DisplayHeight
		{
			get;
			set;
		}

		public static DisplayOrientation DisplayOrientation
		{
			get;
			set;
		}

		public static GestureType EnabledGestures
		{
			get;
			set;
		}

		public static bool IsGestureAvailable
		{
			get
			{
				return gestures.Count > 0;
			}
		}

		public static IntPtr WindowHandle
		{
			get;
			set;
		}

		public static bool MouseAsTouch
		{
			get => _MouseAsTouch;
			set {
				if (!value && TouchDeviceExists)
				{
					TouchDeviceExists = false;
				}
				else if (value)
				{
					/* WPR change: eagerly mark a touch device as present whenever
					 * MouseAsTouch is enabled. The original FNA behaviour only flipped
					 * TouchDeviceExists on the first SDL_MOUSEBUTTONDOWN, which left
					 * GetCapabilities().IsConnected reporting false until the user
					 * physically clicked. WP7 games commonly probe IsConnected during
					 * their Game constructor / LoadContent and cache the result; if
					 * they see "false" once they may disable touch input handling for
					 * the entire session, leaving the user unable to click anything.
					 * Returning IsConnected=true the moment MouseAsTouch goes true
					 * matches what the host clearly intends when it sets that flag. */
					TouchDeviceExists = true;
				}

				_MouseAsTouch = value;
			}
		}


		#endregion

		#region Internal Static Variables

		internal static bool TouchDeviceExists;
		internal static bool _MouseAsTouch = false;

		/// <summary>
		/// WPR addition. How many finger slots at the top of <c>touches[]</c> are owned by a
		/// synthetic-touch source and must NOT be written or cleared by the platform's own drain.
		///
		/// <para>The drain (<c>SDL2_FNAPlatform.UpdateTouchPanelState</c>) writes real fingers
		/// from slot 0 upwards and then clears every slot up to its limit, so without a reservation
		/// there is no free slot: anything an injector writes is wiped in the same tick. This is
		/// the same trick the mouse-as-touch slot already uses, generalised — reserved slots sit
		/// immediately below the mouse slot.</para>
		///
		/// <para>Set by the injector when it is installed, and left alone otherwise. Zero means
		/// "no synthetic source", which is the normal case.</para>
		/// </summary>
		internal static int ReservedFingerSlots = 0;

		/// <summary>
		/// Index of the first slot owned by a synthetic source — equivalently, the exclusive upper
		/// bound of the range the platform drain may touch. The rule lives here so the drain and
		/// the injector cannot disagree about where the boundary is.
		/// </summary>
		internal static int FirstReservedFingerSlot
		{
			get
			{
				int limit = (_MouseAsTouch ? MAX_TOUCHES - 1 : MAX_TOUCHES) - ReservedFingerSlots;
				return limit < 0 ? 0 : limit;
			}
		}

		/// <summary>
		/// Is a real finger (or the mouse standing in for one) currently down? An injector uses
		/// this to stay out of the way: <c>GestureDetector</c> tracks a single active finger and a
		/// second one starts a Pinch, so overlapping a synthetic drag with a real touch produces
		/// gestures the user never made.
		/// </summary>
		internal static bool HasRealTouchDown
		{
			get
			{
				int reservedFrom = FirstReservedFingerSlot;
				for (int i = 0; i < MAX_TOUCHES; i += 1)
				{
					// Skip the reserved band itself — that is the injector's own finger.
					if (i >= reservedFrom && i < MAX_TOUCHES - (_MouseAsTouch ? 1 : 0))
					{
						continue;
					}

					TouchLocationState s = touches[i].State;
					if (s == TouchLocationState.Pressed || s == TouchLocationState.Moved)
					{
						return true;
					}
				}
				return false;
			}
		}
		// SDL_TouchID is a 64-bit value (on Windows it's derived from an hDevice pointer,
		// so the upper bits matter on x64). Keep this as long so the device handle round-trips
		// intact between SDL_FINGERDOWN and UpdateTouchPanelState's SDL_GetNumTouchFingers call —
		// truncating to int silently zeroed the upper bits and made real-touchscreen polling
		// always return 0 fingers.
		internal static long LastActiveTouchId = 0;

		#endregion

		#region Private Static Variables

		private static Queue<GestureSample> gestures = new Queue<GestureSample>();
		private static TouchLocation[] touches = new TouchLocation[MAX_TOUCHES];
		private static TouchLocation[] prevTouches = new TouchLocation[MAX_TOUCHES];
		private static List<TouchLocation> validTouches = new List<TouchLocation>();

		#endregion

		#region Public Static Methods

		public static TouchPanelCapabilities GetCapabilities()
		{
			return XnaBackend.Input.GetTouchCapabilities();
		}

		private static int _wprGetStateTraceCount;
		public static TouchCollection GetState()
		{
			validTouches.Clear();
			for (int i = 0; i < MAX_TOUCHES; i += 1)
			{
				if (touches[i].State != TouchLocationState.Invalid)
				{
					validTouches.Add(touches[i]);
				}
			}
			// Log only when the collection is non-empty — confirms the game is
			// reading touch state when the user clicks, and what State value the
			// game sees. If we see SetFinger populate touches[0] but never see this
			// trace, the game's Update isn't calling TouchPanel.GetState() (i.e.
			// CGame1.g() isn't being dispatched). If we DO see this trace but the
			// game still doesn't advance, the gate is inside the obfuscated state
			// class — not in our touch plumbing.
			if (validTouches.Count > 0 && _wprGetStateTraceCount < 30)
			{
				_wprGetStateTraceCount++;
				WprDebugTrace.WriteLine($"[wpr-trace] TouchPanel.GetState #{_wprGetStateTraceCount}: count={validTouches.Count} t0.State={validTouches[0].State} t0.Id={validTouches[0].Id} t0.Pos=({validTouches[0].Position.X:F1},{validTouches[0].Position.Y:F1})");
			}
			return new TouchCollection(validTouches.ToArray());
		}

		public static GestureSample ReadGesture()
		{
			if (gestures.Count == 0)
			{
				throw new InvalidOperationException();
			}
			return gestures.Dequeue();
		}

		#endregion

		#region Internal Static Methods

		internal static void EnqueueGesture(GestureSample gesture)
		{
			gestures.Enqueue(gesture);
		}

		// Counts the first 30 touch events so we can confirm clicks/taps reach FNA at
		// all. Press events are loudest signal; if we never see a Pressed log here a
		// click in the host window isn't being translated to a touch.
		private static int _wprTouchTraceCount;

		internal static void INTERNAL_onTouchEvent(
			int fingerId,
			TouchLocationState state,
			float x,
			float y,
			float dx,
			float dy
		)
		{
			// Calculate the scaled touch position
			Vector2 touchPos = new Vector2(
				(float) Math.Round(x * DisplayWidth),
				(float) Math.Round(y * DisplayHeight)
			);

			if (_wprTouchTraceCount < 30 && state != TouchLocationState.Moved)
			{
				_wprTouchTraceCount++;
				WprDebugTrace.WriteLine($"[wpr-trace] TouchPanel.INTERNAL_onTouchEvent #{_wprTouchTraceCount}: finger={fingerId} state={state} pos=({touchPos.X:F1},{touchPos.Y:F1}) display={DisplayWidth}x{DisplayHeight} mouseAsTouch={_MouseAsTouch} deviceExists={TouchDeviceExists}");
			}

			// Notify the Gesture Detector about the event
			switch (state)
			{
				case TouchLocationState.Pressed:
					GestureDetector.OnPressed(fingerId, touchPos);
					break;

				case TouchLocationState.Moved:

					/* WPR change: don't Math.Round here. For mouse-as-touch, the
					 * caller passes (motion.xrel / WindowWidth, motion.yrel /
					 * WindowHeight) — a 1-pixel cursor move in a wide host window
					 * comes out as e.g. dx ≈ 0.0008; multiplied by a 480-wide phone
					 * display that's 0.375 pixels, which Math.Round flattens to 0.
					 * Slow-moving mouse drags then deliver delta=(0,0) to every
					 * gesture sample, breaking games that integrate deltas
					 * (Pac-Man drag/pinch in particular). Use the float value
					 * directly so sub-pixel deltas survive — GestureSample's
					 * fields are floats so consumers tolerate fractional values.
					 */
					Vector2 delta = new Vector2(
						(float) (dx * DisplayWidth),
						(float) (dy * DisplayHeight)
					);

					GestureDetector.OnMoved(fingerId, touchPos, delta);

					break;

				case TouchLocationState.Released:
					GestureDetector.OnReleased(fingerId, touchPos);
					break;
			}
		}

		private static int _wprSetFingerTraceCount;
		internal static void SetFinger(int index, int fingerId, Vector2 fingerPos)
		{
			// Trace the first N SetFinger calls — confirms the touches[] array gets
			// updated by the mouse-as-touch poll path. Without entries here, GetState()
			// returns empty even if the user is clicking.
			if (_wprSetFingerTraceCount < 30 && (fingerId != NO_FINGER || prevTouches[index].State == TouchLocationState.Pressed || prevTouches[index].State == TouchLocationState.Moved))
			{
				_wprSetFingerTraceCount++;
				WprDebugTrace.WriteLine($"[wpr-trace] TouchPanel.SetFinger #{_wprSetFingerTraceCount}: idx={index} finger={fingerId} pos=({fingerPos.X:F1},{fingerPos.Y:F1}) prevState={prevTouches[index].State}");
			}
			if (fingerId == NO_FINGER)
			{
				// Was there a finger here before and the user just released it?
				if (prevTouches[index].State != TouchLocationState.Invalid
					&& prevTouches[index].State != TouchLocationState.Released)
				{
					touches[index] = new TouchLocation(
						prevTouches[index].Id,
						TouchLocationState.Released,
						prevTouches[index].Position,
						prevTouches[index].State,
						prevTouches[index].Position
					);
				}
				else
				{
					/* Nothing interesting here at all.
					 * Insert invalid data so this element
					 * is not included in GetState().
					 */
					touches[index] = new TouchLocation(
						NO_FINGER,
						TouchLocationState.Invalid,
						Vector2.Zero
					);
				}

				return;
			}

			// Is this a newly pressed finger?
			if (prevTouches[index].State == TouchLocationState.Invalid)
			{
				touches[index] = new TouchLocation(
					fingerId,
					TouchLocationState.Pressed,
					fingerPos
				);
			}
			else
			{
				// This finger was already down, so it's "moved"
				touches[index] = new TouchLocation(
					fingerId,
					TouchLocationState.Moved,
					fingerPos,
					prevTouches[index].State,
					prevTouches[index].Position
				);
			}
		}

		/// <summary>
		/// Pumps one frame of touch. Driven from <c>FrameworkDispatcher.Update()</c>, which is
		/// itself the LAST thing <c>Game.Update</c> does — after every <c>GameComponent</c>.
		///
		/// <para><b>Read this before injecting synthetic touch</b> (the eventual gamepad-to-touch
		/// mapper is the case in mind). Two things about the order below are not obvious and each
		/// one silently breaks a naive implementation:</para>
		///
		/// <para><b>1. State and gestures arrive by two different routes.</b>
		/// <see cref="SetFinger"/> fills the array <see cref="GetState"/> returns;
		/// <see cref="INTERNAL_onTouchEvent"/> feeds <see cref="GestureDetector"/> and nothing
		/// else. SDL pushes the latter live from <c>PollEvents</c> and services the former from
		/// inside <c>UpdateTouchPanelState</c>. So a synthetic source must write BOTH — one that
		/// only calls <c>SetFinger</c> produces no gesture, and one that only raises the event
		/// produces an empty <see cref="TouchCollection"/>. Games use both APIs.</para>
		///
		/// <para><b>2. A <c>GameComponent</c> can inject gestures but NOT state.</b> Components
		/// run before this method at any <c>UpdateOrder</c>, so their gesture events land ahead
		/// of <c>GestureDetector.OnUpdate()</c> and work — but <c>UpdateTouchPanelState</c> then
		/// rewrites every unreserved finger slot (see the clear loop in
		/// <c>SDL2_FNAPlatform.UpdateTouchPanelState</c>) and wipes their state writes in the
		/// same tick. <b>The tilt-emulator precedent therefore does not transfer here.</b> The
		/// shape that works is a decorator over <c>WPR.Xna.Rhi.IInputBackend</c>, registered
		/// where <c>FnaGameHost</c> calls <c>XnaBackend.SetInput</c>: it forwards the inner
		/// drain first and writes afterwards, which makes it the last writer every frame with no
		/// ordering hazard. Reserve slots the way <c>mouseSlot</c> already is, and take finger
		/// ids from the top of the range — real SDL fingers are small positive ints and the
		/// mouse-as-touch slot is <c>int.MaxValue</c>.</para>
		///
		/// <para>Two further constraints, neither of which needs new API.
		/// <see cref="GestureDetector"/> tracks a single active finger and a single second
		/// finger, so a real finger landing during a synthetic drag becomes the second one and
		/// starts a Pinch — synthetic and real touch must be mutually exclusive per tick, which
		/// <see cref="GetState"/> already answers. And one id per binding, allocated up front
		/// rather than per press: changing the id mid-gesture aborts the drag.</para>
		///
		/// <para>Finally, <c>FrameworkDispatcher</c> only calls this while
		/// <see cref="TouchDeviceExists"/> is true. On Android with a pad and no screen touch it
		/// is false and nothing here runs at all, so a synthetic source has to set that flag
		/// when it activates — exactly as the <see cref="MouseAsTouch"/> setter does.</para>
		/// </summary>
		internal static void Update()
		{
			// Update Gesture Detector for time-sensitive gestures
			GestureDetector.OnUpdate();

			// Remember the last frame's touches
			touches.CopyTo(prevTouches, 0);

			// Get the latest finger data
			XnaBackend.Input.UpdateTouchPanelState();
		}

		#endregion
	}
}
