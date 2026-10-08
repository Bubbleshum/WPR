#if ANDROID
using System.Collections.Concurrent;
using Android.Media;

namespace WPR.Wp8Native
{
    /// <summary>
    /// XAudio2's source voices played through Android's own <see cref="AudioTrack"/>, one per
    /// voice, instead of FAudio.
    /// </summary>
    /// <remarks>
    /// Through FAudio (<see cref="Wp8NativeAudio"/>) every WP8 title's sound was choppy on a
    /// phone while clean on the desktop: Android's output pulls in bigger, burstier periods, and
    /// a game keeping two ~30 ms buffers queued ran dry a few times a second. An AudioTrack in
    /// stream mode goes straight to the platform mixer, and "finished" is read off its playback
    /// head, so the game is paced by what has actually been heard. A cushion of extra queued
    /// audio was tried first and added audible lag; this needs none.
    /// <para>
    /// Writes are non-blocking, from one writer thread per voice: a blocking write into a paused
    /// or flushed track is the easy way to hang the guest.
    /// </para>
    /// </remarks>
    internal sealed class AudioTrackOutput : IHostAudio
    {
        private readonly Action<string>? _log;
        private readonly List<Voice> _voices = [];
        private int _retiredUnderruns;
        private bool _failed;
        private bool _suspended;

        public AudioTrackOutput(Action<string>? log) => _log = log;

        public int VoicesCreated { get; private set; }

        public int Underruns { get; private set; }

        public IAudioVoiceOutput CreateVoice(int sampleRate, int channels)
        {
            var voice = new Voice(this, sampleRate, channels);
            lock (_voices)
            {
                _voices.Add(voice);
                VoicesCreated++;
                if (_suspended)
                {
                    voice.Suspend(true);
                }
            }

            _log?.Invoke($"[wpr-wp8] audio: AudioTrack voice {sampleRate} Hz {channels}ch, buffer {voice.BufferFrames} frames");
            return voice;
        }

        public void Pump()
        {
            lock (_voices)
            {
                int total = _retiredUnderruns;
                foreach (Voice voice in _voices)
                {
                    total += voice.Underruns;
                }

                Underruns = total;
            }
        }

        public void Suspend(bool suspended)
        {
            lock (_voices)
            {
                _suspended = suspended;
                foreach (Voice voice in _voices)
                {
                    voice.Suspend(suspended);
                }
            }
        }

        public void Dispose()
        {
            lock (_voices)
            {
                foreach (Voice voice in _voices.ToArray())
                {
                    voice.Dispose();
                }
            }
        }

        private void Failed(Exception ex)
        {
            // An audio failure costs the sound, never the game. Said once.
            if (!_failed)
            {
                _failed = true;
                _log?.Invoke($"[wpr-wp8] AudioTrack failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Retire(Voice voice, int underruns)
        {
            lock (_voices)
            {
                if (_voices.Remove(voice))
                {
                    _retiredUnderruns += underruns;
                }
            }
        }

        private sealed class Voice : IAudioVoiceOutput
        {
            private readonly AudioTrackOutput _owner;
            private readonly int _frameBytes;
            private readonly AudioTrack? _track;
            private readonly int _sampleRate;

            /// <summary>Guards the track, <see cref="_ends"/> and the counters below.</summary>
            private readonly object _gate = new();

            /// <summary>The frame each still-playing buffer ends at, counted since the last flush.</summary>
            private readonly Queue<long> _ends = new();
            private long _submittedFrames;
            private int _completed;
            private int _generation;
            private bool _wantPlaying;
            private bool _suspended;
            private bool _disposed;

            private readonly BlockingCollection<(int Generation, byte[] Data)> _queue = new();
            private readonly Thread? _writer;

            public Voice(AudioTrackOutput owner, int sampleRate, int channels)
            {
                _owner = owner;
                _sampleRate = sampleRate;
                _frameBytes = 2 * channels;
                try
                {
                    ChannelOut mask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
                    int minimum = AudioTrack.GetMinBufferSize(sampleRate, mask, Encoding.Pcm16bit);
                    var builder = new AudioTrack.Builder()
                        .SetAudioAttributes(new AudioAttributes.Builder()
                            .SetUsage(AudioUsageKind.Game)!
                            .SetContentType(AudioContentType.Music)!
                            .Build()!)
                        .SetAudioFormat(new AudioFormat.Builder()
                            .SetEncoding(Encoding.Pcm16bit)!
                            .SetSampleRate(sampleRate)!
                            .SetChannelMask(mask)!
                            .Build()!)
                        .SetTransferMode(AudioTrackMode.Stream)
                        .SetBufferSizeInBytes(Math.Max(minimum, 0) * 2);
                    if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    {
                        builder.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency);
                    }

                    _track = builder.Build();
                    BufferFrames = _track.BufferSizeInFrames;

                    // A streaming track does not start until its whole buffer is full, and a game
                    // keeps far less than that queued (MC4: two 25 ms buffers against ~200 ms), so it
                    // never started, the head never moved, no buffer ever finished and the game's
                    // audio stopped after its first two. Start after 10 ms instead.
                    // Below API 31 there is no threshold to lower, so the buffer is primed with
                    // silence instead (again after every flush, which empties it).
                    if (OperatingSystem.IsAndroidVersionAtLeast(31))
                    {
                        _track.SetStartThresholdInFrames(Math.Clamp(sampleRate / 100, 1, BufferFrames));
                    }
                    else
                    {
                        PrimeWithSilence();
                    }
                    _writer = new Thread(Write) { Name = "WP8 AudioTrack writer", IsBackground = true };
                    _writer.Start();
                }
                catch (Exception ex)
                {
                    _owner.Failed(ex);
                    _track = null;
                }
            }

            public int BufferFrames { get; }

            /// <summary>Fills the empty buffer with silence so the track starts (pre-API-31 only). Under <see cref="_gate"/>.</summary>
            private void PrimeWithSilence()
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31) || _track is null)
                {
                    return;
                }

                byte[] silence = new byte[BufferFrames * _frameBytes];
                int written = _track.Write(silence, 0, silence.Length, WriteMode.NonBlocking);
                _submittedFrames += Math.Max(0, written) / _frameBytes;
            }

            public int Underruns
            {
                get
                {
                    lock (_gate)
                    {
                        return _track is { } track && !_disposed ? track.UnderrunCount : 0;
                    }
                }
            }

            /// <summary>Buffers whose last frame the playback head has passed, or that were flushed.</summary>
            public int Completed
            {
                get
                {
                    lock (_gate)
                    {
                        if (_track is { } track && !_disposed)
                        {
                            long head = (uint)track.PlaybackHeadPosition;
                            while (_ends.Count > 0 && _ends.Peek() <= head)
                            {
                                _ends.Dequeue();
                                _completed++;
                            }
                        }
                        else
                        {
                            // No track: nothing can play, so nothing may wait on it.
                            _completed += _ends.Count;
                            _ends.Clear();
                        }

                        return _completed;
                    }
                }
            }

            public void Submit(byte[] pcm)
            {
                int generation;
                lock (_gate)
                {
                    _submittedFrames += pcm.Length / _frameBytes;
                    _ends.Enqueue(_submittedFrames);
                    generation = _generation;
                }

                if (_track is not null && !_queue.IsAddingCompleted)
                {
                    _queue.Add((generation, pcm));
                }
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
                    _owner.Failed(ex);
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
                // Stop and flush rewinds the playback head to 0, so the frame count starts again.
                _generation++;
                track.Pause();
                track.Flush();
                _completed += _ends.Count;
                _ends.Clear();
                _submittedFrames = 0;
                PrimeWithSilence();
                if (_wantPlaying && !_suspended)
                {
                    track.Play();
                }
            });

            public void SetVolume(float volume) => Locked(track => track.SetVolume(Math.Clamp(volume, 0f, 1f)));

            public void SetPitch(float octaves) => Locked(track =>
                track.SetPlaybackRate((int)Math.Round(_sampleRate * Math.Pow(2, Math.Clamp(octaves, -1f, 1f)))));

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

            private void Locked(Action<AudioTrack> action)
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
                        _owner.Failed(ex);
                    }
                }
            }

            public void Dispose()
            {
                int underruns = Underruns;
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
                _owner.Retire(this, underruns);
                try
                {
                    _track?.Stop();
                    _track?.Release();
                }
                catch (Exception ex)
                {
                    _owner.Failed(ex);
                }
            }
        }
    }
}
#endif
