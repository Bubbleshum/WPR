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
#endregion

namespace Microsoft.Xna.Framework.Audio
{
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.audioemitter.aspx
	public class AudioEmitter
	{
		/* WPR 5c-3a: this used to hold FAudio's F3DAUDIO_EMITTER and each property poked that native
		 * struct directly, negating Z to convert XNA's right-handed space to the native left-handed
		 * one. The struct now lives behind the audio seam, so values are kept here as plain XNA-space
		 * data and the BACKEND applies the handedness flip plus the fixed "unused variable" defaults
		 * XNA implies (single channel, unit channel radius, pinned stereo azimuths, no cones or
		 * custom curves) — see FnaAudioBackend.Calculate3D. Same public API, no native dependency.
		 */

		#region Public Properties

		private float INTERNAL_dopplerScale = 1.0f;
		public float DopplerScale
		{
			get
			{
				return INTERNAL_dopplerScale;
			}
			set
			{
				if (value < 0.0f)
				{
					throw new ArgumentOutOfRangeException(
						"AudioEmitter.DopplerScale must be greater than or equal to 0.0f"
					);
				}
				INTERNAL_dopplerScale = value;
			}
		}

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

		public AudioEmitter()
		{
			DopplerScale = 1.0f;
			Forward = Vector3.Forward;
			Position = Vector3.Zero;
			Up = Vector3.Up;
			Velocity = Vector3.Zero;
		}

		#endregion
	}
}
