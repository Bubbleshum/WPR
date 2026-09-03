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
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.cue.aspx
	public sealed class Cue : IDisposable
	{
		#region Public Properties

		public bool IsCreated
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetCueState(handle);
				return (state & (uint) XactState.Created) != 0;
			}
		}

		public bool IsDisposed
		{
			get;
			private set;
		}

		public bool IsPaused
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetCueState(handle);
				return (state & (uint) XactState.Paused) != 0;
			}
		}

		public bool IsPlaying
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetCueState(handle);
				return (state & (uint) XactState.Playing) != 0;
			}
		}

		public bool IsPrepared
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetCueState(handle);
				return (state & (uint) XactState.Prepared) != 0;
			}
		}

		public bool IsPreparing
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetCueState(handle);
				return (state & (uint) XactState.Preparing) != 0;
			}
		}

		public bool IsStopped
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetCueState(handle);
				return (state & (uint) XactState.Stopped) != 0;
			}
		}

		public bool IsStopping
		{
			get
			{
				uint state;
				state = (uint) AudioBackendRegistry.Xact.GetCueState(handle);
				return (state & (uint) XactState.Stopping) != 0;
			}
		}

		public string Name
		{
			get;
			private set;
		}

		#endregion

		#region Private Variables

		private IntPtr handle;
		private SoundBank bank;
		private WeakReference selfReference;

		#endregion

		#region Disposing Event

		public event EventHandler<EventArgs> Disposing;

		#endregion

		#region Internal Constructor

		internal Cue(IntPtr cue, string name, SoundBank soundBank)
		{
			handle = cue;
			Name = name;
			bank = soundBank;

			selfReference = new WeakReference(this, true);
			bank.engine.RegisterPointer(handle, selfReference);
		}

		#endregion

		#region Destructor

		~Cue()
		{
			if (AudioEngine.ProgramExiting)
			{
				return;
			}

			if (!IsDisposed && IsPlaying)
			{
				// STOP LEAKING YOUR CUES, ARGH
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

		#region Public Methods

		public void Apply3D(AudioListener listener, AudioEmitter emitter)
		{
			if (listener == null)
			{
				throw new ArgumentNullException("listener");
			}
			if (emitter == null)
			{
				throw new ArgumentNullException("emitter");
			}

			AudioBackendRegistry.Xact.Apply3DToCue(
				bank.engine.handle,
				handle,
				SoundBank.Build3DParams(listener, emitter, bank.engine.channels)
			);
		}

		public float GetVariable(string name)
		{
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}

			int variable = AudioBackendRegistry.Xact.GetCueVariableIndex(handle, name);

			if (variable < 0)
			{
				throw new InvalidOperationException(
					"Invalid variable name!"
				);
			}

			return AudioBackendRegistry.Xact.GetCueVariable(handle, variable);
		}

		public void Pause()
		{
			AudioBackendRegistry.Xact.PauseCue(handle, true);
		}

		public void Play()
		{
			AudioBackendRegistry.Xact.PlayPreparedCue(handle);
		}

		public void Resume()
		{
			AudioBackendRegistry.Xact.PauseCue(handle, false);
		}

		public void SetVariable(string name, float value)
		{
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}

			int variable = AudioBackendRegistry.Xact.GetCueVariableIndex(handle, name);

			if (variable < 0)
			{
				throw new InvalidOperationException(
					"Invalid variable name!"
				);
			}

			AudioBackendRegistry.Xact.SetCueVariable(handle, variable, value);
		}

		public void Stop(AudioStopOptions options)
		{
			AudioBackendRegistry.Xact.StopCue(
				handle,
				(options == AudioStopOptions.Immediate) ? XactStopOptions.Immediate : XactStopOptions.Release
			);
		}

		#endregion

		#region Internal Methods

		internal void OnCueDestroyed()
		{
			IsDisposed = true;
			handle = IntPtr.Zero;
			selfReference = null;
		}

		#endregion

		#region Private Methods

		private void Dispose(bool disposing)
		{
			lock (bank.engine.gcSync)
			{
				if (!IsDisposed)
				{
					if (Disposing != null)
					{
						Disposing.Invoke(this, null);
					}

					// If this is Disposed, stop leaking memory!
					if (!bank.engine.IsDisposed)
					{
						AudioBackendRegistry.Xact.DestroyCue(handle);
					}
					OnCueDestroyed();
				}
			}
		}

		#endregion
	}
}
