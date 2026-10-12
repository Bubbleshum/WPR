using System;
using System.Diagnostics;
using Microsoft.Xna.Framework.Audio;
using WPR.Engine.Audio;
using WPR.Xna.Rhi;

namespace WPR.Xna.Media
{
	/// <summary>
	/// Plays one movie file for the host: a wall clock, the newest due RGBA frame, and the sound
	/// through a <see cref="DynamicSoundEffectInstance"/>. Shared by the two WP7 APIs that show
	/// video — <c>MediaPlayerLauncher</c> (drawn full screen by the host overlay) and Silverlight's
	/// <c>MediaElement</c> (painted into its page) — so they agree on timing and on when a movie
	/// has ended.
	///
	/// <para>Decoding is the platform's (<see cref="VideoDecoderBackend"/>). Everything here runs
	/// on the game thread, once per frame, through <see cref="Update"/>.</para>
	///
	/// <para><b>Clock.</b> Wall time while playing, not the audio position: a cutscene is a minute
	/// at most, the two cannot drift audibly in that time, and a wall clock keeps the picture
	/// moving when a movie has no sound or the audio device is missing.</para>
	/// </summary>
	public sealed class WprMoviePlayer : IDisposable
	{
		private readonly IVideoStream _stream;
		private readonly Stopwatch _clock = new Stopwatch();
		private DynamicSoundEffectInstance? _sound;
		private byte[]? _audioChunk;
		private bool _audioDone;
		private bool _disposed;
		private float _volume = 1f;

		private WprMoviePlayer(IVideoStream stream, string name)
		{
			_stream = stream;
			Name = name;
			Frame = new byte[stream.Width * stream.Height * 4];
			_audioDone = stream.AudioChannels <= 0;
		}

		/// <summary>Opens <paramref name="path"/> with the platform decoder; null when this
		/// platform has none or cannot play the file. Never throws.</summary>
		public static WprMoviePlayer? TryOpen(string path)
		{
			IVideoDecoder? decoder = VideoDecoderBackend.Decoder;
			if (decoder == null || !decoder.IsAvailable)
			{
				XnaBackend.LogInfo("[wpr-video] no video decoder on this platform; skipping " + path);
				return null;
			}
			IVideoStream? stream;
			try
			{
				stream = decoder.Open(path);
			}
			catch (Exception ex)
			{
				XnaBackend.LogWarn("[wpr-video] " + decoder.Name + " threw opening " + path + ": " + ex.Message);
				return null;
			}
			if (stream == null || stream.Width <= 0 || stream.Height <= 0)
			{
				stream?.Dispose();
				XnaBackend.LogInfo("[wpr-video] " + decoder.Name + " cannot play " + path + "; skipping");
				return null;
			}
			XnaBackend.LogInfo($"[wpr-video] playing {System.IO.Path.GetFileName(path)} via {decoder.Name}: {stream.Width}x{stream.Height}, {stream.Duration.TotalSeconds:0.0} s");
			return new WprMoviePlayer(stream, System.IO.Path.GetFileName(path));
		}

		public string Name { get; }
		public int Width => _stream.Width;
		public int Height => _stream.Height;
		public TimeSpan Duration => _stream.Duration;
		public TimeSpan Position => _clock.Elapsed;
		public bool IsPlaying => _clock.IsRunning;

		/// <summary>The newest frame, RGBA, <see cref="Width"/> x <see cref="Height"/>.</summary>
		public byte[] Frame { get; }

		/// <summary>Bumped whenever <see cref="Frame"/> changes, so a consumer can skip re-uploads.</summary>
		public int FrameVersion { get; private set; }

		/// <summary>True once the picture and the sound have both run out.</summary>
		public bool Ended { get; private set; }

		public float Volume
		{
			get => _volume;
			set
			{
				_volume = Math.Clamp(value, 0f, 1f);
				if (_sound != null) _sound.Volume = _volume;
			}
		}

		public void Play()
		{
			if (_disposed || Ended) return;
			_clock.Start();
			EnsureSound();
			if (_sound != null && _sound.State != SoundState.Playing)
			{
				TryAudio(() => _sound.Play());
			}
		}

		public void Pause()
		{
			_clock.Stop();
			if (_sound != null && _sound.State == SoundState.Playing)
			{
				TryAudio(() => _sound.Pause());
			}
		}

		/// <summary>Advances one frame. True when <see cref="Frame"/> changed.</summary>
		public bool Update()
		{
			if (_disposed || Ended) return false;

			bool newFrame = false;
			if (_clock.IsRunning)
			{
				newFrame = _stream.TryGetFrame(_clock.Elapsed, Frame);
				if (newFrame) FrameVersion++;
				FeedAudio();
			}

			bool soundDone = _audioDone && (_sound == null || _sound.PendingBufferCount == 0);
			bool overrun = _stream.Duration > TimeSpan.Zero && _clock.Elapsed > _stream.Duration + TimeSpan.FromSeconds(3);
			if ((_stream.VideoEnded && soundDone) || overrun)
			{
				Ended = true;
				_clock.Stop();
			}
			return newFrame;
		}

		private void EnsureSound()
		{
			if (_sound != null || _audioDone) return;
			try
			{
				_sound = new DynamicSoundEffectInstance(
					_stream.AudioSampleRate,
					_stream.AudioChannels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
				_sound.Volume = _volume;
				// About 1/15 s a buffer; FeedAudio keeps a few queued.
				int bytes = _stream.AudioSampleRate * _stream.AudioChannels * 2 / 15;
				_audioChunk = new byte[bytes - bytes % (_stream.AudioChannels * 2)];
			}
			catch (Exception ex)
			{
				// No audio device (NoAudioHardwareException) is not a reason to lose the picture.
				XnaBackend.LogWarn("[wpr-video] no sound for " + Name + ": " + ex.Message);
				_sound = null;
				_audioDone = true;
			}
		}

		private void FeedAudio()
		{
			if (_sound == null || _audioDone || _audioChunk == null) return;
			while (_sound.PendingBufferCount < 4)
			{
				int n = _stream.ReadAudio(_audioChunk, 0, _audioChunk.Length);
				if (n < 0)
				{
					_audioDone = true;
					return;
				}
				if (n == 0) return;
				n -= n % (_stream.AudioChannels * 2);
				if (n == 0) return;
				byte[] buffer = n == _audioChunk.Length ? (byte[]) _audioChunk.Clone() : _audioChunk.AsSpan(0, n).ToArray();
				TryAudio(() => _sound.SubmitBuffer(buffer));
			}
		}

		private void TryAudio(Action a)
		{
			try
			{
				a();
			}
			catch (Exception ex)
			{
				XnaBackend.LogWarn("[wpr-video] audio for " + Name + " failed: " + ex.Message);
				try { _sound?.Dispose(); } catch { }
				_sound = null;
				_audioDone = true;
			}
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;
			_clock.Stop();
			try
			{
				_sound?.Stop();
				_sound?.Dispose();
			}
			catch (Exception)
			{
			}
			_sound = null;
			_stream.Dispose();
		}
	}
}
