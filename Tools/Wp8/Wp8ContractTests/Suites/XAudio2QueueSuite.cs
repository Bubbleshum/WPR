using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The XAudio2 source-voice queue: buffers are handed to the host in ~20 ms slices, so what is
/// still to play (and so OnVoiceProcessingPassStart's BytesRequired) is known while a long buffer
/// plays. Mandreel's mixer (Fruit Ninja 2013, Jetpack Joyride) submits only from that callback; it
/// used to be raised never, and the voice stayed silent, then with whole-buffer accounting it was
/// asked for more only once the host had run dry, a gap at every ~240 ms buffer.
/// </summary>
public sealed class XAudio2QueueSuite : IContractSuite
{
    public string Name => "xaudio2 queue";

    private sealed class FakeOutput : IAudioVoiceOutput
    {
        public readonly List<int> Submitted = [];
        public int Completed { get; set; }
        public void Submit(byte[] pcm) => Submitted.Add(pcm.Length);
        public void Play() { }
        public void Pause() { }
        public void Flush() => Completed = Submitted.Count;
        public void SetVolume(float volume) { }
        public void SetPitch(float octaves) { }
        public void Dispose() { }
    }

    public void Run(Harness h)
    {
        XAudio2Format pcm = new(1, 2, 44100, 16, 4, 0, []);
        FakeOutput output = new();
        XAudio2BufferQueue queue = new(pcm, output);
        List<(int Slot, long Context)> callbacks = [];

        h.Check("an empty voice asks for its lead in bytes", queue.BytesRequired(0.1) == 4410 * 4,
            $"{queue.BytesRequired(0.1)}");

        // One 240 ms buffer (10584 frames), as Mandreel submits.
        queue.Enqueue(0x40, new byte[10584 * 4], 0, 0, 0, 0, 0, 7);
        queue.Feed((s, c) => callbacks.Add((s, c)));
        h.Check("a long buffer reaches the host in ~20 ms slices",
            output.Submitted.Count >= 3 && output.Submitted.All(b => b <= 882 * 4 * 3 / 2) && output.Submitted.Sum() == 10584 * 4,
            string.Join(",", output.Submitted));
        h.Check("OnBufferStart once, with its context", callbacks.Count == 1 && callbacks[0] == (XAudio2BufferQueue.OnBufferStart, 7));
        h.Check("while it is all still to play, nothing is required", queue.BytesRequired(0.1) == 0 && queue.QueuedFrames == 10584);

        // The host finishes all but the last ~60 ms.
        output.Completed = output.Submitted.Count - 3;
        long left = queue.QueuedFrames;
        h.Check("what has played is subtracted slice by slice", left > 0 && left < 10584 && left <= 882 * 3 + 441, $"{left}");
        h.Check("... so more is asked for before the buffer has ended", queue.BytesRequired(0.1) > 0);

        callbacks.Clear();
        queue.Retire((s, c) => callbacks.Add((s, c)));
        h.Check("the buffer has not ended while slices of it still play", callbacks.Count == 0 && queue.Count == 1);

        output.Completed = output.Submitted.Count;
        queue.Retire((s, c) => callbacks.Add((s, c)));
        h.Check("it ends once the last slice has played: OnBufferEnd then OnStreamEnd",
            callbacks.SequenceEqual([(XAudio2BufferQueue.OnBufferEnd, 7L), (XAudio2BufferQueue.OnStreamEnd, 0L)]) && queue.Count == 0,
            string.Join(",", callbacks));
        h.Check("SamplesPlayed counts the whole buffer", queue.SamplesPlayed == 10584);

        // A short buffer is one slice; ADPCM asks in whole blocks.
        FakeOutput small = new();
        XAudio2BufferQueue shortQueue = new(pcm, small);
        shortQueue.Enqueue(0, new byte[256 * 4], 0, 0, 0, 0, 0, 1);
        shortQueue.Feed((_, _) => { });
        h.Check("a short buffer goes over whole", small.Submitted.SequenceEqual([256 * 4]));

        XAudio2Format adpcm = new(2, 1, 22050, 4, 512, 1012, []);
        XAudio2BufferQueue adpcmQueue = new(adpcm, new FakeOutput());
        h.Check("ADPCM's BytesRequired is whole blocks", adpcmQueue.BytesRequired(0.1) % 512 == 0 && adpcmQueue.BytesRequired(0.1) >= 2205 / 1012 * 512);

        // An infinite loop never asks for more.
        XAudio2BufferQueue loop = new(pcm, new FakeOutput());
        loop.Enqueue(0, new byte[4410 * 4], 0, 0, 0, 0, XAudio2BufferQueue.InfiniteLoop, 2);
        loop.Feed((_, _) => { });
        h.Check("a voice looping for ever requires nothing", loop.BytesRequired(0.1) == 0);
    }
}
