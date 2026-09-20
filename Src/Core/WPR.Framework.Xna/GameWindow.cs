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
using System.ComponentModel;

using Microsoft.Xna.Framework.Input;
#endregion

namespace Microsoft.Xna.Framework
{
	public abstract class GameWindow
	{
		#region Public Properties

		[DefaultValue(false)]
		public abstract bool AllowUserResizing
		{ 
			get;
			set;
		}

		/// <summary>
		/// The WP7 screen, always in the device's NATIVE PORTRAIT orientation -- 480x800 --
		/// regardless of which way the game is presenting.
		/// </summary>
		/// <remarks>
		/// <para>This deliberately does NOT report the host OS window. On a phone the window
		/// <i>is</i> the screen, and WP7's screen is a fixed 480x800 WVGA panel that never
		/// rotates: a landscape title gets an 800x480 <b>backbuffer</b> and 800x480
		/// <b>touch</b> coordinates while <c>Window.ClientBounds</c> keeps saying 480x800.
		/// That asymmetry looks like a bug and is not -- it is what WP7 did, and titles were
		/// written against it.</para>
		///
		/// <para>Gravity Guy is the reference case, and it is load-bearing rather than
		/// cosmetic. Its Cocos2D port (<c>Libs.dll</c>, <c>XNAGlue.UpdateMouseTouch</c>)
		/// draws into a 480x800 portrait logical space and rotates every incoming touch into
		/// it with <c>mx' = Window.ClientBounds.Width - my; my' = mx</c>. With the true WP7
		/// value that maps the whole 800x480 landscape touch rect exactly onto the game's
		/// 480x800 space -- bijectively, no clipping. Returning the desktop SDL window
		/// (1200x720) instead put <c>mx'</c> in (720,1200] against a 480-wide space, so
		/// <b>every</b> touch landed off-screen and the entire menu was dead while the game
		/// rendered, animated and logged perfectly. Nothing throws; the only symptom is that
		/// taps do nothing.</para>
		///
		/// <para>The byte-identical expression ships in Miniclip's <c>LibsC.dll</c> -- verified
		/// by decompiling Fragger, iStunt 2 and Monster Island -- so this is an engine's worth
		/// of titles rather than one game. Across the 307-XAP library exactly 14 titles read
		/// this property at all, so the blast radius of getting it wrong in the other
		/// direction is small and known.</para>
		///
		/// <para><b>Anything that genuinely means the OS window wants
		/// <see cref="HostClientBounds"/>.</b> Backbuffer resizing is the case that matters:
		/// feeding it this value would resize a landscape game's backbuffer to portrait.</para>
		/// </remarks>
		public Rectangle ClientBounds
		{
			get
			{
				return new Rectangle(0, 0, PhoneScreenShortDim, PhoneScreenLongDim);
			}
		}

		/// <summary>
		/// WP7's fixed WVGA panel, in its native portrait orientation. Every WP7 and WP7.5
		/// device shipped this exact panel -- higher resolutions arrived with WP8 -- so this
		/// is hardware, not a preference, and a game asking for a smaller backbuffer still
		/// saw the whole screen here.
		/// </summary>
		internal const int PhoneScreenShortDim = 480;
		internal const int PhoneScreenLongDim = 800;

		/// <summary>
		/// The host window's real client rectangle, in host pixels. WPR-internal: this is the
		/// desktop SDL window or the Android surface, which on neither head is the 480x800
		/// screen <see cref="ClientBounds"/> promises a game.
		/// </summary>
		internal abstract Rectangle HostClientBounds
		{
			get;
		}

		public abstract DisplayOrientation CurrentOrientation
		{
			get;
			internal set;
		}

		public abstract IntPtr Handle
		{
			get;
		}

		public abstract string ScreenDeviceName
		{
			get;
		}

		public string Title
		{
			get
			{
				return _title;
			}
			set
			{
				if (_title != value)
				{
					SetTitle(value);
					_title = value;
				}
			}
		}

		/// <summary>
		/// Determines whether the border of the window is visible.
		/// </summary>
		/// <exception cref="System.NotImplementedException">
		/// Thrown when trying to use this property on an unsupported platform.
		/// </exception>
		public virtual bool IsBorderlessEXT
		{
			get
			{
				return false;
			}
			set
			{
				throw new NotImplementedException();
			}
		}

		#endregion

		#region Private Variables

		private string _title;

		#endregion

		#region Protected Constructors

		protected GameWindow()
		{
		}

		#endregion

		#region Events

		public event EventHandler<EventArgs> ClientSizeChanged;
		public event EventHandler<EventArgs> OrientationChanged;
		public event EventHandler<EventArgs> ScreenDeviceNameChanged;

		#endregion

		#region Public Methods

		public abstract void BeginScreenDeviceChange(bool willBeFullScreen);

		public abstract void EndScreenDeviceChange(
			string screenDeviceName,
			int clientWidth,
			int clientHeight
		);

		public void EndScreenDeviceChange(string screenDeviceName)
		{
			EndScreenDeviceChange(
				screenDeviceName,
				// HostClientBounds: this overload means "keep the size I already have", and
				// the size actually in effect is the host window's, not WP7's fixed screen.
				HostClientBounds.Width,
				HostClientBounds.Height
			);
		}

		#endregion

		#region Protected Methods

		protected void OnActivated()
		{
		}

		protected void OnClientSizeChanged()
		{
			if (ClientSizeChanged != null)
			{
				ClientSizeChanged(this, EventArgs.Empty);
			}
		}

		protected void OnDeactivated()
		{
		}

		protected void OnOrientationChanged()
		{
			if (OrientationChanged != null)
			{
				OrientationChanged(this, EventArgs.Empty);
			}
		}

		protected void OnPaint()
		{
		}

		protected void OnScreenDeviceNameChanged()
		{
			if (ScreenDeviceNameChanged != null)
			{
				ScreenDeviceNameChanged(this, EventArgs.Empty);
			}
		}

		protected internal abstract void SetSupportedOrientations(
			DisplayOrientation orientations
		);

		protected abstract void SetTitle(string title);

		#endregion
	}
}
