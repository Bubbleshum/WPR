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
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
#endregion

namespace Microsoft.Xna.Framework.Audio
{
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.soundeffect.aspx
	public sealed class SoundEffect : IDisposable
	{
		#region Public Properties

		public TimeSpan Duration
		{
			get
			{
				return TimeSpan.FromSeconds(
					(double) handle.PlayLength /
					(double) sampleRate
				);
			}
		}

		public bool IsDisposed
		{
			get;
			private set;
		}

		public string Name
		{
			get;
			set;
		}

		#endregion

		#region Public Static Properties

		public static float MasterVolume
		{
			get
			{
				Device();
				return AudioBackendRegistry.Sound.GetMasterVolume();
			}
			set
			{
				Device();
				AudioBackendRegistry.Sound.SetMasterVolume(value);
			}
		}

		public static float DistanceScale
		{
			get
			{
				return Device().CurveDistanceScaler;
			}
			set
			{
				if (value <= 0.0f)
				{
					throw new ArgumentOutOfRangeException("value <= 0.0f");
				}
				Device().CurveDistanceScaler = value;
			}
		}

		public static float DopplerScale
		{
			get
			{
				return Device().DopplerScale;
			}
			set
			{
				if (value < 0.0f)
				{
					throw new ArgumentOutOfRangeException("value < 0.0f");
				}
				Device().DopplerScale = value;
			}
		}

		public static float SpeedOfSound
		{
			get
			{
				return Device().SpeedOfSound;
			}
			set
			{
				FAudioContext dev = Device();
				dev.SpeedOfSound = value;
				AudioBackendRegistry.Sound.SetSpeedOfSound(dev.SpeedOfSound);
			}
		}

		#endregion

		#region Internal Variables

		internal List<WeakReference> Instances = new List<WeakReference>();
		internal AudioBufferDesc handle;
		internal IntPtr formatPtr;
		internal ushort channels;
		internal uint sampleRate;
		internal uint loopStart;
		internal uint loopLength;

		#endregion

		#region Public Constructors

		public SoundEffect(
			byte[] buffer,
			int sampleRate,
			AudioChannels channels
		) : this(
			null,
			buffer,
			0,
			buffer.Length,
			null,
			1,
			(ushort) channels,
			(uint) sampleRate,
			(uint) (sampleRate * ((ushort) channels * 2)),
			(ushort) ((ushort) channels * 2),
			16,
			0,
			0
		) {
		}

		public SoundEffect(
			byte[] buffer,
			int offset,
			int count,
			int sampleRate,
			AudioChannels channels,
			int loopStart,
			int loopLength
		) : this(
			null,
			buffer,
			offset,
			count,
			null,
			1,
			(ushort) channels,
			(uint) sampleRate,
			(uint) (sampleRate * ((ushort) channels * 2)),
			(ushort) ((ushort) channels * 2),
			16,
			loopStart,
			loopLength
		) {
		}

		#endregion

		#region Internal Constructor

		internal unsafe SoundEffect(
			string name,
			byte[] buffer,
			int offset,
			int count,
			byte[] extraData,
			ushort wFormatTag,
			ushort nChannels,
			uint nSamplesPerSec,
			uint nAvgBytesPerSec,
			ushort nBlockAlign,
			ushort wBitsPerSample,
			int loopStart,
			int loopLength
		) {
			Device();
			Name = name;
			channels = nChannels;
			sampleRate = nSamplesPerSec;
			this.loopStart = (uint) loopStart;
			this.loopLength = (uint) loopLength;

			/* Buffer format */
			if (extraData == null)
			{
				formatPtr = Marshal.AllocHGlobal(
					Marshal.SizeOf(typeof(AudioWaveFormatEx))
				);
			}
			else
			{
				formatPtr = Marshal.AllocHGlobal(
					Marshal.SizeOf(typeof(AudioWaveFormatEx)) +
					extraData.Length
				);
				Marshal.Copy(
					extraData,
					0,
					formatPtr + Marshal.SizeOf(typeof(AudioWaveFormatEx)),
					extraData.Length
				);
			}

			AudioWaveFormatEx* pcm = (AudioWaveFormatEx*) formatPtr;
			pcm->wFormatTag = wFormatTag;
			pcm->nChannels = nChannels;
			pcm->nSamplesPerSec = nSamplesPerSec;
			pcm->nAvgBytesPerSec = nAvgBytesPerSec;
			pcm->nBlockAlign = nBlockAlign;
			pcm->wBitsPerSample = wBitsPerSample;
			pcm->cbSize = (ushort) ((extraData == null) ? 0 : extraData.Length);

			/* Easy stuff */
			handle = new AudioBufferDesc();
			handle.Flags = AudioBackendRegistry.Sound.EndOfStreamFlag;
			handle.pContext = IntPtr.Zero;

			/* Buffer data */
			handle.AudioBytes = (uint) count;
			handle.pAudioData = Marshal.AllocHGlobal(count);
			Marshal.Copy(
				buffer,
				offset,
				handle.pAudioData,
				count
			);

			/* Play regions */
			handle.PlayBegin = 0;
			if (wFormatTag == 1)
			{
				handle.PlayLength = (uint) (
					count /
					nChannels /
					(wBitsPerSample / 8)
				);
			}
			else if (wFormatTag == 2)
			{
				handle.PlayLength = (uint) (
					count /
					nBlockAlign *
					(((nBlockAlign / nChannels) - 6) * 2)
				);
			}
			else if (wFormatTag == 0x166)
			{
				AudioXma2WaveFormatEx* xma2 = (AudioXma2WaveFormatEx*) formatPtr;
				// dwSamplesEncoded / nChannels / (wBitsPerSample / 8) doesn't always (if ever?) match up.
				handle.PlayLength = xma2->dwPlayLength;
			}

			/* Set by Instances! */
			handle.LoopBegin = 0;
			handle.LoopLength = 0;
			handle.LoopCount = 0;
		}

		#endregion

		#region Destructor

		~SoundEffect()
		{
			if (Instances.Count > 0)
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
			if (!IsDisposed)
			{
				/* FIXME: Is it ironic that we're generating
				 * garbage with ToArray while cleaning up after
				 * the program's leaks?
				 * -flibit
				 */
				foreach (WeakReference instance in Instances.ToArray())
				{
					object target = instance.Target;
					if (target != null)
					{
						(target as IDisposable).Dispose();
					}
				}
				Instances.Clear();
				Marshal.FreeHGlobal(formatPtr);
				Marshal.FreeHGlobal(handle.pAudioData);
				IsDisposed = true;
			}
		}

		public bool Play()
		{
			return Play(1.0f, 0.0f, 0.0f);
		}

		public bool Play(float volume, float pitch, float pan)
		{
			SoundEffectInstance instance = new SoundEffectInstance(this);
			instance.Volume = volume;
			instance.Pitch = pitch;
			instance.Pan = pan;
			instance.Play();
			if (instance.State != SoundState.Playing)
			{
				// Ran out of AL sources, probably.
				instance.Dispose();
				return false;
			}
			return true;
		}

		public SoundEffectInstance CreateInstance()
		{
			return new SoundEffectInstance(this);
		}

		#endregion

		#region Public Static Methods

		public static TimeSpan GetSampleDuration(
			int sizeInBytes,
			int sampleRate,
			AudioChannels channels
		) {
			sizeInBytes /= 2; // 16-bit PCM!
			int ms = (int) (
				(sizeInBytes / (int) channels) /
				(sampleRate / 1000.0f)
			);
			return new TimeSpan(0, 0, 0, 0, ms);
		}

		public static int GetSampleSizeInBytes(
			TimeSpan duration,
			int sampleRate,
			AudioChannels channels
		) {
			return (int) (
				duration.TotalSeconds *
				sampleRate *
				(int) channels *
				2 // 16-bit PCM!
			);
		}

		public static SoundEffect FromStream(Stream stream)
		{
			// Sample data
			byte[] data;

			// WaveFormatEx data
			ushort wFormatTag;
			ushort nChannels;
			uint nSamplesPerSec;
			uint nAvgBytesPerSec;
			ushort nBlockAlign;
			ushort wBitsPerSample;
			// ushort cbSize;

			int samplerLoopStart = 0;
			int samplerLoopEnd = 0;

			using (BinaryReader reader = new BinaryReader(stream))
			{
				// RIFF Signature
				string signature = new string(reader.ReadChars(4));
				if (signature != "RIFF")
				{
					throw new NotSupportedException("Specified stream is not a wave file.");
				}

				reader.ReadUInt32(); // Riff Chunk Size

				string wformat = new string(reader.ReadChars(4));
				if (wformat != "WAVE")
				{
					throw new NotSupportedException("Specified stream is not a wave file.");
				}

				// WAVE Header
				string format_signature = new string(reader.ReadChars(4));
				while (format_signature != "fmt ")
				{
					reader.ReadBytes(reader.ReadInt32());
					format_signature = new string(reader.ReadChars(4));
				}

				int format_chunk_size = reader.ReadInt32();

				wFormatTag = reader.ReadUInt16();
				nChannels = reader.ReadUInt16();
				nSamplesPerSec = reader.ReadUInt32();
				nAvgBytesPerSec = reader.ReadUInt32();
				nBlockAlign = reader.ReadUInt16();
				wBitsPerSample = reader.ReadUInt16();

				// Reads residual bytes
				if (format_chunk_size > 16)
				{
					reader.ReadBytes(format_chunk_size - 16);
				}

				// data Signature
				string data_signature = new string(reader.ReadChars(4));
				while (data_signature.ToLowerInvariant() != "data")
				{
					reader.ReadBytes(reader.ReadInt32());
					data_signature = new string(reader.ReadChars(4));
				}
				if (data_signature != "data")
				{
					//throw new NotSupportedException("Specified wave file is not supported.");
					Debug.WriteLine("[ex] SoundEffect: " + "Specified wave file is not supported.");
					//return; // RnD
				}

				int waveDataLength = reader.ReadInt32();
				data = reader.ReadBytes(waveDataLength);

				// Scan for other chunks
				while (reader.PeekChar() != -1)
				{
					char[] chunkIDChars = reader.ReadChars(4);
					if (chunkIDChars.Length < 4)
					{
						break; // EOL!
					}
					byte[] chunkSizeBytes = reader.ReadBytes(4);
					if (chunkSizeBytes.Length < 4)
					{
						break; // EOL!
					}
					string chunk_signature = new string(chunkIDChars);
					int chunkDataSize = BitConverter.ToInt32(chunkSizeBytes, 0);
					if (chunk_signature == "smpl") // "smpl", Sampler Chunk Found
					{
						reader.ReadUInt32(); // Manufacturer
						reader.ReadUInt32(); // Product
						reader.ReadUInt32(); // Sample Period
						reader.ReadUInt32(); // MIDI Unity Note
						reader.ReadUInt32(); // MIDI Pitch Fraction
						reader.ReadUInt32(); // SMPTE Format
						reader.ReadUInt32(); // SMPTE Offset
						uint numSampleLoops = reader.ReadUInt32();
						int samplerData = reader.ReadInt32();

						for (int i = 0; i < numSampleLoops; i += 1)
						{
							reader.ReadUInt32(); // Cue Point ID
							reader.ReadUInt32(); // Type
							int start = reader.ReadInt32();
							int end = reader.ReadInt32();
							reader.ReadUInt32(); // Fraction
							reader.ReadUInt32(); // Play Count

							if (i == 0) // Grab loopStart and loopEnd from first sample loop
							{
								samplerLoopStart = start;
								samplerLoopEnd = end;
							}
						}

						if (samplerData != 0) // Read Sampler Data if it exists
						{
							reader.ReadBytes(samplerData);
						}
					}
					else // Read unwanted chunk data and try again
					{
						reader.ReadBytes(chunkDataSize);
					}
				}
				// End scan
			}

			return new SoundEffect(
				null,
				data,
				0,
				data.Length,
				null,
				wFormatTag,
				nChannels,
				nSamplesPerSec,
				nAvgBytesPerSec,
				nBlockAlign,
				wBitsPerSample,
				samplerLoopStart,
				samplerLoopEnd - samplerLoopStart
			);
		}

		#endregion

		#region FAudio Context

		/// <summary>
		/// WPR (Stage 5c-3a): the audio device/context now lives in the BACKEND
		/// (<c>WPR.Audio.FAudio.FAudioSoundBackend</c>), which owns every FAudio/F3DAudio struct â€” the
		/// context handle, mastering voice, 3D handle blob and the reverb submix chain. What remains
		/// here is a thin shim holding the MANAGED device state XNA exposes (distance / doppler /
		/// speed-of-sound) and forwarding lifetime + reverb to the backend.
		///
		/// <para><b>THIS SHAPE IS LOAD-BEARING â€” do not rename it, its static <c>Context</c> field, or
		/// its <c>Dispose()</c>, and do not change their visibility.</b> Two callers bind it by name:
		/// <list type="bullet">
		/// <item><c>WPR.ApplicationLaunch.TeardownAudioState</c> reflects
		/// <c>GetNestedType("FAudioContext")</c> â†’ static field <c>Context</c> â†’ instance
		/// <c>Dispose()</c> to drive the ordered audio teardown that fixes the stuck-audio and
		/// ALC-unload regressions (ADR Risk #1).</item>
		/// <item>FNA's <c>SDL2_FNAPlatform.ProgramExit</c> calls <c>FAudioContext.Context.Dispose()</c>
		/// directly (legal via this assembly's InternalsVisibleTo("FNA")).</item>
		/// </list></para>
		/// </summary>
		internal class FAudioContext
		{
			public static FAudioContext Context = null;

			internal const float DefaultSpeedOfSound = 343.5f;

			/// <summary>Output-device properties (channel count / sample rate / speaker mask).
			/// Replaces the old FAudio device-details struct.</summary>
			public AudioDeviceInfo DeviceInfo;

			public float CurveDistanceScaler;
			public float DopplerScale;
			public float SpeedOfSound;

			private FAudioContext(AudioDeviceInfo info)
			{
				DeviceInfo = info;
				CurveDistanceScaler = 1.0f;
				DopplerScale = 1.0f;
				SpeedOfSound = DefaultSpeedOfSound;
				Context = this;
			}

			public void Dispose()
			{
				AudioBackendRegistry.Sound.DestroyDevice();
				Context = null;
			}

			public void AttachReverb(IntPtr voice)
			{
				AudioBackendRegistry.Sound.AttachReverb(voice);
			}

			public static void Create()
			{
				if (!AudioBackendRegistry.HasSound)
				{
					/* No audio backend registered (e.g. a headless host). Device() turns
					 * this into NoAudioHardwareException, same as a missing sound card.
					 */
					return;
				}

				AudioDeviceInfo info;
				if (!AudioBackendRegistry.Sound.TryCreateDevice(DefaultSpeedOfSound, out info))
				{
					/* FAudio missing, no sound cards, or the soundcard failed to
					 * configure â€” all of which FNA treated as "no device".
					 */
					return;
				}

				new FAudioContext(info); /* ctor publishes itself as Context */
			}
		}

		private static readonly object createLock = new object();
		internal static FAudioContext Device()
		{
			/* Ideally the device has been made, just return it. */
			if (FAudioContext.Context != null)
			{
				return FAudioContext.Context;
			}

			/* From here on out, it gets weird... */
			lock (createLock)
			{
				/* If this trips it's because another thread
				 * got here first. We do the check above to
				 * avoid the mutex lock for the 99.99% of the
				 * time where it's not necessary.
				 */
				if (FAudioContext.Context != null)
				{
					return FAudioContext.Context;
				}

				/* If you're here, you were the first caller!
				 * that, or there genuinely is no hardware and
				 * you're about to get a lot more of these.
				 */
				FAudioContext.Create();
				if (FAudioContext.Context == null)
				{
					throw new NoAudioHardwareException();
				}
			}
			return FAudioContext.Context;
		}

		#endregion
	}
}
