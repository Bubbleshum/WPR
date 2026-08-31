using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using WPR.Abstractions.Audio;

namespace WPR.Core
{
    /// <summary>
    /// Thrown when no usable <see cref="IAudioTranscoder"/> is composed in, or when every track in
    /// a package failed to convert. Both are systemic — as opposed to one odd file — so they fail
    /// the install (<c>ApplicationInstallError.ConvertFailed</c>) instead of leaving the user a
    /// game that installed cleanly and plays no music.
    /// </summary>
    public sealed class AudioTranscodeUnavailableException : Exception
    {
        public AudioTranscodeUnavailableException(string message) : base(message)
        {
        }
    }

    public static class AudioCompabilityConverter
    {
        /// <summary>
        /// Finds every <c>.wma</c> under <paramref name="rootFolder"/> that the XNA content
        /// pipeline references as a Song and rewrites it as Ogg Vorbis in place, keeping the
        /// original alongside as <c>.wma.original</c>.
        ///
        /// <para><b>The result keeps the <c>.wma</c> filename.</b> The <c>.xnb</c> Song stub next
        /// to it names that path and is not rewritten, so the extension deliberately lies about
        /// the container afterwards. <c>MediaPlayer.IsSupportedSongPath</c> sniffs for the
        /// <c>OggS</c> magic instead of trusting the extension precisely because of this.</para>
        ///
        /// <para>The actual transcode goes through <see cref="AudioTranscoderBackend"/>: the tool
        /// is a bundled <c>ffmpeg.exe</c> on Windows and FFmpegKit's JNI entry point on Android,
        /// and neither can serve the other platform.</para>
        /// </summary>
        /// <exception cref="AudioTranscodeUnavailableException">
        /// No transcoder is registered, the registered one cannot run, or every candidate track
        /// failed.
        /// </exception>
        public static async Task ScanWmaAndConvert(string rootFolder, Action<int> progressReport, CancellationToken cancelToken)
        {
            var fileEnum = Directory.EnumerateFiles(rootFolder, "*.wma", SearchOption.AllDirectories).ToList();

            int countSoFar = 0;
            int totalCount = fileEnum.Count;

            if (totalCount == 0)
            {
                // Nothing to convert — an XNA title with no .wma soundtrack (most of them). Do NOT
                // demand a transcoder in this case: it would fail the install of every silent or
                // XACT-only game on a head that happens to have no transcoder composed in.
                progressReport(100);
                return;
            }

            // Resolved once, up front. A missing or unusable transcoder is not something to
            // rediscover 40 times: before this seam existed, Android hit exactly that and turned
            // it into 40 swallowed warnings and a mute game that reported a successful install.
            IAudioTranscoder? transcoder = AudioTranscoderBackend.Transcoder;
            if (transcoder == null)
            {
                throw new AudioTranscodeUnavailableException(
                    $"No IAudioTranscoder is registered, but this package has {totalCount} .wma " +
                    "file(s) that must be transcoded to Ogg Vorbis before MediaPlayer can play " +
                    "them. The platform head is expected to call " +
                    "AudioTranscoderBackend.SetTranscoder(...) in ServicesSetup.Start().");
            }

            if (!transcoder.IsAvailable)
            {
                throw new AudioTranscodeUnavailableException(
                    $"The registered audio transcoder ({transcoder.Name}) is not available on this " +
                    $"device, and this package has {totalCount} .wma file(s) that need it.");
            }

            WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                $"Transcoding {totalCount} .wma file(s) to Ogg Vorbis using {transcoder.Name}.");

            var failures = new List<string>();
            int attempted = 0;

            foreach (var filename in fileEnum)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                if (!File.Exists(filename + ".xnb") && !File.Exists(Path.ChangeExtension(filename, ".xnb")))
                {
                    // No Song stub references this file, so nothing will ever ask MediaPlayer for
                    // it. Leave it alone.
                    countSoFar++;
                    progressReport((int)(countSoFar * 100.0 / totalCount));

                    continue;
                }

                // ASF/WMA container magic. A `using` here rather than the hand-rolled Dispose calls
                // this method used to carry: the old code leaked the handle on every file whose
                // magic did NOT match, because that path fell out of the `if` without disposing.
                bool isAsf;
                using (FileStream headerCheckFile = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] magic = new byte[16] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA,
                        0x00, 0x62, 0xCE, 0x6C };

                    byte[] magicCheck = new byte[16];
                    int read = headerCheckFile.Read(magicCheck, 0, 16);

                    isAsf = read == 16 && magicCheck.SequenceEqual(magic);
                }

                if (!isAsf)
                {
                    // Already converted on a previous install pass (the file is Ogg now, under its
                    // old name), or never was WMA. Either way there is nothing to do — but it still
                    // has to advance the progress counter, which the previous version of this loop
                    // forgot, leaving the bar stuck for the rest of the package.
                    countSoFar++;
                    progressReport((int)(countSoFar * 100.0 / totalCount));

                    continue;
                }

                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                string newFilename = filename + ".new.ogg";
                attempted++;

                try
                {
                    // ConfigureAwait(false) is load-bearing, not decoration. Both heads start an
                    // install from their UI thread, so without it every continuation in this loop —
                    // the container sniffs, the File.Moves, the next transcode kick-off — is posted
                    // back to that thread, and on Android that produced a multi-minute ANR ("WPR
                    // isn't responding") for the whole soundtrack. Progress still reaches the UI
                    // safely: both progress sinks marshal (WpProgressDialog.SetProgress wraps
                    // RunOnUiThread).
                    AudioTranscodeResult result = await transcoder.TranscodeToOggVorbisAsync(
                        filename, newFilename, cancelToken).ConfigureAwait(false);

                    if (!result.Success)
                    {
                        failures.Add(filename);
                        WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                            $"Fail to convert audio file {filename} to ogg! {result.Error}");
                        TryDelete(newFilename);
                        countSoFar++;
                        progressReport((int)(countSoFar * 100.0 / totalCount));
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(newFilename);
                    return;
                }
                catch (Exception ex)
                {
                    failures.Add(filename);
                    WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                        $"Exception during audio conversion of {filename}: {ex.Message}");
                    TryDelete(newFilename);
                    countSoFar++;
                    progressReport((int)(countSoFar * 100.0 / totalCount));
                    continue;
                }

                File.Move(filename, filename + ".original", true);
                File.Move(newFilename, filename, true);

                countSoFar++;
                progressReport((int)(countSoFar * 100.0 / totalCount));
            }

            // One bad track is a warning and the rest of the soundtrack still plays. Every track
            // failing is the transcoder not working at all, which is the case that must not pass
            // for a successful install.
            if (attempted > 0 && failures.Count == attempted)
            {
                throw new AudioTranscodeUnavailableException(
                    $"All {attempted} .wma file(s) failed to transcode with {transcoder.Name}. " +
                    "The game would install with no music at all; see the warnings above for the " +
                    "per-file errors.");
            }

            if (failures.Count > 0)
            {
                WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                    $"{failures.Count} of {attempted} .wma file(s) could not be transcoded; those " +
                    "tracks will be silent.");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A leftover .new.ogg is harmless — the next install pass overwrites it, and the
                // .wma it would have replaced is still in place.
            }
        }
    }
}
