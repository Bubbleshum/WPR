using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFMpegCore;
using WPR.Engine.Audio;

namespace WPR.Platform.Windows.Audio
{
    /// <summary>
    /// Windows <see cref="IAudioTranscoder"/> — FFMpegCore over the <c>ffmpeg.exe</c> this head
    /// ships next to the executable (see the <c>None Update="ffmpeg.exe"</c> item in
    /// <c>WPR.Platform.Windows.csproj</c>, <c>CopyToOutputDirectory=Always</c>).
    ///
    /// <para><b>This used to live in <c>WPR.Loader</c></b>, calling FFMpegCore directly from
    /// <c>AudioCompabilityConverter</c> under a comment claiming it was the implementation "for all
    /// platforms". FFMpegCore spawns an <c>ffmpeg</c> <em>process</em>, so it never worked on
    /// Android and every WP7 <c>.wma</c> soundtrack was silently left untranscoded there. Moving it
    /// into the head is the same correction the sensors got: a desktop-only implementation does not
    /// belong on a shared Core project, where it also gets shipped inside the APK.</para>
    /// </summary>
    public sealed class FFMpegCoreAudioTranscoder : IAudioTranscoder
    {
        /// <summary>
        /// Where <c>ffmpeg.exe</c> is. FFMpegCore's default <c>BinaryFolder</c> is the empty
        /// string, which resolves relative to the <em>current working directory</em> — fine when
        /// the app is launched from its own folder, wrong the moment it is launched from anywhere
        /// else (a shortcut with a different "Start in", a shell that cd'd elsewhere first).
        /// Pinning it to the assembly's own directory makes it independent of that.
        /// </summary>
        private static readonly string BinaryFolder = AppContext.BaseDirectory;

        private static readonly string FfmpegPath =
            Path.Combine(BinaryFolder, "ffmpeg.exe");

        public FFMpegCoreAudioTranscoder()
        {
            GlobalFFOptions.Configure(options =>
            {
                options.BinaryFolder = BinaryFolder;
                options.TemporaryFilesFolder = Path.GetTempPath();
            });
        }

        public string Name => "FFMpegCore (ffmpeg.exe)";

        public bool IsAvailable => File.Exists(FfmpegPath);

        public async Task<AudioTranscodeResult> TranscodeToOggVorbisAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken)
        {
            try
            {
                bool ok = await FFMpegArguments
                    .FromFileInput(inputPath)
                    .OutputToFile(outputPath, true, options => options
                        .WithAudioCodec("libvorbis"))
                    .CancellableThrough(cancellationToken)
                    .ProcessAsynchronously(throwOnError: false);

                return ok
                    ? AudioTranscodeResult.Succeeded()
                    : AudioTranscodeResult.Failed("ffmpeg returned a non-zero exit code.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AudioTranscodeResult.Failed($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
