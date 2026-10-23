using System;
using Microsoft.Xna.Framework;

namespace WPR.Xna.Rhi
{
	/// <summary>Which way the host window shows a game's portrait backbuffer.</summary>
	public enum BackBufferTurn
	{
		/// <summary>As it is: portrait backbuffer, portrait window.</summary>
		None,

		/// <summary>
		/// A quarter turn anticlockwise: the backbuffer's right edge is the top of a landscape
		/// window, so portrait (px, py) is shown at window (py, width - px). For a picture drawn
		/// with its top along the right edge of the portrait screen.
		/// </summary>
		Anticlockwise,

		/// <summary>
		/// A quarter turn clockwise: the backbuffer's left edge is the top of a landscape window,
		/// so portrait (px, py) is shown at window (height - py, px).
		/// </summary>
		Clockwise,
	}

	/// <summary>
	/// Shows a portrait backbuffer turned a quarter on a landscape host window, while the game is
	/// drawing a landscape picture into it and expecting the player to turn the phone.
	///
	/// <para>WP7 had no other way to do landscape before XNA 4.0 grew <c>SupportedOrientations</c>,
	/// and J2ME ports kept doing it after. EA's <c>midp</c> layer (Need for Speed Undercover,
	/// The Sims 3, The Sims Medieval) asks for a portrait backbuffer and draws every sprite batch
	/// through <c>Matrix.CreateRotationZ(PI/2) * Matrix.CreateTranslation(width, 0, 0)</c>, so the
	/// picture's top is the right edge of the screen. On a phone the player turns the device. On
	/// a desktop nobody can turn the monitor, so the window does it instead: the game keeps the
	/// exact backbuffer it asked for (a render target the device binds in place of the real one,
	/// see <see cref="Microsoft.Xna.Framework.Graphics.GraphicsDevice"/>), and Present draws that
	/// target turned onto a real landscape backbuffer.</para>
	///
	/// <para><b>Detected, not listed.</b> Such a game asks for portrait and reads no orientation,
	/// so nothing it says gives it away; what it draws does. Every frame, the device notes whether
	/// a sprite batch began on the backbuffer with a matrix that is exactly a quarter turn mapping
	/// the landscape picture onto the portrait backbuffer (<see cref="QuarterTurnOf"/>), and
	/// WPR's own movie player says so when it turns a landscape movie onto a portrait backbuffer.
	/// <see cref="TurnOnFrames"/> such frames in a row turn the window; <see cref="TurnOffFrames"/>
	/// frames in a row whose backbuffer batches are all upright turn it back. Frames with no
	/// backbuffer sprite batch (a 3D scene alone, a loading pause) count for neither.</para>
	///
	/// <para>It applies only on a platform that cannot rotate
	/// (<see cref="IPlatformBackend.SupportsOrientationChanges"/> false, the desktop) and only
	/// while the backbuffer is portrait. A phone keeps the WP7 behaviour: the activity stays
	/// portrait and the player turns the device.</para>
	/// </summary>
	public static class PresentationTurn
	{
		/// <summary>Turned frames in a row before the window turns.</summary>
		public const int TurnOnFrames = 10;

		/// <summary>Upright frames in a row before a turned window turns back.</summary>
		public const int TurnOffFrames = 180;

		/// <summary>
		/// The turn the game's drawing asks for, from detection. The device applies it at the next
		/// frame boundary. Reset per launch.
		/// </summary>
		public static BackBufferTurn Requested { get; private set; }

		/// <summary>
		/// The turn in effect for the device's current backbuffer, set by the
		/// <see cref="Microsoft.Xna.Framework.Graphics.GraphicsDevice"/> whenever its backbuffer is
		/// created, reset or retargeted. Input mapping reads this one.
		/// </summary>
		public static BackBufferTurn Active { get; internal set; }

		private static BackBufferTurn _frameTurned;
		private static bool _frameUpright;
		private static BackBufferTurn _candidate;
		private static int _turnedRun;
		private static int _uprightRun;
		private static bool _windowChangePending;

		/// <summary>True when this platform never turns anything (a phone, or no platform yet).</summary>
		private static bool PlatformRotates =>
			!XnaBackend.HasPlatform || XnaBackend.Platform.SupportsOrientationChanges();

		/// <summary>The turn a backbuffer of this size gets on this platform.</summary>
		public static BackBufferTurn For(int backBufferWidth, int backBufferHeight) =>
			For(Requested, backBufferWidth, backBufferHeight, PlatformRotates);

		/// <summary>
		/// The rule itself: a requested turn applies to a portrait backbuffer on a platform that
		/// cannot rotate.
		/// </summary>
		public static BackBufferTurn For(BackBufferTurn requested, int backBufferWidth, int backBufferHeight, bool platformRotates)
		{
			if (requested == BackBufferTurn.None || backBufferHeight <= backBufferWidth || platformRotates)
			{
				return BackBufferTurn.None;
			}
			return requested;
		}

		/// <summary>
		/// Which turn would show a sprite batch drawn through <paramref name="m"/> onto a
		/// <paramref name="width"/> x <paramref name="height"/> portrait backbuffer upright: the
		/// matrix must be an exact unit quarter turn that carries the landscape rectangle
		/// (height x width) onto the backbuffer. Anything else, including a quarter turn about some
		/// other point or at another scale, is <see cref="BackBufferTurn.None"/>.
		/// </summary>
		public static BackBufferTurn QuarterTurnOf(in Matrix m, int width, int height)
		{
			const float e = 1e-3f;
			if (height <= width ||
				Math.Abs(m.M11) > e || Math.Abs(m.M22) > e ||
				Math.Abs(m.M13) > e || Math.Abs(m.M23) > e || Math.Abs(m.M31) > e || Math.Abs(m.M32) > e ||
				Math.Abs(m.M33 - 1f) > e || Math.Abs(m.M44 - 1f) > e ||
				Math.Abs(m.M14) > e || Math.Abs(m.M24) > e || Math.Abs(m.M34) > e || Math.Abs(m.M43) > e)
			{
				return BackBufferTurn.None;
			}

			// Row vectors: x' = x*M11 + y*M21 + M41, y' = x*M12 + y*M22 + M42.
			// CreateRotationZ(+PI/2) * Translation(width, 0): x' = width - y, y' = x. The
			// picture's top (y = 0) is the right edge, so the window turns anticlockwise.
			if (Math.Abs(m.M12 - 1f) < e && Math.Abs(m.M21 + 1f) < e &&
				Math.Abs(m.M41 - width) < 1f && Math.Abs(m.M42) < 1f)
			{
				return BackBufferTurn.Anticlockwise;
			}
			// CreateRotationZ(-PI/2) * Translation(0, height): x' = y, y' = height - x.
			if (Math.Abs(m.M12 + 1f) < e && Math.Abs(m.M21 - 1f) < e &&
				Math.Abs(m.M41) < 1f && Math.Abs(m.M42 - height) < 1f)
			{
				return BackBufferTurn.Clockwise;
			}
			return BackBufferTurn.None;
		}

		/// <summary>
		/// A sprite batch began on the game's portrait backbuffer. Called by the device; the matrix
		/// decides whether this frame drew a turned picture or an upright one.
		/// </summary>
		internal static void NoteBackBufferBatch(in Matrix transform, int width, int height)
		{
			BackBufferTurn turn = QuarterTurnOf(transform, width, height);
			if (turn != BackBufferTurn.None)
			{
				_frameTurned = turn;
			}
			else
			{
				_frameUpright = true;
			}
		}

		/// <summary>
		/// The frame drew a landscape picture turned onto a portrait backbuffer by other means
		/// (WPR's movie player turning a landscape movie, as the phone's player did).
		/// </summary>
		public static void NoteTurnedFrame(BackBufferTurn turn)
		{
			if (turn != BackBufferTurn.None)
			{
				_frameTurned = turn;
			}
		}

		/// <summary>
		/// Called by the device after each present. True when <see cref="Requested"/> changed and
		/// the device has to retarget its backbuffer.
		/// </summary>
		internal static bool EndFrame()
		{
			BackBufferTurn turned = _frameTurned;
			bool upright = _frameUpright;
			_frameTurned = BackBufferTurn.None;
			_frameUpright = false;
			return Observe(turned, upright, PlatformRotates);
		}

		/// <summary>The decision rule over one frame's evidence. Separate so it can be tested.</summary>
		public static bool Observe(BackBufferTurn turned, bool upright, bool platformRotates)
		{
			if (platformRotates)
			{
				return false;
			}

			if (turned != BackBufferTurn.None)
			{
				_uprightRun = 0;
				_turnedRun = turned == _candidate ? _turnedRun + 1 : 1;
				_candidate = turned;
				if (_turnedRun >= TurnOnFrames && Requested != turned)
				{
					Requested = turned;
					return true;
				}
				return false;
			}

			if (upright)
			{
				_turnedRun = 0;
				_uprightRun++;
				if (_uprightRun >= TurnOffFrames && Requested != BackBufferTurn.None)
				{
					Requested = BackBufferTurn.None;
					return true;
				}
			}
			return false;
		}

		/// <summary>Set by the device after it retargets, for the platform to resize the window.</summary>
		internal static void RequestWindowChange() => _windowChangePending = true;

		/// <summary>True once after the device retargeted: the platform resizes the window then.</summary>
		public static bool TakeWindowChange()
		{
			bool pending = _windowChangePending;
			_windowChangePending = false;
			return pending;
		}

		/// <summary>
		/// Maps a position normalised to the presented window area onto one normalised to the
		/// backbuffer, through <see cref="Active"/>.
		/// </summary>
		public static void WindowToBackBuffer(ref float u, ref float v) => WindowToBackBuffer(Active, ref u, ref v);

		/// <summary>Maps a position normalised to the window onto one normalised to the backbuffer.</summary>
		public static void WindowToBackBuffer(BackBufferTurn turn, ref float u, ref float v)
		{
			float wu = u, wv = v;
			switch (turn)
			{
				case BackBufferTurn.Anticlockwise:
					u = 1f - wv;
					v = wu;
					break;
				case BackBufferTurn.Clockwise:
					u = wv;
					v = 1f - wu;
					break;
			}
		}

		/// <summary>The same mapping for a movement, which turns but does not move.</summary>
		public static void WindowDeltaToBackBuffer(ref float du, ref float dv)
		{
			float wu = du, wv = dv;
			switch (Active)
			{
				case BackBufferTurn.Anticlockwise:
					du = -wv;
					dv = wu;
					break;
				case BackBufferTurn.Clockwise:
					du = wv;
					dv = -wu;
					break;
			}
		}

		/// <summary>The inverse of <see cref="WindowToBackBuffer(BackBufferTurn, ref float, ref float)"/>.</summary>
		public static void BackBufferToWindow(BackBufferTurn turn, ref float u, ref float v)
		{
			float pu = u, pv = v;
			switch (turn)
			{
				case BackBufferTurn.Anticlockwise:
					u = pv;
					v = 1f - pu;
					break;
				case BackBufferTurn.Clockwise:
					u = 1f - pv;
					v = pu;
					break;
			}
		}

		/// <summary>The host window's size for a backbuffer of this size: swapped when turned.</summary>
		public static void WindowSize(BackBufferTurn turn, ref int width, ref int height)
		{
			if (turn != BackBufferTurn.None)
			{
				(width, height) = (height, width);
			}
		}

		/// <summary>Called at launch and teardown, so every title starts unturned.</summary>
		public static void Reset()
		{
			Requested = BackBufferTurn.None;
			Active = BackBufferTurn.None;
			_frameTurned = BackBufferTurn.None;
			_frameUpright = false;
			_candidate = BackBufferTurn.None;
			_turnedRun = 0;
			_uprightRun = 0;
			_windowChangePending = false;
		}
	}
}
