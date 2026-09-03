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
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.soundeffectinstance.aspx
	public class SoundEffectInstance : IDisposable
	{
		#region Public Properties

		public bool IsDisposed
		{
			get;
			protected set;
		}

		private bool INTERNAL_looped = false;
		public virtual bool IsLooped
		{
			get
			{
				return INTERNAL_looped;
			}
			set
			{
				if (INTERNAL_looped == value)
				{
					return;
				}
				bool shouldReplay = false;
				if (hasStarted)
				{
					Stop();
					shouldReplay = true;
				}
				INTERNAL_looped = value;
				if (shouldReplay)
				{
					Play();
				}
			}
		}

		private float INTERNAL_pan = 0.0f;
		public float Pan
		{
			get
			{
				return INTERNAL_pan;
			}
			set
			{
				if (IsDisposed)
				{
					throw new ObjectDisposedException(
						"SoundEffectInstance"
					);
				}

				if (value > 1.0f || value < -1.0f)
				{
					throw new ArgumentOutOfRangeException("value");
				}
				INTERNAL_pan = value;
				if (is3D)
				{
					return;
				}

				SetPanMatrixCoefficients();
				if (handle != IntPtr.Zero)
				{
					AudioBackendRegistry.Sound.SetOutputMatrix(handle, AudioOutputTarget.Master, srcChannelCount, dstChannelCount, matrixCoefficients);
				}
			}
		}

		private float INTERNAL_pitch = 0.0f;
		public float Pitch
		{
			get
			{
				return INTERNAL_pitch;
			}
			set
			{
				INTERNAL_pitch = MathHelper.Clamp(value, -1.0f, 1.0f);
				if (handle != IntPtr.Zero)
				{
					UpdatePitch();
				}
			}
		}

		private SoundState INTERNAL_state = SoundState.Stopped;
		public SoundState State
		{
			get
			{
				if (	!isDynamic &&
					handle != IntPtr.Zero &&
					INTERNAL_state == SoundState.Playing	)
				{
					int buffersQueued;
					AudioBackendRegistry.Sound.GetVoiceState(handle, true, out buffersQueued, out _);
					if (buffersQueued == 0)
					{
						Stop(true);
					}
				}
				return INTERNAL_state;
			}
		}

		private float INTERNAL_volume = 1.0f;
		public float Volume
		{
			get
			{
				return INTERNAL_volume;
			}
			set
			{
				INTERNAL_volume = value;
				if (handle != IntPtr.Zero)
				{
					AudioBackendRegistry.Sound.SetVoiceVolume(handle, INTERNAL_volume);
				}
			}
		}

		#endregion

		#region Internal Variables

		internal IntPtr handle;
		internal bool isDynamic;

		#endregion

		#region Private Variables

		private SoundEffect parentEffect;
		private WeakReference selfReference;
		private bool hasStarted;
		private bool is3D;
		private bool usingReverb;

		/* WPR 5c-3a: was FAudio's F3DAUDIO_DSP_SETTINGS (with an unmanaged pMatrixCoefficients
		 * block). The channel counts and the coefficient buffer are plain managed state now — the
		 * backend fills the buffer during Calculate3D and reads it for SetOutputMatrix, so there is
		 * nothing to AllocHGlobal/FreeHGlobal here any more.
		 */
		private int srcChannelCount;
		private int dstChannelCount;
		private float[] matrixCoefficients;
		private float dopplerFactor = 1.0f;

		#endregion

		#region Internal Constructor

		internal SoundEffectInstance(SoundEffect parent = null)
		{
			SoundEffect.Device();

			selfReference = new WeakReference(this, true);
			parentEffect = parent;
			isDynamic = this is DynamicSoundEffectInstance;
			hasStarted = false;
			is3D = false;
			usingReverb = false;
			INTERNAL_state = SoundState.Stopped;

			if (!isDynamic)
			{
				InitDSPSettings(parentEffect.channels);
			}
			if (parentEffect != null)
			{
				parentEffect.Instances.Add(selfReference);
			}
		}

		#endregion

		#region Destructor

		~SoundEffectInstance()
		{
			if (!IsDisposed && State == SoundState.Playing)
			{
				// STOP LEAKING YOUR INSTANCES, ARGH
				GC.ReRegisterForFinalize(this);
				return;
			}
			Dispose();
		}

		#endregion

		#region Public Methods

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

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
			if (IsDisposed)
			{
				throw new ObjectDisposedException(
					"SoundEffectInstance"
				);
			}

			is3D = true;
			SoundEffect.FAudioContext dev = SoundEffect.Device();

			/* WPR 5c-3a: the 3D maths runs in the backend (it owns the native 3D handle and structs).
			 * Note the global SoundEffect.DopplerScale is deliberately NOT passed here — UpdatePitch
			 * folds it into the pitch below, exactly as before, so it must not be applied twice.
			 */
			Audio3DParams params3D = new Audio3DParams
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
				CurveDistanceScaler = dev.CurveDistanceScaler,
				SourceChannels = srcChannelCount,
				DestinationChannels = dstChannelCount,
			};
			AudioBackendRegistry.Sound.Calculate3D(in params3D, matrixCoefficients, out dopplerFactor);
			if (handle != IntPtr.Zero)
			{
				UpdatePitch();
				AudioBackendRegistry.Sound.SetOutputMatrix(handle, AudioOutputTarget.Master, srcChannelCount, dstChannelCount, matrixCoefficients);
			}
		}

		public void Apply3D(AudioListener[] listeners, AudioEmitter emitter)
		{
			if (listeners == null)
			{
				throw new ArgumentNullException("listeners");
			}
			if (listeners.Length == 1)
			{
				Apply3D(listeners[0], emitter);
				return;
			}
			throw new NotSupportedException("Only one listener is supported.");
		}

		public virtual void Play()
		{
			if (State == SoundState.Playing)
			{
				return;
			}
			if (State == SoundState.Paused)
			{
				/* Just resume the existing handle */
				AudioBackendRegistry.Sound.Start(handle);
				INTERNAL_state = SoundState.Playing;
				return;
			}

			SoundEffect.Device();

			/* Create handle. Both paths ask for a filter-capable voice at the default max
			 * frequency ratio, as FNA did (FAUDIO_VOICE_USEFILTER / FAUDIO_DEFAULT_FREQ_RATIO).
			 */
			if (isDynamic)
			{
				AudioWaveFormatEx fmt = (this as DynamicSoundEffectInstance).format;
				handle = AudioBackendRegistry.Sound.CreateSourceVoice(
					fmt.wFormatTag,
					fmt.nChannels,
					(int) fmt.nSamplesPerSec,
					(int) fmt.nAvgBytesPerSec,
					fmt.nBlockAlign,
					fmt.wBitsPerSample,
					fmt.cbSize,
					true,
					DefaultMaxFrequencyRatio
				);
			}
			else
			{
				/* Static effects keep their format marshalled as a native blob (it may be wider
				 * than WAVEFORMATEX, e.g. XMA2), so hand the pointer over as-is.
				 */
				handle = AudioBackendRegistry.Sound.CreateSourceVoiceRaw(
					parentEffect.formatPtr,
					true,
					DefaultMaxFrequencyRatio
				);
			}
			if (handle == IntPtr.Zero)
			{
				return; /* What */
			}

			/* Apply current properties */
			AudioBackendRegistry.Sound.SetVoiceVolume(handle, INTERNAL_volume);
			UpdatePitch();
			if (is3D || Pan != 0.0f)
			{
				AudioBackendRegistry.Sound.SetOutputMatrix(handle, AudioOutputTarget.Master, srcChannelCount, dstChannelCount, matrixCoefficients);
			}

			/* For static effects, submit the buffer now */
			if (isDynamic)
			{
				(this as DynamicSoundEffectInstance).QueueInitialBuffers();
			}
			else
			{
				if (IsLooped)
				{
					parentEffect.handle.LoopCount = 255;
					parentEffect.handle.LoopBegin = parentEffect.loopStart;
					parentEffect.handle.LoopLength = parentEffect.loopLength;
				}
				else
				{
					parentEffect.handle.LoopCount = 0;
					parentEffect.handle.LoopBegin = 0;
					parentEffect.handle.LoopLength = 0;
				}
				AudioBackendRegistry.Sound.SubmitBuffer(handle, in parentEffect.handle);
			}

			/* Play, finally. */
			AudioBackendRegistry.Sound.Start(handle);
			INTERNAL_state = SoundState.Playing;
			hasStarted = true;
		}

		public void Pause()
		{
			if (handle != IntPtr.Zero && State == SoundState.Playing)
			{
				AudioBackendRegistry.Sound.Stop(handle, true);
				INTERNAL_state = SoundState.Paused;
			}
		}

		public void Resume()
		{
			SoundState state = State; // Triggers a query, update
			if (handle == IntPtr.Zero)
			{
				// XNA4 just plays if we've not started yet.
				Play();
			}
			else if (state == SoundState.Paused)
			{
				AudioBackendRegistry.Sound.Start(handle);
				INTERNAL_state = SoundState.Playing;
			}
		}

		public void Stop()
		{
			Stop(true);
		}

		public void Stop(bool immediate)
		{
			if (handle == IntPtr.Zero)
			{
				return;
			}

			if (immediate)
			{
				AudioBackendRegistry.Sound.Stop(handle, true);
				AudioBackendRegistry.Sound.FlushSourceBuffers(handle);
				AudioBackendRegistry.Sound.DestroyVoice(handle);
				handle = IntPtr.Zero;
				usingReverb = false;
				INTERNAL_state = SoundState.Stopped;

				if (isDynamic)
				{
					lock (DynamicSoundEffectInstance.Streams)
					{
						DynamicSoundEffectInstance.Streams.Remove(
							this as DynamicSoundEffectInstance
						);
					}
					(this as DynamicSoundEffectInstance).ClearBuffers();
				}
			}
			else
			{
				if (isDynamic)
				{
					throw new InvalidOperationException();
				}
				AudioBackendRegistry.Sound.ExitLoop(handle);
			}
		}

		#endregion

		#region Protected Methods

		protected virtual void Dispose(bool disposing)
		{
			if (!IsDisposed)
			{
				Stop(true);
				if (parentEffect != null)
				{
					parentEffect.Instances.Remove(selfReference);
				}
				selfReference = null;
				IsDisposed = true;
			}
		}

		#endregion

		#region Internal Methods

		/// <summary>XNA's default max frequency ratio for a source voice (FAUDIO_DEFAULT_FREQ_RATIO).</summary>
		private const float DefaultMaxFrequencyRatio = 2.0f;

		internal void InitDSPSettings(uint srcChannels)
		{
			dopplerFactor = 1.0f;
			srcChannelCount = (int) srcChannels;
			dstChannelCount = SoundEffect.Device().DeviceInfo.ChannelCount;

			/* A managed array zero-initialises, so the explicit memset FNA needed for its
			 * AllocHGlobal block is gone.
			 */
			matrixCoefficients = new float[srcChannelCount * dstChannelCount];
			SetPanMatrixCoefficients();
		}

		internal unsafe void INTERNAL_applyReverb(float rvGain)
		{
			if (handle == IntPtr.Zero)
			{
				return;
			}

			if (!usingReverb)
			{
				SoundEffect.Device().AttachReverb(handle);
				usingReverb = true;
			}

			// Re-using this float array...
			float[] outputMatrix = matrixCoefficients;
			outputMatrix[0] = rvGain;
			if (srcChannelCount == 2)
			{
				outputMatrix[1] = rvGain;
			}
			AudioBackendRegistry.Sound.SetOutputMatrix(handle, AudioOutputTarget.Reverb, srcChannelCount, 1, matrixCoefficients);
		}

		internal void INTERNAL_applyLowPassFilter(float cutoff)
		{
			if (handle == IntPtr.Zero)
			{
				return;
			}

			AudioFilterType filterType = AudioFilterType.LowPass;
			AudioBackendRegistry.Sound.SetFilter(handle, filterType, cutoff, 1.0f);
		}

		internal void INTERNAL_applyHighPassFilter(float cutoff)
		{
			if (handle == IntPtr.Zero)
			{
				return;
			}

			AudioFilterType filterType = AudioFilterType.HighPass;
			AudioBackendRegistry.Sound.SetFilter(handle, filterType, cutoff, 1.0f);
		}

		internal void INTERNAL_applyBandPassFilter(float center)
		{
			if (handle == IntPtr.Zero)
			{
				return;
			}

			AudioFilterType filterType = AudioFilterType.BandPass;
			AudioBackendRegistry.Sound.SetFilter(handle, filterType, center, 1.0f);
		}

		#endregion

		#region Private Methods

		private void UpdatePitch()
		{
			float doppler;
			float dopplerScale = SoundEffect.Device().DopplerScale;
			if (!is3D || dopplerScale == 0.0f)
			{
				doppler = 1.0f;
			}
			else
			{
				doppler = dopplerFactor * dopplerScale;
			}

			AudioBackendRegistry.Sound.SetFrequencyRatio(handle, (float) Math.Pow(2.0, INTERNAL_pitch) * doppler);
		}

		private unsafe void SetPanMatrixCoefficients()
		{
			/* Two major things to notice:
			 * 1. The spec assumes any speaker count >= 2 has Front Left/Right.
			 * 2. Stereo panning is WAY more complicated than you think.
			 *    The main thing is that hard panning does NOT eliminate an
			 *    entire channel; the two channels are blended on each side.
			 * Aside from that, XNA is pretty naive about the output matrix.
			 * -flibit
			 */
			float[] outputMatrix = matrixCoefficients;
			if (srcChannelCount == 1)
			{
				if (dstChannelCount == 1)
				{
					outputMatrix[0] = 1.0f;
				}
				else
				{
					outputMatrix[0] = (INTERNAL_pan > 0.0f) ? (1.0f - INTERNAL_pan) : 1.0f;
					outputMatrix[1] = (INTERNAL_pan < 0.0f) ? (1.0f  + INTERNAL_pan) : 1.0f;
				}
			}
			else
			{
				if (dstChannelCount == 1)
				{
					outputMatrix[0] = 1.0f;
					outputMatrix[1] = 1.0f;
				}
				else
				{
					if (INTERNAL_pan <= 0.0f)
					{
						// Left speaker blends left/right channels
						outputMatrix[0] = 0.5f * INTERNAL_pan + 1.0f;
						outputMatrix[1] = 0.5f * -INTERNAL_pan;
						// Right speaker gets less of the right channel
						outputMatrix[2] = 0.0f;
						outputMatrix[3] = INTERNAL_pan + 1.0f;
					}
					else
					{
						// Left speaker gets less of the left channel
						outputMatrix[0] = -INTERNAL_pan + 1.0f;
						outputMatrix[1] = 0.0f;
						// Right speaker blends right/left channels
						outputMatrix[2] = 0.5f * INTERNAL_pan;
						outputMatrix[3] = 0.5f * -INTERNAL_pan + 1.0f;
					}
				}
			}
		}

		#endregion
	}
}
