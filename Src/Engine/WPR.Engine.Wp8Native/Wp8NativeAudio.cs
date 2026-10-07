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
    /// The runtime calls in on the guest thread; everything that touches an instance is queued and
    /// run on the XNA thread by <see cref="Pump"/>, which Wp8NativeGame calls every Update. The
    /// one thing that flows back, how many buffers each voice has finished, is published as a
    /// plain int the guest reads. A buffer therefore completes up to a frame late, which costs
    /// nothing while the runtime keeps three queued per voice.
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
            _commands.Enqueue(voice.Create);
            return voice;
        }

        /// <summary>Runs queued work and publishes completion counts. XNA thread only.</summary>
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

            foreach (Voice voice in _voices)
            {
                voice.Publish();
            }
        }

        public void Dispose()
        {
            Pump();
            foreach (Voice voice in _voices)
            {
                voice.Release();
            }

            _voices.Clear();
        }

        private sealed class Voice(Wp8NativeAudio owner, int sampleRate, int channels) : IAudioVoiceOutput
        {
            private DynamicSoundEffectInstance? _instance;
            private int _executed;
            private int _completed;
            private bool _disposed;

            public int Completed => Volatile.Read(ref _completed);

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

            public void Publish()
            {
                if (_instance is { } instance)
                {
                    int pending = instance.PendingBufferCount;
                    Volatile.Write(ref _completed, _executed - pending);
                    if (pending == 0 && _executed > 0 && instance.State == SoundState.Playing)
                    {
                        owner.Underruns++;
                    }
                }
            }

            private void Run(Action<DynamicSoundEffectInstance> action) => owner._commands.Enqueue(() =>
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

                Publish();
            });

            public void SetVolume(float volume) => Run(instance => instance.Volume = Math.Clamp(volume, 0f, 1f));

            public void SetPitch(float octaves) => Run(instance => instance.Pitch = Math.Clamp(octaves, -1f, 1f));

            public void Dispose() => owner._commands.Enqueue(() =>
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
