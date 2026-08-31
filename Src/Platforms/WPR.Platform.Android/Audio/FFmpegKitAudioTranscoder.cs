using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Com.Arthenica.Ffmpegkit;
using WPR.Abstractions.Audio;

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

        /// <summary>
        /// Bridges ffmpeg-kit's completion callback to a <see cref="TaskCompletionSource{TResult}"/>.
        /// ffmpeg-kit invokes <see cref="Apply"/> on its own executor thread when the session ends.
        /// </summary>
        private sealed class CompletionCallback : Java.Lang.Object, IFFmpegSessionCompleteCallback
        {
            private readonly TaskCompletionSource<AudioTranscodeResult> _completion;
            private readonly string _outputPath;

            public CompletionCallback(
                TaskCompletionSource<AudioTranscodeResult> completion,
                string outputPath)
            {
                _completion = completion;
                _outputPath = outputPath;
            }

            public void Apply(FFmpegSession? session)
            {
                try
                {
                    _completion.TrySetResult(Evaluate(session, _outputPath));
                }
                catch (Exception ex)
                {
                    // Never let an exception escape into ffmpeg-kit's executor thread: it is a Java
                    // thread and an unhandled managed exception there takes the process down.
                    _completion.TrySetResult(
                        AudioTranscodeResult.Failed($"{ex.GetType().Name}: {ex.Message}"));
                }
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

            /* The ASYNC entry point, deliberately — not ExecuteWithArguments wrapped in Task.Run.
             *
             * The synchronous overload runs ffmpeg on the calling thread, and that turned out to be
             * usable from exactly one place: the UI thread. Called there it works but freezes the
             * app for the whole soundtrack (a 36-track ANR, verified on the emulator); moved to a
             * .NET thread-pool thread to fix that, it does not run at all — the call never returns
             * and ffmpeg never emits a single session log, so the conversion stalls on the first
             * file with the app still responsive. Both failure modes were observed on Pixel_Dev.
             *
             * ExecuteWithArgumentsAsync hands the work to ffmpeg-kit's OWN executor and returns
             * immediately, which is what its API is designed for. The completion callback lands on
             * that executor's thread and we bridge it back to a Task. */
            var completion = new TaskCompletionSource<AudioTranscodeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using (var callback = new CompletionCallback(completion, outputPath))
            {
                FFmpegSession? session;
                try
                {
                    session = FFmpegKit.ExecuteWithArgumentsAsync(args, callback);
                }
                catch (Exception ex)
                {
                    return AudioTranscodeResult.Failed($"{ex.GetType().Name}: {ex.Message}");
                }

                if (session == null)
                {
                    return AudioTranscodeResult.Failed(
                        "FFmpegKit.ExecuteWithArgumentsAsync returned no session.");
                }

                long sessionId = session.SessionId;

                // Cancelling the install should stop the track in flight, not just stop waiting for
                // it — otherwise ffmpeg keeps burning CPU on a package the user has abandoned.
                using (cancellationToken.Register(() =>
                {
                    try { FFmpegKit.Cancel(sessionId); } catch { /* already finished */ }
                    completion.TrySetCanceled(cancellationToken);
                }))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
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
