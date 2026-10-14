#nullable enable
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Android.Media;
using WPR.Engine.Audio;

namespace WPR.Audio.AudioTrack
{
    /// <summary>
    /// One streaming voice on its own <see cref="global::Android.Media.AudioTrack"/>, always stereo
    /// and at the device's native rate.
    /// </summary>
    /// <remarks>
    /// <para>Moved from <c>WPR.Engine.Wp8Native.AudioTrackOutput</c> (2026-10-13), where it played
    /// WP8 titles' XAudio2 voices. Through FAudio every WP8 title's sound was choppy on a phone
    /// while clean on the desktop: Android's output pulls bigger, burstier periods, and a game
    /// keeping two ~30 ms buffers queued ran dry a few times a second. A stream-mode track goes
    /// straight to the platform mixer, and "finished" is read off its playback head, so the game is
    /// paced by what has actually been heard. A cushion of extra queued audio was tried first and
    /// added audible lag; this needs none.</para>
    /// <para>Writes are non-blocking, from one writer thread per track: a blocking write into a
    /// paused or flushed track is the easy way to hang the game thread.</para>
    /// <para><b>Native rate, stereo.</b> Anything but the device's native rate is refused the
    /// fast path and resampled by AudioFlinger's general-purpose resampler, which made Modern
    /// Combat 4's 32 kHz mix dull; <see cref="SincResampler"/> does it here instead. Stereo so the
    /// voice's output matrix (XNA pan and 3D) can be applied in software.</para>
    /// </remarks>
    internal sealed class AudioTrackVoice : IDisposable
    {
        private readonly global::Android.Media.AudioTrack? _track;
        private readonly SincResampler? _resampler;
        private const int OutChannels = 2;
        private const int FrameBytes = 2 * OutChannels;

        /// <summary>Guards the track, <see cref="_ends"/> and everything below.</summary>
        private readonly object _gate = new();

        /// <summary>The frame each still-playing buffer ends at, counted since the last flush.</summary>
        private readonly Queue<long> _ends = new();
        private long _submittedFrames;
        private ulong _sourceFramesPlayedBefore;
        private int _generation;
        private bool _wantPlaying;
        private bool _suspended;
        private bool _disposed;
        private float _volume = 1f;
        private float _master = 1f;
        private double _pitch = 1.0;
        private int _underrunBaseline;

        /// <summary>Output gains, [destination * 2 + source] for a stereo source, [destination] for mono.</summary>
        private readonly float[] _matrix = new float[4];
        private bool _matrixIsDefault = true;

        private readonly BlockingCollection<(int Generation, byte[] Data)> _queue = new();
        private readonly Thread? _writer;

        public AudioTrackVoice(int sampleRate, int channels)
        {
            SampleRate = sampleRate;
            Channels = channels;
            ResetMatrix();

            int native = 0;
            try
            {
                native = global::Android.Media.AudioTrack.GetNativeOutputSampleRate(global::Android.Media.Stream.Music);
            }
            catch (Exception)
            {
            }

            OutputRate = native > 0 ? native : 48000;
            if (OutputRate != sampleRate)
            {
                _resampler = new SincResampler(sampleRate, OutputRate, channels);
            }

            int minimum = global::Android.Media.AudioTrack.GetMinBufferSize(OutputRate, ChannelOut.Stereo, Encoding.Pcm16bit);
            var builder = new global::Android.Media.AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Game)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!)
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetSampleRate(OutputRate)!
                    .SetChannelMask(ChannelOut.Stereo)!
                    .Build()!)
                .SetTransferMode(AudioTrackMode.Stream)
                .SetBufferSizeInBytes(Math.Max(minimum, 0) * 2);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                builder.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency);
            }

            _track = builder.Build() ?? throw new InvalidOperationException("AudioTrack.Builder returned null");
            BufferFrames = _track.BufferSizeInFrames;

            // A streaming track does not start until its whole buffer is full, and a game keeps far
            // less than that queued (MC4: two 25 ms buffers against ~200 ms), so it never started,
            // the head never moved, no buffer ever finished and the game's audio stopped after its
            // first two. Start after 10 ms instead. Below API 31 there is no threshold to lower, so
            // the buffer is primed with silence instead (again after every flush, which empties it).
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                _track.SetStartThresholdInFrames(Math.Clamp(OutputRate / 100, 1, BufferFrames));
            }
            else
            {
                PrimeWithSilence();
            }

            _writer = new Thread(Write) { Name = "AudioTrack writer", IsBackground = true };
            _writer.Start();
        }

        public int SampleRate { get; }

        public int Channels { get; }

        /// <summary>The rate the track plays at: the device's native one.</summary>
        public int OutputRate { get; }

        public int BufferFrames { get; }

        /// <summary>Still able to play: not disposed and its writer alive.</summary>
        public bool Usable
        {
            get
            {
                lock (_gate)
                {
                    return !_disposed && _track is not null && _writer is { IsAlive: true };
                }
            }
        }

        /// <summary>Underruns since <see cref="Begin"/>.</summary>
        public int Underruns
        {
            get
            {
                lock (_gate)
                {
                    return _track is { } track && !_disposed ? Math.Max(0, track.UnderrunCount - _underrunBaseline) : 0;
                }
            }
        }

        /// <summary>Makes the track a fresh voice: a new one, or one coming back from the pool.</summary>
        public void Begin(float master, bool suspended)
        {
            lock (_gate)
            {
                _master = master;
                _volume = 1f;
                _pitch = 1.0;
                _wantPlaying = false;
                _suspended = suspended;
                ResetMatrix();
                _resampler?.SetPitch(1.0);
                if (_track is { } track)
                {
                    _underrunBaseline = track.UnderrunCount;
                    if (_resampler is null)
                    {
                        track.SetPlaybackRate(OutputRate);
                    }

                    ApplyVolume(track);
                }
            }
        }

        /// <summary>Back to the pool: silent, empty, and nothing of the old voice left queued.</summary>
        public void Park() => Locked(track =>
        {
            _wantPlaying = false;
            ClearLocked(track);
        });

        private void ResetMatrix()
        {
            // FAudio's defaults: a mono source to both speakers at full level, stereo straight across.
            if (Channels == 1)
            {
                _matrix[0] = 1f;
                _matrix[1] = 1f;
            }
            else
            {
                _matrix[0] = 1f;
                _matrix[1] = 0f;
                _matrix[2] = 0f;
                _matrix[3] = 1f;
            }

            _matrixIsDefault = true;
        }

        /// <summary>Fills the empty buffer with silence so the track starts (pre-API-31 only). Under <see cref="_gate"/>.</summary>
        private void PrimeWithSilence()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31) || _track is null)
            {
                return;
            }

            byte[] silence = new byte[BufferFrames * FrameBytes];
            int written = _track.Write(silence, 0, silence.Length, WriteMode.NonBlocking);
            _submittedFrames += Math.Max(0, written) / FrameBytes;
        }

        /// <summary>Advances past every buffer the playback head has passed. Under <see cref="_gate"/>.</summary>
        private void Retire()
        {
            if (_track is { } track && !_disposed)
            {
                long head = (uint)track.PlaybackHeadPosition;
                while (_ends.Count > 0 && _ends.Peek() <= head)
                {
                    _ends.Dequeue();
                }
            }
            else
            {
                // No track: nothing can play, so nothing may wait on it.
                _ends.Clear();
            }
        }

        public void GetState(out int buffersQueued, out ulong samplesPlayed)
        {
            lock (_gate)
            {
                Retire();
                buffersQueued = _ends.Count;
                long head = _track is { } track && !_disposed ? (uint)track.PlaybackHeadPosition : 0;
                samplesPlayed = _sourceFramesPlayedBefore + (ulong)Math.Max(0, (long)(head * (SampleRate / (double)OutputRate)));
            }
        }

        public void Submit(in AudioBufferDesc buffer)
        {
            if (buffer.pAudioData == IntPtr.Zero || buffer.AudioBytes == 0)
            {
                return;
            }

            byte[] pcm = new byte[buffer.AudioBytes];
            Marshal.Copy(buffer.pAudioData, pcm, 0, pcm.Length);

            int generation;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_resampler is not null)
                {
                    pcm = _resampler.Process(pcm);
                }

                pcm = ToStereo(pcm);
                _submittedFrames += pcm.Length / FrameBytes;
                _ends.Enqueue(_submittedFrames);
                generation = _generation;
            }

            if (_track is not null && !_queue.IsAddingCompleted)
            {
                try
                {
                    _queue.Add((generation, pcm));
                }
                catch (InvalidOperationException)
                {
                    // Completed between the check and the add: the voice is going away.
                }
            }
        }

        /// <summary>Source channels to the stereo track through the output matrix. Under <see cref="_gate"/>.</summary>
        private byte[] ToStereo(byte[] pcm)
        {
            if (Channels == 2 && _matrixIsDefault)
            {
                return pcm;
            }

            int frames = pcm.Length / (2 * Channels);
            byte[] output = new byte[frames * FrameBytes];
            for (int f = 0; f < frames; f++)
            {
                float left, right;
                if (Channels == 1)
                {
                    float s = BitConverter.ToInt16(pcm, f * 2);
                    left = s * _matrix[0];
                    right = s * _matrix[1];
                }
                else
                {
                    float l = BitConverter.ToInt16(pcm, f * 4);
                    float r = BitConverter.ToInt16(pcm, (f * 4) + 2);
                    left = (l * _matrix[0]) + (r * _matrix[1]);
                    right = (l * _matrix[2]) + (r * _matrix[3]);
                }

                short a = (short)Math.Clamp((int)MathF.Round(left), short.MinValue, short.MaxValue);
                short b = (short)Math.Clamp((int)MathF.Round(right), short.MinValue, short.MaxValue);
                int o = f * FrameBytes;
                output[o] = (byte)a;
                output[o + 1] = (byte)(a >> 8);
                output[o + 2] = (byte)b;
                output[o + 3] = (byte)(b >> 8);
            }

            return output;
        }

        private void Write()
        {
            try
            {
                foreach (var (generation, data) in _queue.GetConsumingEnumerable())
                {
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int written;
                        lock (_gate)
                        {
                            if (_disposed || generation != _generation)
                            {
                                break;   // flushed: this audio is no longer wanted
                            }

                            written = _track!.Write(data, offset, data.Length - offset, WriteMode.NonBlocking);
                        }

                        if (written < 0)
                        {
                            break;
                        }

                        offset += written;
                        if (written == 0)
                        {
                            Thread.Sleep(2);   // the track's buffer is full; it drains in real time
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AudioTrackSoundBackend.Log($"writer failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Play() => Locked(track =>
        {
            _wantPlaying = true;
            if (!_suspended)
            {
                track.Play();
            }
        });

        public void Pause() => Locked(track =>
        {
            _wantPlaying = false;
            track.Pause();
        });

        public void Flush() => Locked(track =>
        {
            ClearLocked(track);
            if (_wantPlaying && !_suspended)
            {
                track.Play();
            }
        });

        /// <summary>Drops everything queued. Stop and flush rewinds the playback head to 0, so the frame count starts again.</summary>
        private void ClearLocked(global::Android.Media.AudioTrack track)
        {
            long head = (uint)track.PlaybackHeadPosition;
            _sourceFramesPlayedBefore += (ulong)Math.Max(0, (long)(head * (SampleRate / (double)OutputRate)));
            _generation++;
            track.Pause();
            track.Flush();
            _ends.Clear();
            _submittedFrames = 0;
            _resampler?.Reset();
            PrimeWithSilence();
        }

        public void SetVolume(float volume) => Locked(track =>
        {
            _volume = volume;
            ApplyVolume(track);
        });

        public void SetMasterVolume(float master) => Locked(track =>
        {
            _master = master;
            ApplyVolume(track);
        });

        private void ApplyVolume(global::Android.Media.AudioTrack track) =>
            track.SetVolume(Math.Clamp(_volume * _master, 0f, 1f));

        public void SetFrequencyRatio(float ratio) => Locked(track =>
        {
            double factor = Math.Clamp((double)ratio, 0.25, 4.0);
            if (Math.Abs(factor - _pitch) < 1e-6)
            {
                return;
            }

            _pitch = factor;
            if (_resampler is not null)
            {
                _resampler.SetPitch(factor);
            }
            else
            {
                track.SetPlaybackRate((int)Math.Round(OutputRate * Math.Clamp(factor, 0.5, 2.0)));
            }
        });

        public void SetOutputMatrix(int sourceChannels, int destinationChannels, float[] levelMatrix)
        {
            if (sourceChannels != Channels || destinationChannels != 2 || levelMatrix.Length < sourceChannels * 2)
            {
                return;   // a mono or surround device: the default routing stands
            }

            lock (_gate)
            {
                // FAudio's layout is [destination * sourceChannels + source], which for the two
                // channel counts here is exactly this class's.
                for (int i = 0; i < sourceChannels * 2; i++)
                {
                    _matrix[i] = levelMatrix[i];
                }

                _matrixIsDefault = Channels == 2
                    ? _matrix[0] == 1f && _matrix[1] == 0f && _matrix[2] == 0f && _matrix[3] == 1f
                    : false;
            }
        }

        public void Suspend(bool suspended) => Locked(track =>
        {
            _suspended = suspended;
            if (suspended)
            {
                track.Pause();
            }
            else if (_wantPlaying)
            {
                track.Play();
            }
        });

        private void Locked(Action<global::Android.Media.AudioTrack> action)
        {
            lock (_gate)
            {
                if (_track is null || _disposed)
                {
                    return;
                }

                try
                {
                    action(_track);
                }
                catch (Exception ex)
                {
                    AudioTrackSoundBackend.Log($"track call failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _queue.CompleteAdding();
            _writer?.Join(500);
            try
            {
                _track?.Stop();
                _track?.Release();
            }
            catch (Exception ex)
            {
                AudioTrackSoundBackend.Log($"release failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
