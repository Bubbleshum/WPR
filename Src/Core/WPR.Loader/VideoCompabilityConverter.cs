using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WPR.Common;
using WPR.Engine.Audio;

namespace WPR.Core
{
    /// <summary>
    /// Install-time preparation of a game's movies for a platform that cannot decode them as
    /// shipped — today Android, whose <c>MediaCodec</c> has no WMV. Writes
    /// <see cref="VideoSidecars"/> beside each movie through the composed transcoder, when that
    /// transcoder implements <see cref="IVideoSidecarTranscoder"/>; otherwise does nothing.
    ///
    /// <para><b>Never fails an install.</b> Unlike a soundtrack, a movie that cannot be prepared
    /// costs only the cutscene: the player reports it finished at once, which is what every WP7
    /// movie did before WPR played any. Each file is logged and skipped.</para>
    ///
    /// <para>Idempotent: a movie whose video sidecar already exists is left alone, so a repatch
    /// is nearly free and an install interrupted half-way resumes where it stopped.</para>
    /// </summary>
    public static class VideoCompabilityConverter
    {
        public static async Task PrepareMovies(string rootFolder, Action<int> progressReport, CancellationToken cancelToken)
        {
            if (AudioTranscoderBackend.Transcoder is not IVideoSidecarTranscoder transcoder)
            {
                return;
            }

            string[] movies = Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories)
                .Where(VideoSidecars.IsMovie)
                .Where(m => !File.Exists(VideoSidecars.VideoPath(m)))
                .ToArray();
            if (movies.Length == 0)
            {
                return;
            }

            Log.Info(LogCategory.AppAudioConverter, $"[wpr-video] preparing {movies.Length} movie(s) for this platform");
            int done = 0;
            foreach (string movie in movies)
            {
                cancelToken.ThrowIfCancellationRequested();
                string video = VideoSidecars.VideoPath(movie);
                string audio = VideoSidecars.AudioPath(movie);
                try
                {
                    AudioTranscodeResult result = await transcoder
                        .TranscodeVideoAsync(movie, video, audio, cancelToken)
                        .ConfigureAwait(false);
                    if (result.Success)
                    {
                        Log.Info(LogCategory.AppAudioConverter, $"[wpr-video] prepared {Path.GetFileName(movie)}");
                    }
                    else
                    {
                        Log.Warn(LogCategory.AppAudioConverter,
                            $"[wpr-video] could not prepare {movie} (its cutscene will be skipped): {result.Error}");
                        TryDelete(video);
                        TryDelete(audio);
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(video);
                    TryDelete(audio);
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppAudioConverter,
                        $"[wpr-video] preparing {movie} threw (its cutscene will be skipped): {ex.Message}");
                    TryDelete(video);
                    TryDelete(audio);
                }

                done++;
                progressReport((int) (100.0 * done / movies.Length));
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
