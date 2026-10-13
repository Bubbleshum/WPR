namespace WPR.Wp8Native
{
    /// <summary>
    /// What <see cref="Wp8NativeGame"/> plays XAudio2's voices through, plus the bookkeeping it
    /// reports. FAudio (<see cref="Wp8NativeAudio"/>) on the desktop; Android's own
    /// <c>AudioTrack</c> (<see cref="AudioTrackOutput"/>) on a phone.
    /// </summary>
    /// <remarks>
    /// Android keeps AudioTrack even now FAudio has a low-latency AAudio output there
    /// (2026-10-13), because it still measured better: Angry Birds Rio on the emulator, 50 s of
    /// play, 22-43 dry spells through AudioTrack against 56-135 through FAudio over AAudio (and
    /// ~170 over SDL's device, the old FAudio path). The game appears to fill its voice on its
    /// own clock, so what matters is how far ahead of the speaker the output consumes, and a
    /// track per voice consumes closest.
    /// </remarks>
    internal interface IHostAudio : IAudioOutput, IDisposable
    {
        int VoicesCreated { get; }

        /// <summary>Times a playing voice ran dry - an audible gap.</summary>
        int Underruns { get; }

        /// <summary>Once per XNA update, on the XNA thread.</summary>
        void Pump();

        /// <summary>The app went to the background (true) or came back (false).</summary>
        void Suspend(bool suspended);

        /// <summary>The output for this platform.</summary>
        static IHostAudio Create(Action<string>? log)
        {
#if ANDROID
            return new AudioTrackOutput(log);
#else
            return new Wp8NativeAudio(log);
#endif
        }
    }
}
