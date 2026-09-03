using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Com.Arthenica.Ffmpegkit;
using WPR.Engine.Audio;

namespace WPR.Platform.Android.Audio
{
    /// <summary>
    /// Android <see cref="IAudioTranscoder"/> — ffmpeg-kit, invoked through JNI.
    ///
    /// <para><b>Why not FFMpegCore, like the Windows head.</b> FFMpegCore spawns an <c>ffmpeg</c>
    /// child process. There is no <c>ffmpeg</c> executable in an APK and no supported way to exec
    /// one, so on Android it always failed — which is precisely the bug this class fixes: WP7 XNA
    /// titles ship <c>.wma</c> soundtracks, the transcode never ran, and MediaPlayer skipped every
    /// track (it sniffs for the <c>OggS</c> magic), so every such game was mute while its sound
    /// effects worked fine.</para>
    ///
    /// <para>ffmpeg-kit links ffmpeg <em>as a library</em> and exposes its CLI over JNI, so no
    /// process is involved. The pieces were already in the tree and in the APK — the native libs
    /// under <c>Libraries/&lt;abi&gt;/</c> (<c>libffmpegkit.so</c>, <c>libavcodec.so</c>, …) and the
    /// 45 MB AAR in <c>Src/JavaBindings/com.arthenica.ffmpegkit</c> — but that binding project
    /// targeted plain <c>net8.0</c>, so <c>class-parse</c> never ran and it built an assembly with
    /// no types in it. Nothing could call FFmpegKit because there was no FFmpegKit to call. The
    /// binding now targets <c>net8.0-android</c> and generates the API this file uses.</para>
    ///
    /// <para>The bundled build has what is needed on both ends: its configuration string carries
    /// <c>--enable-libvorbis</c> (the encoder) and <c>libavcodec.so</c> exports the <c>wmav1</c> /
    /// <c>wmav2</c> / <c>wmapro</c> decoders.</para>
    /// </summary>
    public sealed class FFmpegKitAudioTranscoder : IAudioTranscoder
    {
        public string Name => "FFmpegKit";

        /// <summary>
        /// True once ffmpeg-kit's native library is loaded and answering in this process.
        ///
        /// <para>Cached: the answer cannot change within a process, and the failure case is a
        /// <c>Java.Lang.UnsatisfiedLinkError</c> from the JNI class load, which is not worth
        /// re-raising per track — the caller checks this once, before its loop, for that
        /// reason.</para>
        /// </summary>
        public bool IsAvailable => _available ??= ProbeNativeLibrary();

        private static bool? _available;

        private static bool ProbeNativeLibrary()
        {
            try
            {
                // FFmpegKitConfig.getFFmpegVersion() is a NATIVE method, and touching the class runs
                // its static initialiser, which is what calls NativeLoader.loadFFmpegKit(). So a
                // non-empty answer here proves libffmpegkit.so both loaded and works.
                //
                // Deliberately not AbiDetect, which looks cheaper: that one lives in a DIFFERENT
                // native library (libffmpegkit_abidetect.so), so it would happily report success
                // while the library that actually does the transcoding was missing.
                string? version = FFmpegKitConfig.FFmpegVersion;
                if (string.IsNullOrEmpty(version))
                {
                    WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                        "FFmpegKit loaded but reported no ffmpeg version; treating it as unavailable.");
                    return false;
                }

                WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                    $"FFmpegKit available (ffmpeg {version}).");
                return true;
            }
            catch (Exception ex)
            {
                // Java.Lang.Throwable derives from System.Exception under .NET for Android, so an
                // UnsatisfiedLinkError lands here rather than escaping as a foreign error.
                WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                    $"FFmpegKit native library is not loadable ({ex.GetType().Name}: {ex.Message}); " +
                    ".wma soundtracks cannot be transcoded on this device.");
                return false;
            }
        }

        public async Task<AudioTranscodeResult> TranscodeToOggVorbisAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Argument array rather than FFmpegKit.Execute(string): the single-string overload is
            // split with shell-like quoting rules on the Java side, and these are absolute paths
            // under the app's data dir that we do not get to sanitise.
            string[] args =
            {
                "-y",                       // overwrite; the caller may be retrying a pass
                "-hide_banner",
                "-loglevel", "error",
                "-i", inputPath,
                "-vn",                      // WMA files can carry cover art; drop it
                "-c:a", "libvorbis",
                outputPath,
            };

            /* Synchronous ffmpeg on a thread of our own. Which thread this runs on is the whole
             * story here — see RunOnOwnThreadAsync for the three variants that were measured and
             * why the other two break the process. */
            return await RunOnOwnThreadAsync(
                () =>
                {
                    FFmpegSession? session = FFmpegKit.ExecuteWithArguments(args);
                    if (session == null)
                    {
                        return AudioTranscodeResult.Failed(
                            "FFmpegKit.ExecuteWithArguments returned no session.");
                    }

                    return Evaluate(session, outputPath);
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a thread created for it and nothing else.
        ///
        /// <para><b>Not <see cref="Task.Run(Func{TResult})"/>.</b> ffmpeg's CLI is re-entrant-hostile
        /// and long-running; on a .NET thread-pool thread the synchronous entry point never returns
        /// at all (no session log, no error — verified on Pixel_Dev). A thread of our own has no
        /// pool bookkeeping to confuse and exits as soon as the track is done.</para>
        ///
        /// <para><b>And not ffmpeg-kit's own executor either</b>, which is what
        /// <c>ExecuteWithArgumentsAsync</c> uses. Its <c>pool-N-thread-*</c> threads park in a
        /// blocking Java queue between tracks and stay alive for the life of the process, and a
        /// completion callback makes them run managed code, which attaches them to the runtime
        /// permanently. Owning the thread means it exits when the track does and nothing foreign is
        /// left attached.</para>
        ///
        /// <para><b>None of this saves the process, and it was never meant to.</b> Running
        /// ffmpeg-kit at all leaves the Mono runtime unable to complete another stop-the-world —
        /// measured identically with this thread arrangement and with ffmpeg-kit's executor, so
        /// its threads are not the mechanism. That is why this class only ever runs inside
        /// <see cref="TranscodeService"/>'s disposable <c>:transcode</c> process; see that type for
        /// the full diagnosis. Do not register it directly as the <c>IAudioTranscoder</c> for the
        /// launcher — <c>RemoteAudioTranscoder</c> is what belongs there.</para>
        /// </summary>
        private static Task<AudioTranscodeResult> RunOnOwnThreadAsync(
            Func<AudioTranscodeResult> work,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<AudioTranscodeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(
                        AudioTranscodeResult.Failed($"{ex.GetType().Name}: {ex.Message}"));
                }
            })
            {
                IsBackground = true,
                Name = "wpr-ffmpeg",
            };

            // Cancelling the install should stop the track in flight, not merely stop waiting for
            // it — otherwise ffmpeg keeps burning CPU on a package the user has abandoned. The
            // thread is left to unwind on its own; FFmpegKit.CancelAll makes that prompt.
            CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try { FFmpegKit.Cancel(); } catch (Exception) { /* nothing running */ }
                completion.TrySetCanceled(cancellationToken);
            });

            completion.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            thread.Start();
            return completion.Task;
        }

        private static AudioTranscodeResult Evaluate(FFmpegSession? session, string outputPath)
        {
            if (session == null)
            {
                return AudioTranscodeResult.Failed("ffmpeg-kit reported completion with no session.");
            }

            ReturnCode? returnCode = session.ReturnCode;
            if (returnCode != null && returnCode.IsValueSuccess)
            {
                // ffmpeg can exit 0 having written nothing when the input has no decodable audio
                // stream. The caller renames this file over the original, so an empty output would
                // replace a playable-in-principle .wma with a zero-byte one.
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    return AudioTranscodeResult.Failed("ffmpeg reported success but wrote no output.");
                }

                return AudioTranscodeResult.Succeeded();
            }

            // The (int) overload is the only one the binding generator emitted. The timeout bounds
            // how long it waits for log lines still in flight from the native side; the session has
            // already completed by the time this runs, so they should all be in, and this is a
            // backstop against blocking the install on a diagnostic string.
            const int logWaitMs = 5000;
            string detail = session.GetAllLogsAsString(logWaitMs)
                            ?? session.FailStackTrace
                            ?? "(no logs)";
            return AudioTranscodeResult.Failed(
                $"ffmpeg exited with {returnCode?.Value.ToString() ?? "an unknown code"}: {Trim(detail)}");
        }

        /// <summary>Keeps a failed session's log tail out of the install log's way.</summary>
        private static string Trim(string text)
        {
            const int max = 600;
            text = text.Trim();
            return text.Length <= max ? text : "…" + text.Substring(text.Length - max);
        }
    }
}
