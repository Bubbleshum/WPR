using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using WPR.Xna.Rhi;
// NOTE: FAudio (the P/Invoke binding compiled into FNA.dll) lives in the GLOBAL namespace,
// unlike FNA3D which is under Microsoft.Xna.Framework.Graphics — so no using/alias for it.

namespace WPR.Backend.FNA
{
	/// <summary>
	/// FAudio implementation of <see cref="IAudioBackend"/> — Stage 5c-3 (docs/STAGE5C-SCOPE.md).
	///
	/// <para>This class owns ALL of the FAudio/F3DAudio native struct surface: the context, the
	/// mastering voice, the 3D handle blob, and the reverb submix chain (effect chain + descriptors +
	/// send list, with their <c>Marshal.AllocHGlobal</c> lifetimes). Above the seam the XNA audio types
	/// see only primitives and opaque voice handles — that is the whole point of the audio seam sitting
	/// higher than the C ABI (see <see cref="IAudioBackend"/> for the rationale).</para>
	///
	/// <para>The device is singleton state because FAudio is: one context, one mastering voice. The
	/// body of <see cref="TryCreateDevice"/>/<see cref="DestroyDevice"/>/<see cref="AttachReverb"/> is a
	/// faithful transcription of FNA's <c>SoundEffect.FAudioContext</c> (same device-role pick, same
	/// I3DL2-generic reverb defaults, same teardown order) so behaviour is unchanged by the relocation.</para>
	/// </summary>
	public sealed class FnaAudioBackend : IAudioBackend
	{
		private IntPtr _context;
		private IntPtr _masterVoice;
		private byte[]? _handle3D;
		private FAudio.FAudioDeviceDetails _deviceDetails;

		private IntPtr _reverbVoice;
		private FAudio.FAudioVoiceSends _reverbSends;
		private bool _reverbSendsAllocated;

		public bool HasDevice => _context != IntPtr.Zero;

		// ---- Device lifetime ----

		public bool TryCreateDevice(float speedOfSound, out AudioDeviceInfo info)
		{
			info = default;

			IntPtr ctx;
			try
			{
				FAudio.FAudioCreate(out ctx, 0, FAudio.FAUDIO_DEFAULT_PROCESSOR);
			}
			catch (Exception e)
			{
				// FAudio native lib missing — same bail-out FNA had.
				Microsoft.Xna.Framework.FNALoggerEXT.LogWarn?.Invoke("FAudio failed to load: " + e);
				return false;
			}

			FAudio.FAudio_GetDeviceCount(ctx, out uint devices);
			if (devices == 0)
			{
				FAudio.FAudio_Release(ctx); // no sound cards
				return false;
			}

			// Prefer the default *game* device, else fall back to device 0 (FNA's behaviour).
			uint i;
			for (i = 0; i < devices; i += 1)
			{
				FAudio.FAudio_GetDeviceDetails(ctx, i, out _deviceDetails);
				if ((_deviceDetails.Role & FAudio.FAudioDeviceRole.FAudioDefaultGameDevice)
					== FAudio.FAudioDeviceRole.FAudioDefaultGameDevice)
				{
					break;
				}
			}
			if (i == devices)
			{
				i = 0;
				FAudio.FAudio_GetDeviceDetails(ctx, i, out _deviceDetails);
			}

			if (FAudio.FAudio_CreateMasteringVoice(
					ctx,
					out _masterVoice,
					FAudio.FAUDIO_DEFAULT_CHANNELS,
					FAudio.FAUDIO_DEFAULT_SAMPLERATE,
					0,
					i,
					IntPtr.Zero) != 0)
			{
				FAudio.FAudio_Release(ctx);
				_masterVoice = IntPtr.Zero;
				Microsoft.Xna.Framework.FNALoggerEXT.LogError?.Invoke("Failed to create mastering voice!");
				return false;
			}

			_context = ctx;
			_handle3D = new byte[FAudio.F3DAUDIO_HANDLE_BYTESIZE];
			FAudio.F3DAudioInitialize(_deviceDetails.OutputFormat.dwChannelMask, speedOfSound, _handle3D);

			info = new AudioDeviceInfo
			{
				ChannelCount = _deviceDetails.OutputFormat.Format.nChannels,
				SampleRate = (int) _deviceDetails.OutputFormat.Format.nSamplesPerSec,
				ChannelMask = _deviceDetails.OutputFormat.dwChannelMask,
			};
			return true;
		}

		public void DestroyDevice()
		{
			// Order matches FNA's FAudioContext.Dispose: reverb voice (and its send list) first,
			// then the mastering voice, then the context.
			if (_reverbVoice != IntPtr.Zero)
			{
				FAudio.FAudioVoice_DestroyVoice(_reverbVoice);
				_reverbVoice = IntPtr.Zero;
			}
			if (_reverbSendsAllocated)
			{
				Marshal.FreeHGlobal(_reverbSends.pSends);
				_reverbSends = default;
				_reverbSendsAllocated = false;
			}
			if (_masterVoice != IntPtr.Zero)
			{
				FAudio.FAudioVoice_DestroyVoice(_masterVoice);
				_masterVoice = IntPtr.Zero;
			}
			if (_context != IntPtr.Zero)
			{
				FAudio.FAudio_Release(_context);
				_context = IntPtr.Zero;
			}
			_handle3D = null;
		}

		public void SetSpeedOfSound(float speedOfSound)
		{
			if (_handle3D == null) return;
			FAudio.F3DAudioInitialize(_deviceDetails.OutputFormat.dwChannelMask, speedOfSound, _handle3D);
		}

		public float GetMasterVolume()
		{
			if (_masterVoice == IntPtr.Zero) return 0.0f;
			FAudio.FAudioVoice_GetVolume(_masterVoice, out float volume);
			return volume;
		}

		public void SetMasterVolume(float volume)
		{
			if (_masterVoice == IntPtr.Zero) return;
			FAudio.FAudioVoice_SetVolume(_masterVoice, volume, 0);
		}

		public unsafe void AttachReverb(IntPtr voice)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			// Create the reverb submix on first request only (FNA did the same — reverb is opt-in).
			if (_reverbVoice == IntPtr.Zero)
			{
				FAudio.FAudioCreateReverb(out IntPtr reverb, 0);

				IntPtr chainPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FAudio.FAudioEffectChain)));
				FAudio.FAudioEffectChain* reverbChain = (FAudio.FAudioEffectChain*) chainPtr;
				reverbChain->EffectCount = 1;
				reverbChain->pEffectDescriptors = Marshal.AllocHGlobal(
					Marshal.SizeOf(typeof(FAudio.FAudioEffectDescriptor)));

				FAudio.FAudioEffectDescriptor* reverbDesc =
					(FAudio.FAudioEffectDescriptor*) reverbChain->pEffectDescriptors;
				reverbDesc->InitialState = 1;
				reverbDesc->OutputChannels = (uint) ((_deviceDetails.OutputFormat.Format.nChannels == 6) ? 6 : 1);
				reverbDesc->pEffect = reverb;

				FAudio.FAudio_CreateSubmixVoice(
					_context,
					out _reverbVoice,
					1, /* Reverb will be omnidirectional */
					_deviceDetails.OutputFormat.Format.nSamplesPerSec,
					0,
					0,
					IntPtr.Zero,
					chainPtr);
				FAudio.FAPOBase_Release(reverb);

				Marshal.FreeHGlobal(reverbChain->pEffectDescriptors);
				Marshal.FreeHGlobal(chainPtr);

				// Defaults based on FAUDIOFX_I3DL2_PRESET_GENERIC
				IntPtr rvbParamsPtr = Marshal.AllocHGlobal(
					Marshal.SizeOf(typeof(FAudio.FAudioFXReverbParameters)));
				FAudio.FAudioFXReverbParameters* rvbParams = (FAudio.FAudioFXReverbParameters*) rvbParamsPtr;
				rvbParams->WetDryMix = 100.0f;
				rvbParams->ReflectionsDelay = 7;
				rvbParams->ReverbDelay = 11;
				rvbParams->RearDelay = FAudio.FAUDIOFX_REVERB_DEFAULT_REAR_DELAY;
				rvbParams->PositionLeft = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION;
				rvbParams->PositionRight = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION;
				rvbParams->PositionMatrixLeft = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION_MATRIX;
				rvbParams->PositionMatrixRight = FAudio.FAUDIOFX_REVERB_DEFAULT_POSITION_MATRIX;
				rvbParams->EarlyDiffusion = 15;
				rvbParams->LateDiffusion = 15;
				rvbParams->LowEQGain = 8;
				rvbParams->LowEQCutoff = 4;
				rvbParams->HighEQGain = 8;
				rvbParams->HighEQCutoff = 6;
				rvbParams->RoomFilterFreq = 5000f;
				rvbParams->RoomFilterMain = -10f;
				rvbParams->RoomFilterHF = -1f;
				rvbParams->ReflectionsGain = -26.0200005f;
				rvbParams->ReverbGain = 10.0f;
				rvbParams->DecayTime = 1.49000001f;
				rvbParams->Density = 100.0f;
				rvbParams->RoomSize = FAudio.FAUDIOFX_REVERB_DEFAULT_ROOM_SIZE;
				FAudio.FAudioVoice_SetEffectParameters(
					_reverbVoice,
					0,
					rvbParamsPtr,
					(uint) Marshal.SizeOf(typeof(FAudio.FAudioFXReverbParameters)),
					0);
				Marshal.FreeHGlobal(rvbParamsPtr);

				// Send list is kept alive for the life of the device (freed in DestroyDevice).
				_reverbSends = new FAudio.FAudioVoiceSends();
				_reverbSends.SendCount = 2;
				_reverbSends.pSends = Marshal.AllocHGlobal(
					2 * Marshal.SizeOf(typeof(FAudio.FAudioSendDescriptor)));
				_reverbSendsAllocated = true;
				FAudio.FAudioSendDescriptor* sendDesc = (FAudio.FAudioSendDescriptor*) _reverbSends.pSends;
				sendDesc[0].Flags = 0;
				sendDesc[0].pOutputVoice = _masterVoice;
				sendDesc[1].Flags = 0;
				sendDesc[1].pOutputVoice = _reverbVoice;
			}

			FAudio.FAudioVoice_SetOutputVoices(voice, ref _reverbSends);
		}

		// ---- Source voices ----

		public IntPtr CreateSourceVoice(
			int formatTag, int channels, int sampleRate, int avgBytesPerSec,
			int blockAlign, int bitsPerSample, int cbSize,
			bool useFilter, float maxFrequencyRatio)
		{
			FAudio.FAudioWaveFormatEx format = new FAudio.FAudioWaveFormatEx
			{
				wFormatTag = (ushort) formatTag,
				nChannels = (ushort) channels,
				nSamplesPerSec = (uint) sampleRate,
				nAvgBytesPerSec = (uint) avgBytesPerSec,
				nBlockAlign = (ushort) blockAlign,
				wBitsPerSample = (ushort) bitsPerSample,
				cbSize = (ushort) cbSize,
			};
			FAudio.FAudio_CreateSourceVoice(
				_context,
				out IntPtr voice,
				ref format,
				useFilter ? FAudio.FAUDIO_VOICE_USEFILTER : 0u,
				maxFrequencyRatio,
				IntPtr.Zero,
				IntPtr.Zero,
				IntPtr.Zero);
			return voice;
		}

		public IntPtr CreateSourceVoiceRaw(IntPtr formatBlob, bool useFilter, float maxFrequencyRatio)
		{
			FAudio.FAudio_CreateSourceVoice(
				_context,
				out IntPtr voice,
				formatBlob,
				useFilter ? FAudio.FAUDIO_VOICE_USEFILTER : 0u,
				maxFrequencyRatio,
				IntPtr.Zero,
				IntPtr.Zero,
				IntPtr.Zero);
			return voice;
		}

		/// <summary>
		/// True once the device is gone. Every voice operation below is gated on this: source voices
		/// are owned by the FAudio context, so once it has been released they are dangling and calling
		/// into them is a native use-after-free. Finalizers legitimately arrive after device teardown
		/// (the host forces collections while unloading the game's ALC), so these must degrade to
		/// no-ops rather than crash — the voices died with the device and need no explicit cleanup.
		/// </summary>
		private bool NoDevice => _context == IntPtr.Zero;

		public void DestroyVoice(IntPtr voice)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioVoice_DestroyVoice(voice);
		}

		public void SubmitBuffer(IntPtr voice, in AudioBufferDesc buffer)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			// Field-for-field copy into FAudio's struct (the compile validates the transcription).
			FAudio.FAudioBuffer b = new FAudio.FAudioBuffer
			{
				Flags = buffer.Flags,
				AudioBytes = buffer.AudioBytes,
				pAudioData = buffer.pAudioData,
				PlayBegin = buffer.PlayBegin,
				PlayLength = buffer.PlayLength,
				LoopBegin = buffer.LoopBegin,
				LoopLength = buffer.LoopLength,
				LoopCount = buffer.LoopCount,
				pContext = buffer.pContext,
			};
			FAudio.FAudioSourceVoice_SubmitSourceBuffer(voice, ref b, IntPtr.Zero);
		}

		/// <summary>The END_OF_STREAM flag value for <see cref="AudioBufferDesc.Flags"/>, surfaced so the
		/// framework layer doesn't need the native constant.</summary>
		public uint EndOfStreamFlag => FAudio.FAUDIO_END_OF_STREAM;

		public void Start(IntPtr voice)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioSourceVoice_Start(voice, 0, 0);
		}

		public void Stop(IntPtr voice, bool immediate)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioSourceVoice_Stop(voice, immediate ? 0u : FAudio.FAUDIO_PLAY_TAILS, 0);
		}

		public void FlushSourceBuffers(IntPtr voice)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioSourceVoice_FlushSourceBuffers(voice);
		}

		public void ExitLoop(IntPtr voice)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioSourceVoice_ExitLoop(voice, 0);
		}

		public void GetVoiceState(IntPtr voice, bool samplesPlayedNotNeeded, out int buffersQueued, out ulong samplesPlayed)
		{
			if (NoDevice || voice == IntPtr.Zero)
			{
				buffersQueued = 0;
				samplesPlayed = 0;
				return;
			}
			FAudio.FAudioSourceVoice_GetState(
				voice,
				out FAudio.FAudioVoiceState state,
				samplesPlayedNotNeeded ? FAudio.FAUDIO_VOICE_NOSAMPLESPLAYED : 0u);
			buffersQueued = (int) state.BuffersQueued;
			samplesPlayed = state.SamplesPlayed;
		}

		public void SetVoiceVolume(IntPtr voice, float volume)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioVoice_SetVolume(voice, volume, 0);
		}

		public void SetFrequencyRatio(IntPtr voice, float ratio)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioSourceVoice_SetFrequencyRatio(voice, ratio, 0);
		}

		public void SetOutputMatrix(IntPtr voice, AudioOutputTarget target, int sourceChannels, int destinationChannels, float[] levelMatrix)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			IntPtr destination = (target == AudioOutputTarget.Reverb) ? _reverbVoice : _masterVoice;
			GCHandle pin = GCHandle.Alloc(levelMatrix, GCHandleType.Pinned);
			try
			{
				FAudio.FAudioVoice_SetOutputMatrix(
					voice,
					destination,
					(uint) sourceChannels,
					(uint) destinationChannels,
					pin.AddrOfPinnedObject(),
					0);
			}
			finally
			{
				pin.Free();
			}
		}

		public void SetFilter(IntPtr voice, AudioFilterType type, float frequency, float oneOverQ)
		{
			if (NoDevice || voice == IntPtr.Zero) return;
			FAudio.FAudioFilterParameters p = new FAudio.FAudioFilterParameters
			{
				Type = type switch
				{
					AudioFilterType.HighPass => FAudio.FAudioFilterType.FAudioHighPassFilter,
					AudioFilterType.BandPass => FAudio.FAudioFilterType.FAudioBandPassFilter,
					AudioFilterType.Notch => FAudio.FAudioFilterType.FAudioNotchFilter,
					_ => FAudio.FAudioFilterType.FAudioLowPassFilter,
				},
				Frequency = frequency,
				OneOverQ = oneOverQ,
			};
			FAudio.FAudioVoice_SetFilterParameters(voice, ref p, 0);
		}

		// ---- Microphone (platform capture, via FNA's SDL-backed platform layer) ----

		public Microsoft.Xna.Framework.Audio.Microphone[] GetMicrophones() =>
			Microsoft.Xna.Framework.FNAPlatform.GetMicrophones();
		public int GetMicrophoneSamples(uint handle, byte[] buffer, int offset, int count) =>
			Microsoft.Xna.Framework.FNAPlatform.GetMicrophoneSamples(handle, buffer, offset, count);
		public int GetMicrophoneQueuedBytes(uint handle) =>
			Microsoft.Xna.Framework.FNAPlatform.GetMicrophoneQueuedBytes(handle);
		public void StartMicrophone(uint handle) =>
			Microsoft.Xna.Framework.FNAPlatform.StartMicrophone(handle);
		public void StopMicrophone(uint handle) =>
			Microsoft.Xna.Framework.FNAPlatform.StopMicrophone(handle);

		// ---- 3D ----

		public void Calculate3D(in Audio3DParams p, float[] matrixCoefficients, out float dopplerFactor)
		{
			dopplerFactor = 1.0f;
			if (_handle3D == null) return;

			FAudio.F3DAUDIO_LISTENER listener = new FAudio.F3DAUDIO_LISTENER
			{
				OrientFront = ToF3D(p.ListenerForward),
				OrientTop = ToF3D(p.ListenerUp),
				Position = ToF3D(p.ListenerPosition),
				Velocity = ToF3D(p.ListenerVelocity),
			};

			FAudio.F3DAUDIO_EMITTER emitter = new FAudio.F3DAUDIO_EMITTER
			{
				ChannelCount = (uint) p.SourceChannels,
				CurveDistanceScaler = p.CurveDistanceScaler,
				// Emitter scale ONLY — the global SoundEffect.DopplerScale is applied by the caller
				// when it folds the returned doppler factor into pitch (see Audio3DParams).
				DopplerScaler = p.EmitterDopplerScale,
				OrientFront = ToF3D(p.EmitterForward),
				OrientTop = ToF3D(p.EmitterUp),
				Position = ToF3D(p.EmitterPosition),
				Velocity = ToF3D(p.EmitterVelocity),

				/* Fixed defaults that used to be set once in AudioEmitter's ctor, based on XNA
				 * behaviour: no cone, unit channel radius, pinned stereo azimuths, and no custom
				 * curves (the remaining pointer fields default to IntPtr.Zero).
				 */
				ChannelRadius = 1.0f,
				pChannelAzimuths = StereoAzimuthPtr,
			};

			GCHandle coeffPin = GCHandle.Alloc(matrixCoefficients, GCHandleType.Pinned);
			try
			{
				FAudio.F3DAUDIO_DSP_SETTINGS dsp = new FAudio.F3DAUDIO_DSP_SETTINGS
				{
					SrcChannelCount = (uint) p.SourceChannels,
					DstChannelCount = (uint) p.DestinationChannels,
					pMatrixCoefficients = coeffPin.AddrOfPinnedObject(),
				};

				FAudio.F3DAudioCalculate(
					_handle3D,
					ref listener,
					ref emitter,
					FAudio.F3DAUDIO_CALCULATE_MATRIX | FAudio.F3DAUDIO_CALCULATE_DOPPLER,
					ref dsp);

				dopplerFactor = dsp.DopplerFactor;
			}
			finally
			{
				coeffPin.Free();
			}
		}

		/// <summary>XNA space → native 3D-audio space. The Z flip is the right-handed/left-handed
		/// conversion that AudioListener/AudioEmitter used to do inline in every property setter; it
		/// belongs here now that those types hold plain XNA-space values.</summary>
		private static FAudio.F3DAUDIO_VECTOR ToF3D(Vector3 v) =>
			new FAudio.F3DAUDIO_VECTOR { x = v.X, y = v.Y, z = -v.Z };

		/* Pinned for the process lifetime, exactly as AudioEmitter's static handle was — F3DAudio
		 * reads it during Calculate3D and must not see a moved/collected array. */
		private static readonly float[] StereoAzimuth = new float[] { 0.0f, 0.0f };
		private static readonly GCHandle StereoAzimuthHandle =
			GCHandle.Alloc(StereoAzimuth, GCHandleType.Pinned);
		private static IntPtr StereoAzimuthPtr => StereoAzimuthHandle.AddrOfPinnedObject();
	}
}
