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
using System.Collections.Generic;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Audio
{
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.dynamicsoundeffectinstance.aspx
	public sealed class DynamicSoundEffectInstance : SoundEffectInstance
	{
		#region Public Properties

		public int PendingBufferCount
		{
			get
			{
				return queuedBuffers.Count;
			}
		}

		public override bool IsLooped
		{
			get
			{
				return false;
			}
			set
			{
				// No-op, DynamicSoundEffectInstance cannot be looped!
			}
		}

		#endregion

		#region Internal Variables

		internal AudioWaveFormatEx format;

		#endregion

		#region Private Variables

		private int sampleRate;
		private AudioChannels channels;

		private List<IntPtr> queuedBuffers;
		private List<uint> queuedSizes;

		#endregion

		#region Private Constants

		private const int MINIMUM_BUFFER_CHECK = 3;

		#endregion

		#region BufferNeeded Event

		public event EventHandler<EventArgs> BufferNeeded;

		#endregion

		#region Public Constructor

		public DynamicSoundEffectInstance(
			int sampleRate,
			AudioChannels channels
		) : base() {
			this.sampleRate = sampleRate;
			this.channels = channels;
			isDynamic = true;

			format = new AudioWaveFormatEx();
			format.wFormatTag = 1;
			format.nChannels = (ushort) channels;
			format.nSamplesPerSec = (uint) sampleRate;
			format.wBitsPerSample = 16;
			format.nBlockAlign = (ushort) (2 * format.nChannels);
			format.nAvgBytesPerSec = format.nBlockAlign * format.nSamplesPerSec;
			format.cbSize = 0;

			queuedBuffers = new List<IntPtr>();
			queuedSizes = new List<uint>();

			InitDSPSettings(format.nChannels);
		}

		#endregion

		#region Destructor

		~DynamicSoundEffectInstance()
		{
			// FIXME: ReRegisterForFinalize? -flibit
			Dispose();
		}

		#endregion

		#region Public Methods

		public TimeSpan GetSampleDuration(int sizeInBytes)
		{
			return SoundEffect.GetSampleDuration(
				sizeInBytes,
				sampleRate,
				channels
			);
		}

		public int GetSampleSizeInBytes(TimeSpan duration)
		{
			return SoundEffect.GetSampleSizeInBytes(
				duration,
				sampleRate,
				channels
			);
		}

		public override void Play()
		{
			// Wait! What if we need moar buffers?
			Update();

			// Okay we're good
			base.Play();
			lock (Streams)
			{
				if (!Streams.Contains(this))
				{
					Streams.Add(this);
				}
			}
		}

		public void SubmitBuffer(byte[] buffer)
		{
			this.SubmitBuffer(buffer, 0, buffer.Length);
		}

		public void SubmitBuffer(byte[] buffer, int offset, int count)
		{
			IntPtr next = Marshal.AllocHGlobal(count);
			Marshal.Copy(buffer, offset, next, count);
			lock (queuedBuffers)
			{
				queuedBuffers.Add(next);
				if (State != SoundState.Stopped)
				{
					AudioBufferDesc buf = new AudioBufferDesc();
					buf.AudioBytes = (uint) count;
					buf.pAudioData = next;
					buf.PlayLength = (
						buf.AudioBytes /
						(uint) channels /
						(uint) (format.wBitsPerSample / 8)
					);
					AudioBackendRegistry.Sound.SubmitBuffer(handle, in buf);
				}
				else
				{
					queuedSizes.Add((uint) count);
				}
			}
		}

		public void SubmitFloatBufferEXT(float[] buffer)
		{
			SubmitFloatBufferEXT(buffer, 0, buffer.Length);
		}

		public void SubmitFloatBufferEXT(float[] buffer, int offset, int count)
		{
			/* Float samples are the typical format received from decoders.
			 * We currently use this for the VideoPlayer.
			 * -flibit
			 */
			if (State != SoundState.Stopped && format.wFormatTag == 1)
			{
				throw new InvalidOperationException(
					"Submit a float buffer before Playing!"
				);
			}
			format.wFormatTag = 3;
			format.wBitsPerSample = 32;
			format.nBlockAlign = (ushort) (4 * format.nChannels);
			format.nAvgBytesPerSec = format.nBlockAlign * format.nSamplesPerSec;

			IntPtr next = Marshal.AllocHGlobal(count * sizeof(float));
			Marshal.Copy(buffer, offset, next, count);
			lock (queuedBuffers)
			{
				queuedBuffers.Add(next);
				if (State != SoundState.Stopped)
				{
					AudioBufferDesc buf = new AudioBufferDesc();
					buf.AudioBytes = (uint) count * sizeof(float);
					buf.pAudioData = next;
					buf.PlayLength = (
						buf.AudioBytes /
						(uint) channels /
						(uint) (format.wBitsPerSample / 8)
					);
					AudioBackendRegistry.Sound.SubmitBuffer(handle, in buf);
				}
				else
				{
					queuedSizes.Add((uint) count * sizeof(float));
				}
			}
		}

		#endregion

		#region Protected Methods

		protected override void Dispose(bool disposing)
		{
			// Not much to see here...
			base.Dispose(disposing);
		}

		#endregion

		#region Internal Methods

		internal void QueueInitialBuffers()
		{
			AudioBufferDesc buffer = new AudioBufferDesc();
			lock (queuedBuffers)
			{
				for (int i = 0; i < queuedBuffers.Count; i += 1)
				{
					buffer.AudioBytes = queuedSizes[i];
					buffer.pAudioData = queuedBuffers[i];
					buffer.PlayLength = (
						buffer.AudioBytes /
						(uint) channels /
						(uint) (format.wBitsPerSample / 8)
					);
					AudioBackendRegistry.Sound.SubmitBuffer(handle, in buffer);
				}
				queuedSizes.Clear();
			}
		}

		internal void ClearBuffers()
		{
			lock (queuedBuffers)
			{
				foreach (IntPtr buf in queuedBuffers)
				{
					Marshal.FreeHGlobal(buf);
				}
				queuedBuffers.Clear();
				queuedSizes.Clear();
			}
		}

		/* WPR 5c-3a (pump inversion): this registry and its per-frame pump used to live on FNA's
		 * FrameworkDispatcher. That type stays in FNA (it also pumps MediaPlayer + TouchPanel), and
		 * code in this assembly may not reference FNA — so the list lives here and FNA's
		 * FrameworkDispatcher.Update() calls UpdateAll() instead, which it can reach through this
		 * assembly's InternalsVisibleTo("FNA"). Loop body kept identical, including the "i -= 1"
		 * fixup for instances that dispose (and so remove) themselves during Update().
		 */
		internal static readonly List<DynamicSoundEffectInstance> Streams =
			new List<DynamicSoundEffectInstance>();

		internal static void UpdateAll()
		{
			lock (Streams)
			{
				for (int i = 0; i < Streams.Count; i += 1)
				{
					DynamicSoundEffectInstance dsfi = Streams[i];
					dsfi.Update();
					if (dsfi.IsDisposed)
					{
						i -= 1;
					}
				}
			}
		}

		internal void Update()
		{
			if (State != SoundState.Playing)
			{
				// Shh, we don't need you right now...
				return;
			}

			if (handle != IntPtr.Zero)
			{
				int buffersQueued;
					AudioBackendRegistry.Sound.GetVoiceState(handle, true, out buffersQueued, out _);
				while (PendingBufferCount > buffersQueued)
				lock (queuedBuffers)
				{
					Marshal.FreeHGlobal(queuedBuffers[0]);
					queuedBuffers.RemoveAt(0);
				}
			}

			// Do we need even moar buffers?
			for (
				int i = MINIMUM_BUFFER_CHECK - PendingBufferCount;
				(i > 0) && BufferNeeded != null;
				i -= 1
			) {
				BufferNeeded(this, null);
			}
		}

		#endregion
	}
}
