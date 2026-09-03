#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using WPR.Engine.Audio;
using System;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Audio
{
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.soundbank.aspx
	public class SoundBank : IDisposable
	{
		#region Public Properties

		public bool IsDisposed
		{
			get;
			private set;
		}

		public bool IsInUse
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetSoundBankState(handle);
				return (state & (uint) XactState.InUse) != 0;
			}
		}

		#endregion

		#region Internal Variables

		internal AudioEngine engine;
		/* WPR 5c-3b: the F3DAUDIO_DSP_SETTINGS block (and its unmanaged coefficient buffer) moved
		 * into the backend, which owns every FACT struct. Nothing to allocate or free here now. */

		#endregion

		#region Private Variables

		private IntPtr handle;
		private WeakReference selfReference;

		#endregion

		#region Disposing Event

		public event EventHandler<EventArgs> Disposing;

		#endregion

		#region Public Constructor

		public SoundBank(AudioEngine audioEngine, string filename)
		{
			if (audioEngine == null)
			{
				throw new ArgumentNullException("audioEngine");
			}
			if (String.IsNullOrEmpty(filename))
			{
				throw new ArgumentNullException("filename");
			}

			IntPtr buffer = AudioBackendRegistry.Xact.ReadFileToPointer(filename, out int bufferLength);

			handle = AudioBackendRegistry.Xact.CreateSoundBank(audioEngine.handle, buffer, bufferLength);

			AudioBackendRegistry.Xact.FreeFilePointer(buffer);

			engine = audioEngine;
			selfReference = new WeakReference(this, true);
			engine.RegisterPointer(handle, selfReference);
			IsDisposed = false;
		}

		#endregion

		#region Destructor

		~SoundBank()
		{
			if (AudioEngine.ProgramExiting)
			{
				return;
			}

			if (!IsDisposed && IsInUse)
			{
				// STOP LEAKING YOUR BANKS, ARGH
				GC.ReRegisterForFinalize(this);
				return;
			}
			Dispose(false);
		}

		#endregion

		#region Public Dispose Method

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion

		#region Protected Dispose Method

		protected void Dispose(bool disposing)
		{
			lock (engine.gcSync)
			{
				if (!IsDisposed)
				{
					if (Disposing != null)
					{
						Disposing.Invoke(this, null);
					}

					// If this is disposed, stop leaking memory!
					if (!engine.IsDisposed)
					{
						AudioBackendRegistry.Xact.DestroySoundBank(handle);
					}
					OnSoundBankDestroyed();
				}
			}
		}

		#endregion

		#region Public Methods

		public Cue GetCue(string name)
		{
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}

			int cue = AudioBackendRegistry.Xact.GetCueIndex(handle, name);

			if (cue < 0)
			{
				throw new InvalidOperationException(
					"Invalid cue name!"
				);
			}

			IntPtr result = AudioBackendRegistry.Xact.PrepareCue(handle, cue);
			return new Cue(result, name, this);
		}

		public void PlayCue(string name)
		{
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}

			int cue = AudioBackendRegistry.Xact.GetCueIndex(handle, name);

			if (cue < 0)
			{
				throw new InvalidOperationException(
					"Invalid cue name!"
				);
			}

			AudioBackendRegistry.Xact.PlayCueFireAndForget(handle, cue);
		}

		public void PlayCue(
			string name,
			AudioListener listener,
			AudioEmitter emitter
		) {
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}
			if (listener == null)
			{
				throw new ArgumentNullException("listener");
			}
			if (emitter == null)
			{
				throw new ArgumentNullException("emitter");
			}

			int cue = AudioBackendRegistry.Xact.GetCueIndex(handle, name);

			if (cue < 0)
			{
				throw new InvalidOperationException(
					"Invalid cue name!"
				);
			}

			AudioBackendRegistry.Xact.PlayCue3D(
				engine.handle,
				handle,
				cue,
				Build3DParams(listener, emitter, engine.channels)
			);
		}

		#endregion

		#region Internal Methods

		/// <summary>Builds the backend's 3D input from XNA's listener/emitter. XACT positions with a
		/// single source channel and max distance scaling, exactly as FNA did when it filled
		/// F3DAUDIO_DSP_SETTINGS/F3DAUDIO_EMITTER here.</summary>
		internal static Audio3DParams Build3DParams(
			AudioListener listener,
			AudioEmitter emitter,
			int destinationChannels
		) {
			return new Audio3DParams
			{
				ListenerForward = listener.Forward.ToNumerics(),
				ListenerUp = listener.Up.ToNumerics(),
				ListenerPosition = listener.Position.ToNumerics(),
				ListenerVelocity = listener.Velocity.ToNumerics(),
				EmitterForward = emitter.Forward.ToNumerics(),
				EmitterUp = emitter.Up.ToNumerics(),
				EmitterPosition = emitter.Position.ToNumerics(),
				EmitterVelocity = emitter.Velocity.ToNumerics(),
				EmitterDopplerScale = emitter.DopplerScale,
				CurveDistanceScaler = float.MaxValue,
				SourceChannels = 1,
				DestinationChannels = destinationChannels,
			};
		}

		internal void OnSoundBankDestroyed()
		{
			IsDisposed = true;
			handle = IntPtr.Zero;
			selfReference = null;
		}

		#endregion
	}
}
