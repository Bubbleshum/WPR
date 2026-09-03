using System;

using Android.App;
using Android.Content;
using Android.OS;

using WPR.Engine.Audio;

namespace WPR.Platform.Android.Audio
{
    /// <summary>
    /// Runs ffmpeg-kit in a process of its own (<c>:transcode</c>) on behalf of the launcher.
    ///
    /// <para><b>Why a whole process.</b> Once ffmpeg-kit has run, the Mono runtime in that process
    /// is finished: the next stop-the-world never completes and every managed thread ends up parked
    /// in <c>sigsuspend</c>, having taken the suspend signal and never received the restart. No
    /// exception, no log, no CPU. The transcode itself always succeeds — every track lands on disk
    /// as Ogg Vorbis — so the damage only surfaces the *next* time the launcher does anything
    /// allocation-heavy, which is why it was reported as "installing a second game in one launch
    /// hangs". Measured on Pixel_Dev: two installs with no <c>.wma</c> leave the main thread idle
    /// in <c>do_epoll_wait</c>; one install that transcodes wedges the next one forever. Moving the
    /// work off ffmpeg-kit's executor and onto a thread we own did not help, so its threads are not
    /// the mechanism — the suspect is ffmpeg's native code replacing the signal handlers Mono's
    /// suspend/restart protocol relies on.</para>
    ///
    /// <para>That is not something the launcher can survive, so it does not try. This is the same
    /// answer <see cref="GameActivity"/> already gives for a game run: a process that has done the
    /// unrecoverable thing is disposable, so put the unrecoverable thing in a process we can throw
    /// away. Here it is cheaper — a game run needs a whole activity and SDL, a transcode needs a
    /// Service and two strings.</para>
    ///
    /// <para><b>It kills itself when the batch goes quiet</b> (<see cref="IdleShutdownMs"/>), rather
    /// than waiting to be told. The <c>IAudioTranscoder</c> seam is per-file and has no
    /// "batch finished" signal, and inventing one would push Android's problem into a contract the
    /// Windows head shares. An idle timer needs no protocol and — the point — guarantees the next
    /// install gets a *fresh* process even if this one has already wedged.</para>
    /// </summary>
    [Service(
        Name = "com.wpr.android.TranscodeService",
        Process = ":transcode",
        Exported = false)]
    public sealed class TranscodeService : global::Android.App.Service
    {
        /// <summary>Ask whether ffmpeg-kit is loadable here. Reply carries <see cref="KeyOk"/>.</summary>
        public const int MsgProbe = 1;

        /// <summary>Transcode <see cref="KeyInput"/> to <see cref="KeyOutput"/>. Reply carries
        /// <see cref="KeyOk"/> and, on failure, <see cref="KeyError"/>.</summary>
        public const int MsgTranscode = 2;

        public const string KeyInput = "in";
        public const string KeyOutput = "out";
        public const string KeyOk = "ok";
        public const string KeyError = "err";

        /// <summary>
        /// How long the process stays alive with nothing to do. Long enough to cover the gap
        /// between two tracks (and the container sniff and <c>File.Move</c> the caller does between
        /// them), short enough that a wedged process is gone well before the user can start another
        /// install.
        /// </summary>
        private const int IdleShutdownMs = 20000;

        private HandlerThread? _worker;
        private WorkHandler? _handler;
        private Messenger? _messenger;

        public override void OnCreate()
        {
            base.OnCreate();

            // Its own thread, not the main looper: ffmpeg runs synchronously inside HandleMessage,
            // and a multi-minute soundtrack would otherwise ANR this process. Nothing here draws,
            // so the main looper has no other job — but an ANR would still kill us mid-batch.
            _worker = new HandlerThread("wpr-transcode");
            _worker.Start();

            _handler = new WorkHandler(_worker.Looper!, this);
            _messenger = new Messenger(_handler);

            global::Android.Util.Log.Info("WPR", "TranscodeService started in its own process.");
        }

        public override IBinder? OnBind(Intent? intent) => _messenger?.Binder;

        public override void OnDestroy()
        {
            _worker?.QuitSafely();
            base.OnDestroy();
        }

        /// <summary>
        /// Ends the process outright rather than just <c>stopSelf</c>. The whole point of this
        /// service is that its runtime may already be unusable; letting Android keep the process
        /// warm for reuse would hand the next install exactly the wedged runtime we are trying to
        /// escape.
        /// </summary>
        private void ShutDownNow()
        {
            global::Android.Util.Log.Info("WPR", "TranscodeService idle — ending :transcode process.");
            StopSelf();
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        }

        private sealed class WorkHandler : Handler
        {
            /// <summary>
            /// Self-addressed message that ends the process. A delayed <em>message</em> rather than
            /// a delayed <c>Runnable</c>: cancelling a posted Runnable matches on object identity,
            /// and every C# delegate handed to the binding is wrapped in a fresh Java object, so
            /// <c>RemoveCallbacks</c> would silently never match. <c>RemoveMessages(what)</c> has no
            /// such trap.
            /// </summary>
            private const int MsgIdleShutdown = 99;

            private readonly TranscodeService _service;
            private readonly FFmpegKitAudioTranscoder _transcoder = new();

            public WorkHandler(Looper looper, TranscodeService service) : base(looper)
            {
                _service = service;
                ArmIdleShutdown();
            }

            public override void HandleMessage(Message msg)
            {
                // Message objects are pooled and recycled the moment this returns, so take
                // everything off it before doing any work.
                int what = msg.What;
                int requestId = msg.Arg1;
                Messenger? replyTo = msg.ReplyTo;
                Bundle? data = msg.Data;

                if (what == MsgIdleShutdown)
                {
                    _service.ShutDownNow();
                    return;
                }

                RemoveMessages(MsgIdleShutdown);

                Bundle reply = new Bundle();

                try
                {
                    switch (what)
                    {
                        case MsgProbe:
                            reply.PutBoolean(KeyOk, _transcoder.IsAvailable);
                            break;

                        case MsgTranscode:
                            string? input = data?.GetString(KeyInput);
                            string? output = data?.GetString(KeyOutput);
                            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output))
                            {
                                reply.PutBoolean(KeyOk, false);
                                reply.PutString(KeyError, "TranscodeService: missing input or output path.");
                                break;
                            }

                            // Synchronous on purpose — this handler thread exists to be blocked,
                            // and serialising requests is exactly the behaviour the caller's
                            // one-file-at-a-time loop expects.
                            AudioTranscodeResult result = _transcoder
                                .TranscodeToOggVorbisAsync(input!, output!, System.Threading.CancellationToken.None)
                                .GetAwaiter().GetResult();

                            reply.PutBoolean(KeyOk, result.Success);
                            if (!result.Success)
                            {
                                reply.PutString(KeyError, result.Error ?? "(no error text)");
                            }
                            break;

                        default:
                            reply.PutBoolean(KeyOk, false);
                            reply.PutString(KeyError, $"TranscodeService: unknown message {what}.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    reply.PutBoolean(KeyOk, false);
                    reply.PutString(KeyError, $"{ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    Message answer = Message.Obtain(null, what);
                    answer.Arg1 = requestId;
                    answer.Data = reply;
                    replyTo?.Send(answer);
                }
                catch (Exception ex)
                {
                    // The launcher went away mid-batch. Nothing to report to; just wind down.
                    global::Android.Util.Log.Warn("WPR", "TranscodeService could not reply: " + ex);
                }

                ArmIdleShutdown();
            }

            private void ArmIdleShutdown()
            {
                RemoveMessages(MsgIdleShutdown);
                SendEmptyMessageDelayed(MsgIdleShutdown, IdleShutdownMs);
            }
        }
    }
}
