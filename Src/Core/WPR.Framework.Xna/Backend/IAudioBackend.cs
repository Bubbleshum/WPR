using System;
using Microsoft.Xna.Framework;

namespace WPR.Xna.Rhi
{
	/// <summary>Properties of the opened audio output device, needed by the XNA audio types for
	/// output-matrix sizing and 3D setup. Replaces direct reads of FAudio's device-details struct.</summary>
	public struct AudioDeviceInfo
	{
		/// <summary>Output channel count (<c>OutputFormat.Format.nChannels</c>).</summary>
		public int ChannelCount;
		/// <summary>Output sample rate (<c>OutputFormat.Format.nSamplesPerSec</c>).</summary>
		public int SampleRate;
		/// <summary>Speaker channel mask (<c>OutputFormat.dwChannelMask</c>).</summary>
		public uint ChannelMask;
	}

	/// <summary>Which backend-owned mix target an output matrix applies to. The master and reverb voice
	/// handles stay inside the backend, so callers name the target instead of passing a handle.</summary>
	public enum AudioOutputTarget
	{
		Master,
		Reverb,
	}

	/// <summary>Voice filter kinds XNA exposes (<c>SoundEffectInstance</c>'s low/high/band-pass helpers).
	/// WPR-owned so the framework layer never names FAudio's enum.</summary>
	public enum AudioFilterType
	{
		LowPass,
		BandPass,
		Notch,
		HighPass,
	}

	/// <summary>A queued audio buffer. Flat mirror of FAudio's <c>FAudioBuffer</c> (same field names and
	/// order) — mirroring THIS is safe and worth it: it is plain data with no delegates or pointer-arrays,
	/// it is held as a field by <c>SoundEffect</c>, and keeping the field names identical means the
	/// relocated audio code needs no edits at its use sites. Sample-denominated, as XNA has it.</summary>
	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	public struct AudioBufferDesc
	{
		public uint Flags;
		public uint AudioBytes;
		public IntPtr pAudioData;
		public uint PlayBegin;
		public uint PlayLength;
		public uint LoopBegin;
		public uint LoopLength;
		public uint LoopCount;
		public IntPtr pContext;
	}

	/// <summary>Flat mirror of <c>WAVEFORMATEX</c> (FAudio's <c>FAudioWaveFormatEx</c>) — the layout the
	/// XNA audio types marshal into unmanaged memory for a source voice's format. Layout-identical, so
	/// the backend can hand the same blob straight to the native API.</summary>
	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	public struct AudioWaveFormatEx
	{
		public ushort wFormatTag;
		public ushort nChannels;
		public uint nSamplesPerSec;
		public uint nAvgBytesPerSec;
		public ushort nBlockAlign;
		public ushort wBitsPerSample;
		public ushort cbSize;
	}

	/// <summary>Flat mirror of <c>XMA2WAVEFORMATEX</c>. XMA2 is the Xbox/WP compressed audio format; the
	/// XNA layer only reads <c>dwPlayLength</c> out of the header it already holds as a native blob, but
	/// the full layout must match so that field lands at the right offset.</summary>
	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	public struct AudioXma2WaveFormatEx
	{
		public AudioWaveFormatEx wfx;
		public ushort wNumStreams;
		public uint dwChannelMask;
		public uint dwSamplesEncoded;
		public uint dwBytesPerBlock;
		public uint dwPlayBegin;
		public uint dwPlayLength;
		public uint dwLoopBegin;
		public uint dwLoopLength;
		public byte bLoopCount;
		public byte bEncoderVersion;
		public ushort wBlockCount;
	}

	/// <summary>Inputs for one 3D audio calculation, in plain WPR-owned values. The backend turns
	/// these into whatever its native 3D API needs (FAudio's F3DAUDIO_LISTENER/EMITTER/DSP_SETTINGS).</summary>
	public struct Audio3DParams
	{
		public Vector3 ListenerForward, ListenerUp, ListenerPosition, ListenerVelocity;
		public Vector3 EmitterForward, EmitterUp, EmitterPosition, EmitterVelocity;
		/// <summary>Per-emitter doppler scale (<c>AudioEmitter.DopplerScale</c>). NOTE: the GLOBAL
		/// <c>SoundEffect.DopplerScale</c> is deliberately NOT part of this — XNA applies it later, when
		/// combining the returned doppler factor with the user's pitch, so the backend must not
		/// pre-multiply it or the pitch would be scaled twice.</summary>
		public float EmitterDopplerScale;
		/// <summary>Global <c>SoundEffect.DistanceScale</c>.</summary>
		public float CurveDistanceScaler;
		public int SourceChannels;
		public int DestinationChannels;
	}

	/// <summary>
	/// The audio backend seam — Stage 5c-3 (Plans/STAGE5C-SCOPE.md). The WPR-owned XNA audio types
	/// (<c>SoundEffect</c>/<c>SoundEffectInstance</c>/<c>DynamicSoundEffectInstance</c>) drive playback
	/// through this instead of calling FAudio, so they carry no native dependency.
	///
	/// <para><b>Why this sits HIGHER than the C ABI</b> (deliberately unlike
	/// <see cref="IGraphicsBackend"/>, which mirrors FNA3D 1:1): FAudio/FACT expose 21 structs, several
	/// carrying delegates or pointer-arrays, and mirroring those would mean hand-rolling delegate
	/// marshalling with GC-lifetime hazards in WPR's historically most fragile subsystem. So this
	/// contract speaks in primitives and the <em>adapter</em> builds every native struct — the whole
	/// FAudio struct surface stays behind the seam. Audio calls are per-sound-instance, not per-frame,
	/// so the extra indirection is free, and a future XAudio2/AAudio backend implements these
	/// operations rather than emulating FAudio's ABI.</para>
	///
	/// <para>Voices and the device are opaque <see cref="IntPtr"/> handles, exactly as the XNA types
	/// already hold them. The device itself is backend-owned singleton state (FAudio allows one
	/// context), so the device operations take no handle.</para>
	/// </summary>
	public interface IAudioBackend
	{
		// ---- Device lifetime (adapter owns the context, mastering voice, 3D handle, reverb chain) ----

		/// <summary>Opens the audio device: creates the context, picks the default game device, creates
		/// the mastering voice and initialises the 3D handle. Returns false when there is no usable
		/// audio hardware (the caller then throws <c>NoAudioHardwareException</c>, as XNA does).</summary>
		bool TryCreateDevice(float speedOfSound, out AudioDeviceInfo info);

		/// <summary>Tears the device down (reverb voice, mastering voice, context) and frees native
		/// allocations. Must be idempotent — the host calls it during ordered teardown.</summary>
		void DestroyDevice();

		/// <summary>True while a device is open.</summary>
		bool HasDevice { get; }

		/// <summary>Re-initialises the 3D handle for a new speed of sound (<c>SoundEffect.SpeedOfSound</c>).</summary>
		void SetSpeedOfSound(float speedOfSound);

		float GetMasterVolume();
		void SetMasterVolume(float volume);

		/// <summary>Routes a source voice through the reverb submix, creating that submix (effect chain,
		/// I3DL2 generic defaults, send list) on first use.</summary>
		void AttachReverb(IntPtr voice);

		// ---- Source voices ----

		/// <summary>Creates a source voice for a PCM-style format described by its wave-format fields.</summary>
		IntPtr CreateSourceVoice(
			int formatTag, int channels, int sampleRate, int avgBytesPerSec,
			int blockAlign, int bitsPerSample, int cbSize,
			bool useFilter, float maxFrequencyRatio);

		/// <summary>Creates a source voice from a raw native wave-format blob — for formats wider than
		/// <c>WAVEFORMATEX</c> (e.g. XMA2), where the XNA type already holds the marshalled bytes.</summary>
		IntPtr CreateSourceVoiceRaw(IntPtr formatBlob, bool useFilter, float maxFrequencyRatio);

		void DestroyVoice(IntPtr voice);

		/// <summary>Queues a buffer on a source voice.</summary>
		void SubmitBuffer(IntPtr voice, in AudioBufferDesc buffer);

		/// <summary>The backend's "end of stream" value for <see cref="AudioBufferDesc.Flags"/>, so the
		/// framework layer never names a native constant.</summary>
		uint EndOfStreamFlag { get; }

		void Start(IntPtr voice);

		/// <summary><paramref name="immediate"/> false = let queued buffers drain (XNA's "as authored" stop).</summary>
		void Stop(IntPtr voice, bool immediate);

		void FlushSourceBuffers(IntPtr voice);
		void ExitLoop(IntPtr voice);

		/// <summary>Reads voice state. <paramref name="samplesPlayedNotNeeded"/> maps to FAudio's
		/// NOSAMPLESPLAYED fast path for callers that only want the queued-buffer count.</summary>
		void GetVoiceState(IntPtr voice, bool samplesPlayedNotNeeded, out int buffersQueued, out ulong samplesPlayed);

		void SetVoiceVolume(IntPtr voice, float volume);
		void SetFrequencyRatio(IntPtr voice, float ratio);

		/// <summary>Sets the per-channel output level matrix (pan / 3D positioning / reverb send).
		/// <paramref name="levelMatrix"/> is source×destination, row-major.</summary>
		void SetOutputMatrix(IntPtr voice, AudioOutputTarget target, int sourceChannels, int destinationChannels, float[] levelMatrix);

		/// <summary>Applies a voice filter (XNA's low/high/band-pass helpers and 3D distance muffling).</summary>
		void SetFilter(IntPtr voice, AudioFilterType type, float frequency, float oneOverQ);

		// ---- 3D ----

		/// <summary>
		/// Runs one 3D calculation and returns its results. Deliberately a PURE calculation — it does
		/// NOT apply anything to the voice — so the XNA <c>SoundEffectInstance</c> keeps its original
		/// logic for combining the doppler factor with the user's pitch and for pushing the matrix,
		/// rather than that behaviour being re-derived inside a backend.
		/// </summary>
		/// <param name="matrixCoefficients">Caller-owned buffer, at least SourceChannels×DestinationChannels.</param>
		void Calculate3D(in Audio3DParams p, float[] matrixCoefficients, out float dopplerFactor);

		// ---- Microphone (capture; the platform layer's concern, not FAudio's) ----

		/// <summary>Enumerates capture devices. The backend constructs the instances because it owns the
		/// per-device platform handles; <c>Microphone</c> is WPR-owned as of 5c-3a, and its internal ctor
		/// is reachable from the backend through this assembly's <c>InternalsVisibleTo</c>.</summary>
		Microsoft.Xna.Framework.Audio.Microphone[] GetMicrophones();
		int GetMicrophoneSamples(uint handle, byte[] buffer, int offset, int count);
		int GetMicrophoneQueuedBytes(uint handle);
		void StartMicrophone(uint handle);
		void StopMicrophone(uint handle);
	}
}
