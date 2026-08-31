namespace WPR.Abstractions.Audio
{
    /// <summary>
    /// Transcodes a media file to Ogg Vorbis, so <c>Microsoft.Xna.Framework.Media.MediaPlayer</c>
    /// can play it.
    ///
    /// <para><b>Why this seam exists.</b> WP7 XNA titles ship their soundtracks as <c>.wma</c>
    /// (Mirror's Edge alone has 40+ tracks under <c>Content/music/</c>), but the song backend
    /// decodes Ogg Vorbis only — FAudio's <c>XNA_PlaySong</c> is stb_vorbis. The install pipeline
    /// therefore transcodes every <c>.wma</c> up front (see <c>WPR.Core.AudioCompabilityConverter</c>),
    /// and <em>how</em> you run a transcoder is entirely platform-specific: the Windows head shells
    /// out to a bundled <c>ffmpeg.exe</c> via FFMpegCore, while Android has no executable to spawn
    /// and must go through FFmpegKit's JNI entry point instead.</para>
    ///
    /// <para>Before this contract existed, <c>AudioCompabilityConverter</c> called FFMpegCore
    /// directly and was commented "implementation for all platforms". It was not: on Android the
    /// process launch failed, the exception was swallowed per file, the install still reported
    /// success, and every game was silently mute. That is the exact shape of bug
    /// <c>ISensorProvider</c> was introduced to fix for motion input — a desktop-only
    /// implementation sitting on a shared Core project — so it gets the same treatment.</para>
    ///
    /// <para>The vocabulary here is deliberately just file paths: nothing about a transcode needs
    /// an audio type, so this contract stays in Abstractions rather than reaching for the XNA
    /// <c>Song</c> vocabulary and creating the cycle that put <c>IAchievementStore</c> in
    /// <c>WPR.Framework.Xna</c>.</para>
    /// </summary>
    public interface IAudioTranscoder
    {
        /// <summary>
        /// Short human-readable name of the backing tool, for install logs
        /// (e.g. <c>"FFMpegCore (ffmpeg.exe)"</c>, <c>"FFmpegKit"</c>).
        /// </summary>
        string Name { get; }

        /// <summary>
        /// True when this transcoder can actually run right now — the bundled <c>ffmpeg.exe</c> is
        /// present, the JNI library loaded, and so on.
        ///
        /// <para>Checked <b>once, before</b> the conversion loop starts. A transcoder that reports
        /// false fails the install outright rather than producing one warning per track and a mute
        /// game, which is the failure mode this whole seam was built to end.</para>
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Transcodes <paramref name="inputPath"/> to Ogg Vorbis at <paramref name="outputPath"/>,
        /// overwriting the output if it exists.
        ///
        /// <para>Implementations report failure through the returned
        /// <see cref="AudioTranscodeResult"/> rather than throwing: a single unconvertible track is
        /// a per-file warning, not an install-killer, and the caller needs the tool's own error
        /// text to put in the log. Genuinely exceptional conditions (the tool vanishing mid-run)
        /// may still throw.</para>
        /// </summary>
        Task<AudioTranscodeResult> TranscodeToOggVorbisAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Outcome of one <see cref="IAudioTranscoder.TranscodeToOggVorbisAsync"/> call. A primitive
    /// DTO, per this project's no-dependencies rule.
    /// </summary>
    public readonly struct AudioTranscodeResult
    {
        private AudioTranscodeResult(bool success, string? error)
        {
            Success = success;
            Error = error;
        }

        /// <summary>True when <c>outputPath</c> now holds a playable Ogg Vorbis file.</summary>
        public bool Success { get; }

        /// <summary>
        /// Why the transcode failed, for the install log. Null on success. Carries the tool's own
        /// diagnostics where available (ffmpeg's stderr tail, FFmpegKit's session logs) — without
        /// it a failure is indistinguishable from "the file wasn't WMA after all".
        /// </summary>
        public string? Error { get; }

        public static AudioTranscodeResult Succeeded() => new(true, null);

        public static AudioTranscodeResult Failed(string error) => new(false, error);
    }
}
