#nullable enable
using System;
using Microsoft.Xna.Framework;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Turns mouse-wheel notches into a synthesised vertical finger drag, so a desktop player can
	/// scroll a WP7 list the way they expect to.
	///
	/// <para><b>Why the wheel needed its own path at all.</b> Before this, a wheel notch reached a
	/// game only as a <c>Pinch</c> gesture, and only when the title had enabled Pinch. A title that
	/// scrolls by reading raw finger positions out of <c>TouchPanel.GetState()</c> — which is the
	/// common shape, because WP7 lists were written before anyone trusted the gesture recogniser —
	/// saw nothing at all, and the wheel was simply dead on every such screen.</para>
	///
	/// <para><b>It exists to stop a scroll being read as a tap, which is the failure it was built
	/// for.</b> Chickens Can't Fly is the reference case: its own recogniser
	/// (<c>Evozon.Games.Common</c>) calls anything under <b>40 px</b> of travel a tap and starts a
	/// drag at <b>15 px</b>, so a short mouse drag over the lab list lands in the overlap and opens
	/// whichever laboratory happened to be under the cursor instead of scrolling — "when I am
	/// scrolling, if in the middle, it jumps the menu". A finger never hits that window because a
	/// flick travels hundreds of pixels; a mouse hand does, every time. Hence
	/// <see cref="DisplayPixelsPerNotch"/> is deliberately well above any plausible tap tolerance:
	/// a wheel scroll must be unmistakably a drag.</para>
	///
	/// <para><b>Consecutive notches coalesce into ONE drag</b> rather than a press/release per
	/// notch. Separate touches would each be short enough to look like a tap again — the very bug
	/// this fixes — and would also hand the game a burst of flicks with their own inertia. The
	/// finger lifts once the pending distance is spent and <see cref="IdleFramesBeforeLift"/> have
	/// passed with nothing new arriving.</para>
	///
	/// <para>Static because the producer (the SDL event loop, which owns the wheel event) and the
	/// consumer (the backend's touch injector) are in different assemblies and neither can hold the
	/// other. That is the same reason <see cref="SyntheticTouchSample"/> lives here rather than in a
	/// backend.</para>
	/// </summary>
	public static class WheelTouchScroll
	{
		/// <summary>
		/// How far the virtual finger travels per wheel notch, in display space.
		///
		/// <para>Chosen to clear a game's own tap tolerance by a wide margin — see the note on the
		/// type. 80 px is also about a fifth of a 480x800 WP7 screen, which reads as one
		/// comfortable scroll step rather than a jump.</para>
		/// </summary>
		private const float DisplayPixelsPerNotch = 80f;

		/// <summary>
		/// Frames a notch's travel is spread over. A teleport in one frame gives a game's
		/// recogniser a single enormous delta, which flick detectors read as a violent throw;
		/// spreading it looks like a hand moving.
		/// </summary>
		private const int FramesPerNotch = 4;

		/// <summary>
		/// Frames of no new notches before the finger lifts. Long enough to bridge the gap between
		/// notches of one physical wheel roll, short enough that the lift still feels immediate.
		/// </summary>
		private const int IdleFramesBeforeLift = 6;

		private static readonly object Gate = new object();

		private static bool _active;
		private static bool _pressPending;
		private static Vector2 _position;
		private static float _remaining;      // display px still to travel, signed
		private static float _perFrameStep;   // signed, magnitude of one frame's movement
		private static int _idleFrames;

		/// <summary>
		/// Records a wheel roll at <paramref name="displayX"/>/<paramref name="displayY"/> (display
		/// space). <paramref name="notches"/> is SDL's wheel delta: positive is a roll away from the
		/// user, which scrolls the view up and therefore drags the finger DOWN the screen.
		/// </summary>
		public static void Notify(float displayX, float displayY, int notches)
		{
			if (notches == 0)
			{
				return;
			}

			lock (Gate)
			{
				if (!_active)
				{
					// A fresh gesture starts under the cursor. An in-flight one keeps its anchor:
					// moving it mid-drag would look like the finger jumped, and a game tracking one
					// finger would abandon the gesture.
					_position = new Vector2(displayX, displayY);
					_pressPending = true;
					_active = true;
					_remaining = 0f;
				}

				_remaining += notches * DisplayPixelsPerNotch;
				_perFrameStep = DisplayPixelsPerNotch / FramesPerNotch;
				_idleFrames = 0;
			}
		}

		/// <summary>
		/// One tick of the gesture, for the backend's touch injector. Returns
		/// <see cref="SyntheticTouchSample.Inactive"/> whenever no wheel scroll is in flight, which
		/// is almost always.
		/// </summary>
		public static SyntheticTouchSample Advance()
		{
			lock (Gate)
			{
				if (!_active)
				{
					return SyntheticTouchSample.Inactive;
				}

				bool justPressed = _pressPending;
				_pressPending = false;

				float step = 0f;
				if (Math.Abs(_remaining) > 0.01f)
				{
					step = Math.Sign(_remaining) * Math.Min(_perFrameStep, Math.Abs(_remaining));
					_remaining -= step;
					_idleFrames = 0;
				}
				else
				{
					_idleFrames += 1;
				}

				// The press frame reports the anchor with no movement, so the game's first sample is
				// where the cursor actually was. Moving on the same frame would make the press and
				// the first move indistinguishable and lose the gesture's origin.
				if (!justPressed)
				{
					_position = new Vector2(_position.X, _position.Y + step);
				}

				if (_idleFrames >= IdleFramesBeforeLift)
				{
					_active = false;
					// Released carries the final position; the injector turns Active=false into the
					// lift on the next tick, so this sample is the last Moved of the gesture.
					return new SyntheticTouchSample(true, _position, Vector2.Zero, justPressed, true);
				}

				return new SyntheticTouchSample(
					true,
					_position,
					new Vector2(0f, justPressed ? 0f : step),
					justPressed,
					false);
			}
		}

		/// <summary>
		/// Drops any gesture in flight. Called at teardown so a scroll left half-finished by a game
		/// exiting cannot be delivered into the next launch's first frames.
		/// </summary>
		public static void Reset()
		{
			lock (Gate)
			{
				_active = false;
				_pressPending = false;
				_remaining = 0f;
				_idleFrames = 0;
			}
		}
	}
}
