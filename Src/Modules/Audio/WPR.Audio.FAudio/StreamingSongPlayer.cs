using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WPR.Xna.Rhi;

namespace WPR.Audio.FAudio
{
	/// <summary>
	/// The song half of <see cref="FAudioMediaBackend"/>: streams an Ogg Vorbis file into an FAudio
	/// source voice from a decode thread of its own, keeping several short buffers queued ahead of
	/// the mixer. Replaces FAudio's native <c>XNA_Song</c> player.
	///
	/// <para><b>Why not <c>XNA_PlaySong</c>.</b> <c>XNA_SongSubmitBuffer</c> (<c>XNA_Song.c</c>)
	/// decodes one whole second into a single reused cache, with a queue depth of ONE, refilled from
	/// <c>OnBufferEnd</c>. That callback fires once the buffer has already finished, so at every
	/// boundary the voice has nothing queued and the mixer thread is busy decoding a second of
	/// Vorbis: an audible click exactly once a second on a phone, which is why Android moved its
	/// songs to the platform <c>MediaPlayer</c>. The same gap broke <c>XNA_GetSongEnded</c>, which
	/// answers "ended" whenever <c>BuffersQueued == 0</c>, so a poll landing in that gap ended the
	/// song early. The prebuilt FAudio binaries cannot be rebuilt on the Windows side, so the fix
	/// is here, in managed code, over the FAudio voice API both heads already ship.</para>
	///
	/// <para><b>Decoder.</b> NVorbis (MIT, fully managed), so the same code runs on both heads with
	/// no native library: FAudio's own stb_vorbis is compiled in statically and not exported, and
	/// Theorafile — the other Vorbis decoder in the tree — is not shipped on Windows at all.
	/// Songs are always Ogg Vorbis files on disk: the installer transcodes WP7's WMA soundtracks
	/// in place, keeping the <c>.wma</c> name.</para>
	///
	/// <para><b>Buffering.</b> <see cref="SlotCount"/> slots of 1/<see cref="SlotsPerSecond"/> s
	/// each, used as a ring. A slot is rewritten only when fewer than <see cref="SlotCount"/>
	/// buffers are queued, which (FAudio plays buffers in submission order) means the oldest one,
	/// the slot about to be reused, has finished. No native-to-managed callback is involved: the
	/// thread polls <c>GetState</c>, which keeps foreign mixer threads out of the runtime.</para>
	///
	/// <para><b>Its own FAudio engine, and therefore its own output device.</b> Sharing the
	/// sound-effect engine would put songs under its mastering voice, and that voice's volume IS
	/// <c>SoundEffect.MasterVolume</c> — a game muting its effects would mute its music. The cost
	/// is that the platform does not pause this device for us: SDL's OpenSL ES driver tracks a
	/// single output device, so on Android the song kept playing on the home screen (measured on
	/// an API 36 emulator — the track stayed Active with frames advancing). Hence
	/// <see cref="SuspendForBackground"/>, which <c>GameActivity</c> drives through
	/// <c>AudioBackendRegistry</c>.</para>
	/// </summary>
	internal sealed class StreamingSongPlayer
	{
		private const int SlotCount = 6;
		private const int SlotsPerSecond = 8;      // 125 ms a slot, 750 ms queued when full
		private const int IdleWaitMs = 20;

		private readonly object _lock = new object();

		private IntPtr _engine;
		private IntPtr _master;
		private float _volume = 1.0f;
		private bool _visualization;

		// Per song. All of it belongs to the decode thread while one runs, except _voice (which the
		// game thread may Start/Stop/SetVolume — FAudio voice calls are thread-safe) and the flags.
		private IntPtr _voice;
		private NVorbis.VorbisReader? _decoder;
		private IntPtr[] _slots = Array.Empty<IntPtr>();
		private int _slotFrames;
		private int _channels;
		private int _sampleRate;
		private long _submitted;
		private Thread? _thread;
		private volatile bool _stopRequested;
		private volatile bool _ended = true;
		private volatile bool _paused;
		// The host app is in the background. Latched rather than acted on once: a game reacting
		// to its own Deactivated may stop and replay a track around the pause, and a song
		// started then must not play on the home screen.
		private volatile bool _hostSuspended;
		private volatile bool _decoderDone;
		private int _underruns;
		private string? _name;
		private readonly AutoResetEvent _wake = new AutoResetEvent(false);

		public void Init()
		{
			lock (_lock)
			{
				if (_engine != IntPtr.Zero) return;
				/* Same mix quantum as the sound-effect engine (FAudioSoundBackend.TryCreateDevice):
				 * FAudio's default is a 10 ms device buffer, which a phone's non-realtime audio
				 * callback underruns. FAudio's own XNA_SongInit never asked for more. */
#if __ANDROID__
				const uint engineFlags = global::FAudio.FAUDIO_1024_QUANTUM;
#else
				const uint engineFlags = 0;
#endif
				if (global::FAudio.FAudioCreate(out _engine, engineFlags, global::FAudio.FAUDIO_DEFAULT_PROCESSOR) != 0)
				{
					_engine = IntPtr.Zero;
					XnaBackend.LogWarn("[wpr-song] FAudioCreate failed; songs will not play.");
					return;
				}
				if (global::FAudio.FAudio_CreateMasteringVoice(
					_engine,
					out _master,
					global::FAudio.FAUDIO_DEFAULT_CHANNELS,
					global::FAudio.FAUDIO_DEFAULT_SAMPLERATE,
					0,
					0,
					IntPtr.Zero) != 0)
				{
					_master = IntPtr.Zero;
					global::FAudio.FAudio_Release(_engine);
					_engine = IntPtr.Zero;
					XnaBackend.LogWarn("[wpr-song] no audio device for songs; songs will not play.");
				}
			}
		}

		public void Quit()
		{
			lock (_lock)
			{
				KillLocked();
				if (_master != IntPtr.Zero)
				{
					global::FAudio.FAudioVoice_DestroyVoice(_master);
					_master = IntPtr.Zero;
				}
				if (_engine != IntPtr.Zero)
				{
					global::FAudio.FAudio_Release(_engine);
					_engine = IntPtr.Zero;
				}
			}
		}

		/// <summary>Starts a song; returns its length in seconds (0 when it cannot be played, which
		/// <c>MediaPlayer</c> already treats as a song that ends at once — the native behaviour).</summary>
		public float Play(string fileName)
		{
			lock (_lock)
			{
				KillLocked();
				if (_engine == IntPtr.Zero) Init();
				if (_engine == IntPtr.Zero) return 0f;

				NVorbis.VorbisReader decoder;
				int channels, rate;
				double seconds;
				try
				{
					decoder = new NVorbis.VorbisReader(fileName);
					channels = decoder.Channels;
					rate = decoder.SampleRate;
					seconds = decoder.TotalTime.TotalSeconds;
				}
				catch (Exception ex)
				{
					Trace.WriteLine("[wpr-song] cannot decode " + fileName + ": " + ex.GetType().Name + ": " + ex.Message);
					return 0f;
				}
				if (channels <= 0 || rate <= 0)
				{
					decoder.Dispose();
					return 0f;
				}

				var format = new global::FAudio.FAudioWaveFormatEx
				{
					wFormatTag = 3, // IEEE float
					nChannels = (ushort) channels,
					nSamplesPerSec = (uint) rate,
					wBitsPerSample = 32,
					nBlockAlign = (ushort) (channels * 4),
					nAvgBytesPerSec = (uint) (rate * channels * 4),
					cbSize = 0,
				};
				if (global::FAudio.FAudio_CreateSourceVoice(
					_engine, out IntPtr voice, ref format, 0, 1.0f,
					IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
				{
					decoder.Dispose();
					XnaBackend.LogWarn("[wpr-song] FAudio_CreateSourceVoice failed for " + fileName);
					return 0f;
				}

				_decoder = decoder;
				_voice = voice;
				_channels = channels;
				_sampleRate = rate;
				_slotFrames = Math.Max(256, rate / SlotsPerSecond);
				_slots = new IntPtr[SlotCount];
				for (int i = 0; i < SlotCount; i++)
				{
					_slots[i] = Marshal.AllocHGlobal(_slotFrames * channels * sizeof(float));
				}
				_submitted = 0;
				_underruns = 0;
				_decoderDone = false;
				_stopRequested = false;
				_paused = false;
				_ended = false;
				_name = Path.GetFileName(fileName);

				global::FAudio.FAudioVoice_SetVolume(_voice, _volume, 0);

				// Fill all but one slot before starting, so the first mixer pass has a deep queue.
				// The decode thread tops up the rest.
				for (int i = 0; i < SlotCount - 1 && !_decoderDone; i++)
				{
					SubmitNext();
				}
				if (!_hostSuspended)
				{
					global::FAudio.FAudioSourceVoice_Start(_voice, 0, 0);
				}

				_thread = new Thread(DecodeLoop)
				{
					IsBackground = true,
					Name = "WPR song decoder",
					Priority = ThreadPriority.AboveNormal,
				};
				_thread.Start();

				Trace.WriteLine($"[wpr-song] playing {_name}: {rate} Hz x{channels}, {seconds:0.0} s");
				return (float) seconds;
			}
		}

		public void Pause()
		{
			lock (_lock)
			{
				if (_voice == IntPtr.Zero) return;
				_paused = true;
				global::FAudio.FAudioSourceVoice_Stop(_voice, 0, 0);
			}
		}

		public void Resume()
		{
			lock (_lock)
			{
				if (_voice == IntPtr.Zero) return;
				_paused = false;
				if (!_hostSuspended)
				{
					global::FAudio.FAudioSourceVoice_Start(_voice, 0, 0);
				}
				_wake.Set();
			}
		}

		/// <summary>The host left the foreground: silence the song, keeping its position.</summary>
		public void SuspendForBackground()
		{
			lock (_lock)
			{
				if (_hostSuspended) return;
				_hostSuspended = true;
				if (_voice != IntPtr.Zero && !_paused)
				{
					global::FAudio.FAudioSourceVoice_Stop(_voice, 0, 0);
				}
				Trace.WriteLine("[wpr-song] host backgrounded; song " + (_voice != IntPtr.Zero ? "suspended" : "none"));
			}
		}

		/// <summary>Undoes <see cref="SuspendForBackground"/>. A song the game paused stays paused.</summary>
		public void RestoreFromForeground()
		{
			lock (_lock)
			{
				if (!_hostSuspended) return;
				_hostSuspended = false;
				if (_voice != IntPtr.Zero && !_paused)
				{
					global::FAudio.FAudioSourceVoice_Start(_voice, 0, 0);
					_wake.Set();
				}
			}
		}

		public void Stop()
		{
			lock (_lock)
			{
				KillLocked();
			}
		}

		public void SetVolume(float volume)
		{
			lock (_lock)
			{
				_volume = volume;
				if (_voice != IntPtr.Zero)
				{
					global::FAudio.FAudioVoice_SetVolume(_voice, volume, 0);
				}
			}
		}

		/// <summary>True when no song is loaded or the loaded one has played its last sample. Only
		/// the decode thread sets it, and only once the decoder is exhausted AND the queue has
		/// drained, so a poll between two buffers can no longer end a song early.</summary>
		public bool Ended => _ended;

		public bool VisualizationEnabled
		{
			get => _visualization;
			set => _visualization = value;
		}

		/// <summary>
		/// XNA's visualization tap: <paramref name="samples"/> gets the most recent
		/// <paramref name="count"/> mono samples of the buffer the mixer is playing, and
		/// <paramref name="frequencies"/> a normalised magnitude spectrum of them. Upstream FAudio
		/// left this a TODO and returned nothing at all.
		/// </summary>
		public void GetVisualizationData(float[] frequencies, float[] samples, int count)
		{
			if (frequencies != null) Array.Clear(frequencies, 0, Math.Min(count, frequencies.Length));
			if (samples != null) Array.Clear(samples, 0, Math.Min(count, samples.Length));
			if (!_visualization || frequencies == null || samples == null || count <= 0) return;

			lock (_lock)
			{
				if (_voice == IntPtr.Zero || _slots.Length == 0) return;
				global::FAudio.FAudioSourceVoice_GetState(_voice, out var state, 0);
				int slot = (int) state.pCurrentBufferContext - 1;
				if (slot < 0 || slot >= _slots.Length) return;

				int n = Math.Min(count, Math.Min(samples.Length, _slotFrames));
				unsafe
				{
					float* src = (float*) _slots[slot];
					for (int i = 0; i < n; i++)
					{
						float sum = 0;
						for (int c = 0; c < _channels; c++) sum += src[i * _channels + c];
						samples[i] = sum / _channels;
					}
				}
			}

			int bins = Math.Min(count, frequencies.Length);
			int len = Math.Min(count, samples.Length);
			for (int k = 0; k < bins; k++)
			{
				// Bins span 0..Nyquist, so bin k is frequency k/(2*bins) of the sample rate.
				double w = Math.PI * k / bins;
				double re = 0, im = 0;
				for (int i = 0; i < len; i++)
				{
					re += samples[i] * Math.Cos(w * i);
					im -= samples[i] * Math.Sin(w * i);
				}
				double mag = Math.Sqrt(re * re + im * im) * 2.0 / len;
				frequencies[k] = (float) Math.Min(1.0, mag);
			}
		}

		// ---- decode thread ----

		private void DecodeLoop()
		{
			try
			{
				while (!_stopRequested)
				{
					global::FAudio.FAudioSourceVoice_GetState(_voice, out var state, global::FAudio.FAUDIO_VOICE_NOSAMPLESPLAYED);

					if (_decoderDone)
					{
						if (state.BuffersQueued == 0)
						{
							_ended = true;
							return;
						}
					}
					else
					{
						if (state.BuffersQueued == 0 && !_paused && !_hostSuspended && _submitted > 0)
						{
							// The mixer ran dry before we refilled it: an audible gap.
							_underruns++;
						}
						while (state.BuffersQueued < SlotCount && !_decoderDone && !_stopRequested)
						{
							SubmitNext();
							state.BuffersQueued++;
						}
					}

					_wake.WaitOne(IdleWaitMs);
				}
			}
			catch (Exception ex)
			{
				XnaBackend.LogWarn("[wpr-song] decode thread failed: " + ex);
				_ended = true;
			}
		}

		/// <summary>Decodes one slot and submits it. Caller guarantees the slot is free.</summary>
		private void SubmitNext()
		{
			int slot = (int) (_submitted % SlotCount);
			int wanted = _slotFrames * _channels;
			// NVorbis 0.10.5 can hang for ever in a ReadSamples that reaches past the end of a
			// stream whose first granule is not 0 (found through a movie soundtrack, see
			// MediaCodecStream.ReadAudio); ask for no more than is left. A short read still ends
			// the song below, as before.
			int ask = wanted;
			if (_decoder!.TotalSamples > 0)
			{
				ask = (int) Math.Clamp((_decoder.TotalSamples - _decoder.SamplePosition) * _channels, 0, wanted);
			}
			int got = 0;
			if (ask > 0)
			{
				unsafe
				{
					got = _decoder.ReadSamples(new Span<float>((void*) _slots[slot], ask));
				}
			}
			got -= got % _channels;
			if (got < wanted)
			{
				_decoderDone = true;
			}
			if (got <= 0)
			{
				return;
			}

			var buffer = new global::FAudio.FAudioBuffer
			{
				Flags = _decoderDone ? global::FAudio.FAUDIO_END_OF_STREAM : 0,
				AudioBytes = (uint) (got * sizeof(float)),
				pAudioData = _slots[slot],
				PlayBegin = 0,
				PlayLength = (uint) (got / _channels),
				LoopBegin = 0,
				LoopLength = 0,
				LoopCount = 0,
				pContext = (IntPtr) (slot + 1),
			};
			global::FAudio.FAudioSourceVoice_SubmitSourceBuffer(_voice, ref buffer, IntPtr.Zero);
			_submitted++;
		}

		private void KillLocked()
		{
			if (_voice == IntPtr.Zero && _decoder == null)
			{
				_ended = true;
				return;
			}

			_stopRequested = true;
			_wake.Set();
			Thread? thread = _thread;
			_thread = null;
			if (thread != null && thread != Thread.CurrentThread)
			{
				thread.Join(2000);
			}

			if (_voice != IntPtr.Zero)
			{
				global::FAudio.FAudioSourceVoice_Stop(_voice, 0, 0);
				// DestroyVoice waits for the mixer to let go of the voice, so the slots below are
				// no longer referenced once it returns.
				global::FAudio.FAudioVoice_DestroyVoice(_voice);
				_voice = IntPtr.Zero;
			}
			foreach (IntPtr p in _slots)
			{
				if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
			}
			_slots = Array.Empty<IntPtr>();
			if (_decoder != null)
			{
				_decoder.Dispose();
				_decoder = null;
			}

			if (_underruns > 0)
			{
				XnaBackend.LogWarn("[wpr-song] " + _name + ": " + _underruns + " underrun(s) while playing.");
			}
			Trace.WriteLine("[wpr-song] stopped " + _name + " after " + _submitted + " buffer(s), underruns=" + _underruns);
			_ended = true;
		}
	}
}
