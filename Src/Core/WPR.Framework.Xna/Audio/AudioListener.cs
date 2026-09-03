#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

namespace Microsoft.Xna.Framework.Audio
{
	// http://msdn.microsoft.com/en-us/library/dd940199.aspx
	public class AudioListener
	{
		/* WPR 5c-3a: this used to hold FAudio's F3DAUDIO_LISTENER and each property poked that
		 * native struct directly, negating Z on the way in/out to convert between XNA's
		 * right-handed space and the native left-handed one. The struct now lives behind the audio
		 * seam, so the values are kept here as plain XNA-space vectors and the BACKEND applies the
		 * handedness flip when it builds its native structs (see FAudioSoundBackend.ToF3D). Same
		 * public API, no native dependency.
		 */

		#region Public Properties

		public Vector3 Forward
		{
			get;
			set;
		}

		public Vector3 Position
		{
			get;
			set;
		}

		public Vector3 Up
		{
			get;
			set;
		}

		public Vector3 Velocity
		{
			get;
			set;
		}

		#endregion

		#region Public Constructor

		public AudioListener()
		{
			Forward = Vector3.Forward;
			Position = Vector3.Zero;
			Up = Vector3.Up;
			Velocity = Vector3.Zero;
		}

		#endregion
	}
}
