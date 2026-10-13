#nullable enable
using System.Runtime.InteropServices;
using WPR.Engine.Audio;
using Trace = System.Diagnostics.Trace;

namespace WPR.Audio.AudioTrack
{
    /// <summary>
    /// <see cref="IAudioBackend"/> that plays streaming voices through <see cref="AudioTrackVoice"/>
    /// and hands every other call to the backend below it.
    /// </summary>
    /// <remarks>
    /// <para><b>Which voices are ours.</b> The seam does not say what kind of instance asked for a
    /// voice, but the framework does it in a fixed way: a <c>DynamicSoundEffectInstance</c> creates
    /// its voice through <see cref="CreateSourceVoice"/> with its own format, and a static
    /// <c>SoundEffectInstance</c> through <see cref="CreateSourceVoiceRaw"/> with the effect's
    /// marshalled blob. So <see cref="CreateSourceVoice"/> with 16-bit PCM is the claim; float voices
    /// (the movie player's <c>SubmitFloatBufferEXT</c>) and anything raw stay below.</para>
    /// <para><b>Handles.</b> One byte of unmanaged memory per voice: a pointer no live FAudio voice
    /// can share, so a call is routed by looking the handle up and forwarding it when it is not
    /// ours.</para>
    /// <para><b>Tracks are pooled.</b> A WP8 title's XAudio2 flush and an XNA title's
    /// <c>Stop()</c> + <c>Play()</c> both destroy the voice and make a new one, which here would be a
    /// new AudioTrack and writer thread every time. A destroyed voice is paused, flushed and kept
    /// for the next voice with the same rate and channel count.</para>
    /// <para>Not supported on these voices, and said once in the log: reverb sends and filters. The
    /// output matrix (pan, 3D) is applied in software to a stereo track.</para>
    /// </remarks>
    internal sealed class AudioTrackSoundBackend : IAudioBackend, IBackgroundAwareAudio
    {
        private const int MaxIdlePerFormat = 2;
        private const int MaxIdleTotal = 8;

        private readonly IAudioBackend _next;
        private readonly AudioTrackScope _scope;
        private readonly object _gate = new();
        private readonly Dictionary<IntPtr, AudioTrackVoice> _voices = new();
        private readonly List<AudioTrackVoice> _idle = new();
        private bool _suspended;
        private float _master = 1f;
        private int _voicesCreated;
        private int _tracksCreated;
        private long _retiredUnderruns;
        private bool _reportedReverb;
        private bool _reportedFilter;
        private bool _loggedFirst;

        public AudioTrackSoundBackend(IAudioBackend next, AudioTrackScope scope)
        {
            _next = next;
            _scope = scope;
        }

        internal static void Log(string message) => Trace.WriteLine("[wpr-audiotrack] " + message);

        private bool TryGet(IntPtr handle, out AudioTrackVoice voice)
        {
            lock (_gate)
            {
                return _voices.TryGetValue(handle, out voice!);
            }
        }

        /// <summary>Underruns over every track this launch, live ones included.</summary>
        public long Underruns
        {
            get
            {
                lock (_gate)
                {
                    long total = _retiredUnderruns;
                    foreach (AudioTrackVoice voice in _voices.Values)
                    {
                        total += voice.Underruns;
                    }

                    return total;
                }
            }
        }

        // ---- Device: forwarded ----

        public bool TryCreateDevice(float speedOfSound, out AudioDeviceInfo info) => _next.TryCreateDevice(speedOfSound, out info);

        public void DestroyDevice()
        {
            AudioTrackVoice[] all;
            lock (_gate)
            {
                all = [.. _voices.Values, .. _idle];
                foreach (AudioTrackVoice voice in _voices.Values)
                {
                    _retiredUnderruns += voice.Underruns;
                }

                _voices.Clear();
                _idle.Clear();
            }

            foreach (AudioTrackVoice voice in all)
            {
                voice.Dispose();
            }

            if (_voicesCreated > 0)
            {
                Log($"device closed: {_voicesCreated} voice(s) on {_tracksCreated} track(s), underruns={_retiredUnderruns}");
            }

            _next.DestroyDevice();
        }

        public bool HasDevice => _next.HasDevice;

        public void SetSpeedOfSound(float speedOfSound) => _next.SetSpeedOfSound(speedOfSound);

        public float GetMasterVolume() => _next.GetMasterVolume();

        public void SetMasterVolume(float volume)
        {
            _next.SetMasterVolume(volume);

            // The tracks are not under FAudio's mastering voice, so SoundEffect.MasterVolume is
            // applied to each of them here.
            lock (_gate)
            {
                _master = volume;
                foreach (AudioTrackVoice voice in _voices.Values)
                {
                    voice.SetMasterVolume(volume);
                }
            }
        }

        public void AttachReverb(IntPtr voice)
        {
            if (TryGet(voice, out _))
            {
                if (!_reportedReverb)
                {
                    _reportedReverb = true;
                    Log("reverb send requested on a streaming voice; not supported on AudioTrack, ignored.");
                }

                return;
            }

            _next.AttachReverb(voice);
        }

        // ---- Voices ----

        private bool Claims(int formatTag, int channels, int bitsPerSample)
        {
            if (formatTag != 1 || bitsPerSample != 16 || channels is < 1 or > 2)
            {
                return false;
            }

            return _scope == AudioTrackScope.AllDynamicVoices || AudioBackendRegistry.LowLatencyStreaming;
        }

        public IntPtr CreateSourceVoice(
            int formatTag, int channels, int sampleRate, int avgBytesPerSec,
            int blockAlign, int bitsPerSample, int cbSize,
            bool useFilter, float maxFrequencyRatio)
        {
            if (!Claims(formatTag, channels, bitsPerSample))
            {
                return _next.CreateSourceVoice(formatTag, channels, sampleRate, avgBytesPerSec, blockAlign, bitsPerSample, cbSize, useFilter, maxFrequencyRatio);
            }

            AudioTrackVoice? voice = null;
            bool suspended;
            float master;
            lock (_gate)
            {
                suspended = _suspended;
                master = _master;
                for (int i = 0; i < _idle.Count; i++)
                {
                    if (_idle[i].SampleRate == sampleRate && _idle[i].Channels == channels)
                    {
                        voice = _idle[i];
                        _idle.RemoveAt(i);
                        break;
                    }
                }
            }

            bool fresh = voice is null;
            try
            {
                voice ??= new AudioTrackVoice(sampleRate, channels);
            }
            catch (Exception ex)
            {
                // No track, no claim: the voice goes to the module below rather than playing nothing.
                Log($"cannot create a track for {sampleRate} Hz {channels}ch ({ex.GetType().Name}: {ex.Message}); this voice stays on the module below.");
                return _next.CreateSourceVoice(formatTag, channels, sampleRate, avgBytesPerSec, blockAlign, bitsPerSample, cbSize, useFilter, maxFrequencyRatio);
            }

            voice.Begin(master, suspended);
            IntPtr handle = Marshal.AllocHGlobal(1);
            lock (_gate)
            {
                _voices[handle] = voice;
                _voicesCreated++;
                if (fresh)
                {
                    _tracksCreated++;
                }
            }

            if (fresh && (!_loggedFirst || _tracksCreated <= 8))
            {
                _loggedFirst = true;
                Log($"voice {sampleRate} Hz {channels}ch, played at {voice.OutputRate} Hz stereo, buffer {voice.BufferFrames} frames");
            }

            return handle;
        }

        public IntPtr CreateSourceVoiceRaw(IntPtr formatBlob, bool useFilter, float maxFrequencyRatio) =>
            _next.CreateSourceVoiceRaw(formatBlob, useFilter, maxFrequencyRatio);

        public void DestroyVoice(IntPtr voice)
        {
            AudioTrackVoice? ours;
            bool keep = false;
            lock (_gate)
            {
                if (!_voices.Remove(voice, out ours))
                {
                    ours = null;
                }
                else
                {
                    _retiredUnderruns += ours.Underruns;
                    int sameFormat = 0;
                    foreach (AudioTrackVoice idle in _idle)
                    {
                        if (idle.SampleRate == ours.SampleRate && idle.Channels == ours.Channels)
                        {
                            sameFormat++;
                        }
                    }

                    keep = ours.Usable && sameFormat < MaxIdlePerFormat && _idle.Count < MaxIdleTotal;
                }
            }

            if (ours is null)
            {
                _next.DestroyVoice(voice);
                return;
            }

            Marshal.FreeHGlobal(voice);
            if (keep)
            {
                ours.Park();
                lock (_gate)
                {
                    _idle.Add(ours);
                }
            }
            else
            {
                ours.Dispose();
            }
        }

        public void SubmitBuffer(IntPtr voice, in AudioBufferDesc buffer)
        {
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                ours.Submit(buffer);
                return;
            }

            _next.SubmitBuffer(voice, in buffer);
        }

        public uint EndOfStreamFlag => _next.EndOfStreamFlag;

        public void Start(IntPtr voice)
        {
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                ours.Play();
                return;
            }

            _next.Start(voice);
        }

        public void Stop(IntPtr voice, bool immediate)
        {
            // XNA's Pause is Stop(immediate) with the buffers kept, resumed by Start, which is a
            // pause here. A dynamic instance never stops "as authored" (it throws first).
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                ours.Pause();
                return;
            }

            _next.Stop(voice, immediate);
        }

        public void FlushSourceBuffers(IntPtr voice)
        {
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                ours.Flush();
                return;
            }

            _next.FlushSourceBuffers(voice);
        }

        public void ExitLoop(IntPtr voice)
        {
            if (TryGet(voice, out _))
            {
                return;   // streaming voices never loop
            }

            _next.ExitLoop(voice);
        }

        public void GetVoiceState(IntPtr voice, bool samplesPlayedNotNeeded, out int buffersQueued, out ulong samplesPlayed)
        {
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                ours.GetState(out buffersQueued, out samplesPlayed);
                return;
            }

            _next.GetVoiceState(voice, samplesPlayedNotNeeded, out buffersQueued, out samplesPlayed);
        }

        public void SetVoiceVolume(IntPtr voice, float volume)
        {
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                ours.SetVolume(volume);
                return;
            }

            _next.SetVoiceVolume(voice, volume);
        }

        public void SetFrequencyRatio(IntPtr voice, float ratio)
        {
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                ours.SetFrequencyRatio(ratio);
                return;
            }

            _next.SetFrequencyRatio(voice, ratio);
        }

        public void SetOutputMatrix(IntPtr voice, AudioOutputTarget target, int sourceChannels, int destinationChannels, float[] levelMatrix)
        {
            if (TryGet(voice, out AudioTrackVoice ours))
            {
                if (target == AudioOutputTarget.Master)
                {
                    ours.SetOutputMatrix(sourceChannels, destinationChannels, levelMatrix);
                }

                return;   // reverb sends: see AttachReverb
            }

            _next.SetOutputMatrix(voice, target, sourceChannels, destinationChannels, levelMatrix);
        }

        public void SetFilter(IntPtr voice, AudioFilterType type, float frequency, float oneOverQ)
        {
            if (TryGet(voice, out _))
            {
                if (!_reportedFilter)
                {
                    _reportedFilter = true;
                    Log($"{type} filter requested on a streaming voice; not supported on AudioTrack, ignored.");
                }

                return;
            }

            _next.SetFilter(voice, type, frequency, oneOverQ);
        }

        // ---- Forwarded ----

        public void Calculate3D(in Audio3DParams p, float[] matrixCoefficients, out float dopplerFactor) =>
            _next.Calculate3D(in p, matrixCoefficients, out dopplerFactor);

        public MicrophoneInfo[] GetMicrophones() => _next.GetMicrophones();

        public int GetMicrophoneSamples(uint handle, byte[] buffer, int offset, int count) =>
            _next.GetMicrophoneSamples(handle, buffer, offset, count);

        public int GetMicrophoneQueuedBytes(uint handle) => _next.GetMicrophoneQueuedBytes(handle);

        public void StartMicrophone(uint handle) => _next.StartMicrophone(handle);

        public void StopMicrophone(uint handle) => _next.StopMicrophone(handle);

        // ---- Background ----

        /// <summary>
        /// SDL pauses its own output device when the activity pauses, which silences FAudio, but
        /// these tracks are ours and would play on over the home screen.
        /// </summary>
        public void SuspendForBackground()
        {
            lock (_gate)
            {
                _suspended = true;
                foreach (AudioTrackVoice voice in _voices.Values)
                {
                    voice.Suspend(true);
                }
            }

            Log($"suspended for background (underruns so far: {Underruns})");
            (_next as IBackgroundAwareAudio)?.SuspendForBackground();
        }

        public void RestoreFromForeground()
        {
            lock (_gate)
            {
                _suspended = false;
                foreach (AudioTrackVoice voice in _voices.Values)
                {
                    voice.Suspend(false);
                }
            }

            (_next as IBackgroundAwareAudio)?.RestoreFromForeground();
        }
    }
}
