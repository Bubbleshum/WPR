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
using Microsoft.Xna.Framework.Input.Touch;
using WPR.Xna.Rhi;
#endregion

namespace Microsoft.Xna.Framework.Input
{
	/// <summary>
	/// Allows reading position and button click information from mouse.
	/// </summary>
	public static class Mouse
	{
		#region Public Properties

		public static IntPtr WindowHandle
		{
			get;
			set;
		}

		public static bool IsRelativeMouseModeEXT
		{
			get
			{
				return XnaBackend.Input.GetRelativeMouseMode();
			}
			set
			{
				XnaBackend.Input.SetRelativeMouseMode(value);
			}
		}

		#endregion

		#region Internal Variables

		/* Seed values only — the platform overwrites all four on the first window-resize event and on
		 * every device reset (XnaBackend.NotifyBackBufferSize). These were
		 * GraphicsDeviceManager.DefaultBackBufferWidth/Height; that manager is spine and stays in FNA,
		 * so the constants are inlined here exactly as 5c-1 did in PresentationParameters. */
		internal static int INTERNAL_WindowWidth = 800;
		internal static int INTERNAL_WindowHeight = 480;
		internal static int INTERNAL_BackBufferWidth = 800;
		internal static int INTERNAL_BackBufferHeight = 480;

		/* WPR: INTERNAL_WindowWidth/Height are the area of the window the backbuffer is presented
		 * into, and these are its top-left corner in window coordinates. Both are zero except for
		 * a desktop game running fullscreen, where the backbuffer is letterboxed to its own aspect
		 * ratio (see WPR.Xna.Rhi.PresentationLetterbox). Every window-to-game conversion subtracts
		 * them first, so a click on a side bar lands off the game's surface, not inside it. */
		internal static int INTERNAL_WindowOffsetX = 0;
		internal static int INTERNAL_WindowOffsetY = 0;

		internal static int INTERNAL_MouseWheel = 0;

		#endregion

		/* WPR: where the primary touch last put the mouse, and the hardware position at that time. */
		private static bool reportedFromTouch;
		private static int touchMouseX, touchMouseY, hardwareX, hardwareY;

		/// <summary>
		/// The primary touch as the mouse, in backbuffer coordinates. WP7's <c>Mouse.GetState()</c>
		/// reported the first finger: LeftButton Pressed while it was down, at its position, and
		/// the position kept after it lifted. Titles read their input through it alone (NFS
		/// Undercover's J2ME layer, SPY Mouse); on Android SDL synthesises no mouse from touch
		/// (SDL_HINT_TOUCH_MOUSE_EVENTS is off, see SDL2_FNAPlatform), so they took no touch at
		/// all there. A real mouse button wins, and the mouse-as-touch slot is not a touch here.
		/// The last touch position is kept until the hardware pointer moves.
		/// </summary>
		private static bool FromPrimaryTouch(int rawX, int rawY, ref ButtonState left, out int x, out int y)
		{
			if (TouchPanel.TryGetPrimaryTouch(out Vector2 touch, out bool down))
			{
				x = (int) ((double) touch.X * INTERNAL_BackBufferWidth / Math.Max(1, TouchPanel.DisplayWidth));
				y = (int) ((double) touch.Y * INTERNAL_BackBufferHeight / Math.Max(1, TouchPanel.DisplayHeight));
				if (down)
				{
					left = ButtonState.Pressed;
				}
				reportedFromTouch = true;
				touchMouseX = x;
				touchMouseY = y;
				hardwareX = rawX;
				hardwareY = rawY;
				return true;
			}

			if (reportedFromTouch && rawX == hardwareX && rawY == hardwareY)
			{
				x = touchMouseX;
				y = touchMouseY;
				return true;
			}

			reportedFromTouch = false;
			x = y = 0;
			return false;
		}

		#region Public Events

		public static Action<int> ClickedEXT;

		#endregion

		#region Public Interface

		/// <summary>
		/// Gets mouse state information that includes position and button
		/// presses for the provided window
		/// </summary>
		/// <returns>Current state of the mouse.</returns>
		public static MouseState GetState()
		{
			int x, y;
			ButtonState left, middle, right, x1, x2;

			XnaBackend.Input.GetMouseState(
				WindowHandle,
				out x,
				out y,
				out left,
				out middle,
				out right,
				out x1,
				out x2
			);

			// WPR: on the phone the mouse was the primary touch (see the remarks below).
			if (left == ButtonState.Released && FromPrimaryTouch(x, y, ref left, out int tx, out int ty))
			{
				return new MouseState(tx, ty, INTERNAL_MouseWheel, left, middle, right, x1, x2);
			}

			// Scale the mouse coordinates for the faux-backbuffer
			// WPR: and turn them with a backbuffer shown turned (PresentationTurn).
			if (PresentationTurn.Active == BackBufferTurn.None)
			{
				x = (int) ((double) (x - INTERNAL_WindowOffsetX) * INTERNAL_BackBufferWidth / INTERNAL_WindowWidth);
				y = (int) ((double) (y - INTERNAL_WindowOffsetY) * INTERNAL_BackBufferHeight / INTERNAL_WindowHeight);
			}
			else
			{
				float u = (float) (x - INTERNAL_WindowOffsetX) / INTERNAL_WindowWidth;
				float v = (float) (y - INTERNAL_WindowOffsetY) / INTERNAL_WindowHeight;
				PresentationTurn.WindowToBackBuffer(ref u, ref v);
				x = (int) ((double) u * INTERNAL_BackBufferWidth);
				y = (int) ((double) v * INTERNAL_BackBufferHeight);
			}

			return new MouseState(
				x,
				y,
				INTERNAL_MouseWheel,
				left,
				middle,
				right,
				x1,
				x2
			);
		}

		/// <summary>
		/// Sets mouse cursor's relative position to game-window.
		/// </summary>
		/// <param name="x">Relative horizontal position of the cursor.</param>
		/// <param name="y">Relative vertical position of the cursor.</param>
		public static void SetPosition(int x, int y)
		{
			// In relative mode, this function is meaningless
			if (IsRelativeMouseModeEXT)
			{
				return;
			}

			// Scale the mouse coordinates for the faux-backbuffer
			// WPR: and turn them with a backbuffer shown turned (PresentationTurn).
			if (PresentationTurn.Active == BackBufferTurn.None)
			{
				x = (int) ((double) x * INTERNAL_WindowWidth / INTERNAL_BackBufferWidth) + INTERNAL_WindowOffsetX;
				y = (int) ((double) y * INTERNAL_WindowHeight / INTERNAL_BackBufferHeight) + INTERNAL_WindowOffsetY;
			}
			else
			{
				float u = (float) x / INTERNAL_BackBufferWidth;
				float v = (float) y / INTERNAL_BackBufferHeight;
				PresentationTurn.BackBufferToWindow(PresentationTurn.Active, ref u, ref v);
				x = (int) ((double) u * INTERNAL_WindowWidth) + INTERNAL_WindowOffsetX;
				y = (int) ((double) v * INTERNAL_WindowHeight) + INTERNAL_WindowOffsetY;
			}

			XnaBackend.Input.SetMousePosition(WindowHandle, x, y);
		}

		#endregion

		#region Internal Methods

		internal static void INTERNAL_onClicked(int button)
		{
			if (ClickedEXT != null)
			{
				ClickedEXT(button);
			}
		}

		#endregion
	}
}
