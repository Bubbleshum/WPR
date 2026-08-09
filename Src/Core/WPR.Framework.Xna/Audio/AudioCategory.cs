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
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.audiocategory.aspx
	public struct AudioCategory : IEquatable<AudioCategory>
	{
		#region Public Properties

		private string INTERNAL_name;
		public string Name
		{
			get
			{
				return INTERNAL_name;
			}
		}

		#endregion

		#region Private Variables

		private AudioEngine parent;
		private int index;

		#endregion

		#region Internal Constructor

		internal AudioCategory(
			AudioEngine engine,
			int category,
			string name
		) {
			parent = engine;
			index = category;
			INTERNAL_name = name;
		}

		#endregion

		#region Public Methods

		public void Pause()
		{
			lock (parent.gcSync)
			{
				if (parent.IsDisposed)
				{
					return;
				}
				XnaBackend.Xact.PauseCategory(parent.handle, index, true);
			}
		}

		public void Resume()
		{
			lock (parent.gcSync)
			{
				if (parent.IsDisposed)
				{
					return;
				}
				XnaBackend.Xact.PauseCategory(parent.handle, index, false);
			}
		}

		public void SetVolume(float volume)
		{
			lock (parent.gcSync)
			{
				if (parent.IsDisposed)
				{
					return;
				}
				XnaBackend.Xact.SetCategoryVolume(parent.handle, index, volume);
			}
		}

		public void Stop(AudioStopOptions options)
		{
			lock (parent.gcSync)
			{
				if (parent.IsDisposed)
				{
					return;
				}
				XnaBackend.Xact.StopCategory(
				parent.handle,
				index,
				(options == AudioStopOptions.Immediate) ? XactStopOptions.Immediate : XactStopOptions.Release
			);
			}
		}

		public override int GetHashCode()
		{
			return Name.GetHashCode();
		}

		public bool Equals(AudioCategory other)
		{
			return (GetHashCode() == other.GetHashCode());
		}

		public override bool Equals(Object obj)
		{
			if (obj is AudioCategory)
			{
				return Equals((AudioCategory) obj);
			}
			return false;
		}

		public static bool operator ==(
			AudioCategory value1,
			AudioCategory value2
		) {
			return value1.Equals(value2);
		}

		public static bool operator !=(
			AudioCategory value1,
			AudioCategory value2
		) {
			return !(value1.Equals(value2));
		}

		#endregion
	}
}
