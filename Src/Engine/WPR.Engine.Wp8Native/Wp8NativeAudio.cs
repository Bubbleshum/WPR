using System.Collections.Concurrent;
using Microsoft.Xna.Framework.Audio;

namespace WPR.Wp8Native
{
    /// <summary>
    /// XAudio2's source voices, played as <see cref="DynamicSoundEffectInstance"/>s - so a WP8
    /// native title's sound goes through the same audio stack as every XNA title (FAudio on both
    /// heads).
    /// </summary>
    /// <remarks>
    /// The runtime calls in on the guest thread, and the instance is driven right there, under
    /// <see cref="DynamicSoundEffectInstance.Streams"/> - the lock the XNA thread's
    /// FrameworkDispatcher holds while it updates the same instances. How many buffers a voice
    /// has finished is read live from FAudio. This used to be queued to the XNA thread and
    /// published back once a frame, which put two frames into every trip from "buffer finished"
    /// to "next buffer queued": with OnBufferEnd, the game's own mixer and the next pump on top,
    /// a game double-buffering ~40 ms chunks ran dry every cycle - choppy sound in every title.
    /// </remarks>
    internal sealed class Wp8NativeAudio : IAudioOutput, IDisposable
    {
        private readonly ConcurrentQueue<Action> _commands = new();
        private readonly List<Voice> _voices = [];
        private readonly Action<string>? _log;
        private bool _failed;

        public Wp8NativeAudio(Action<string>? log) => _log = log;

        public int VoicesCreated { get; private set; }

        /// <summary>Updates on which a playing voice had nothing queued - an audible gap.</summary>
        public int Underruns { get; private set; }

        public IAudioVoiceOutput CreateVoice(int sampleRate, int channels)
        {
            Voice voice = new(this, sampleRate, channels);
            Execute(voice.Create);
            return voice;
        }

        /// <summary>Runs <paramref name="command"/> now, serialised with the XNA thread's audio update.</summary>
        private void Execute(Action command)
        {
            try
            {
                lock (DynamicSoundEffectInstance.Streams)
                {
                    command();
                }
            }
            catch (Exception ex)
            {
                // An audio failure costs the sound, never the game. Said once.
                if (!_failed)
                {
                    _failed = true;
                    _log?.Invoke($"[wpr-wp8] audio command failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>Runs anything queued and counts underruns. XNA thread.</summary>
        public void Pump()
        {
            while (_commands.TryDequeue(out Action? command))
            {
                try
                {
                    command();
                }
                catch (Exception ex)
                {
                    // An audio failure costs the sound, never the game. Said once.
                    if (!_failed)
                    {
                        _failed = true;
                        _log?.Invoke($"[wpr-wp8] audio command failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            lock (DynamicSoundEffectInstance.Streams)
            {
                foreach (Voice voice in _voices)
                {
                    voice.CountUnderrun();
                }
            }
        }

        public void Dispose()
        {
            Pump();
            lock (DynamicSoundEffectInstance.Streams)
            {
                foreach (Voice voice in _voices)
                {
                    voice.Release();
                }

                _voices.Clear();
            }
        }

        private sealed class Voice(Wp8NativeAudio owner, int sampleRate, int channels) : IAudioVoiceOutput
        {
            private DynamicSoundEffectInstance? _instance;
            private int _executed;
            private int _completed;
            private bool _disposed;

            /// <summary>Buffers FAudio has finished playing, read live (guest thread).</summary>
            public int Completed
            {
                get
                {
                    lock (DynamicSoundEffectInstance.Streams)
                    {
                        if (_instance is { } instance && !_disposed)
                        {
                            instance.Update();   // drops finished buffers from PendingBufferCount
                            _completed = _executed - instance.PendingBufferCount;
                        }

                        return _completed;
                    }
                }
            }

            public void Create()
            {
                if (_disposed)
                {
                    return;
                }

                _instance = new DynamicSoundEffectInstance(sampleRate, channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
                owner._voices.Add(this);
                owner.VoicesCreated++;
            }

            /// <summary>Counts a frame on which a playing voice had nothing left queued.</summary>
            public void CountUnderrun()
            {
                if (_instance is { } instance && !_disposed &&
                    instance.PendingBufferCount == 0 && _executed > 0 && instance.State == SoundState.Playing)
                {
                    owner.Underruns++;
                }
            }

            private void Run(Action<DynamicSoundEffectInstance> action) => owner.Execute(() =>
            {
                if (_instance is { } instance && !_disposed)
                {
                    action(instance);
                }
            });

            public void Submit(byte[] pcm) => Run(instance =>
            {
                instance.SubmitBuffer(pcm);
                _executed++;
                _dump?.Write(pcm);
            });

            /// <summary>WPR_WP8_AUDIO_DUMP=&lt;folder&gt;: what each voice played, as raw PCM (diagnostic).</summary>
            private readonly FileStream? _dump = Environment.GetEnvironmentVariable("WPR_WP8_AUDIO_DUMP") is { Length: > 0 } folder
                ? File.Create(Path.Combine(folder, $"voice-{Interlocked.Increment(ref _dumpCount)}-{sampleRate}Hz-{channels}ch.pcm"))
                : null;

            private static int _dumpCount;

            public void Play() => Run(instance => instance.Play());

            public void Pause() => Run(instance => instance.Pause());

            public void Flush() => Run(instance =>
            {
                bool playing = instance.State == SoundState.Playing;
                instance.Stop(true);
                if (playing)
                {
                    instance.Play();
                }

                _completed = _executed - instance.PendingBufferCount;
            });

            public void SetVolume(float volume) => Run(instance => instance.Volume = Math.Clamp(volume, 0f, 1f));

            public void SetPitch(float octaves) => Run(instance => instance.Pitch = Math.Clamp(octaves, -1f, 1f));

            public void Dispose() => owner.Execute(() =>
            {
                Release();
                owner._voices.Remove(this);
            });

            public void Release()
            {
                _disposed = true;
                _dump?.Dispose();
                _instance?.Dispose();
                _instance = null;
            }
        }
    }
}
