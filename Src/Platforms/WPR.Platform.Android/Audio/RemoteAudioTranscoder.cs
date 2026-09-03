using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Android.Content;
using Android.OS;

using WPR.Engine.Audio;

namespace WPR.Platform.Android.Audio
{
    /// <summary>
    /// The Android <see cref="IAudioTranscoder"/> the launcher registers: it does no transcoding
    /// itself, it forwards each file to <see cref="TranscodeService"/> in the <c>:transcode</c>
    /// process and waits for the answer.
    ///
    /// <para>See <see cref="TranscodeService"/> for why the work cannot happen in this process.
    /// The short version: running ffmpeg-kit leaves the Mono runtime unable to complete another
    /// stop-the-world, so the process it ran in is finished — silently, and only visibly so the
    /// next time anything allocates hard. The transcode always succeeds; it is the survivor that
    /// suffers. So the launcher stops being the survivor.</para>
    ///
    /// <para>Everything here is deliberately conservative about the far end being dead, because it
    /// is *expected* to die: it kills its own process once a batch goes quiet, and it may well have
    /// wedged before that. Every request is bounded by <see cref="RequestTimeout"/>, a lost binding
    /// completes the pending request as a failure rather than hanging the install, and the next
    /// call simply binds again — which starts a fresh process.</para>
    /// </summary>
    public sealed class RemoteAudioTranscoder : IAudioTranscoder
    {
        /// <summary>
        /// Ceiling on a single file. Generous: this covers ffmpeg decoding a long track on a slow
        /// device, and the only thing it really guards against is the far end having died mid-file,
        /// where the alternative is an install that never finishes.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Ceiling on binding to the service, i.e. on Android starting the process.</summary>
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

        private readonly Context _context;
        private readonly object _gate = new();

        private Connection? _connection;
        private Messenger? _remote;
        private Messenger? _replyTo;
        private int _nextRequestId;

        private readonly Dictionary<int, TaskCompletionSource<Bundle?>> _pending = new();

        public RemoteAudioTranscoder(Context context)
        {
            _context = context;
        }

        public string Name => "FFmpegKit (:transcode process)";

        /// <summary>
        /// Probes the remote process. Blocking, because the seam is a property and the caller
        /// checks it exactly once before its loop — on a thread-pool thread, never the UI thread
        /// (<c>ScanWmaAndConvert</c> is wrapped in <c>Task.Run</c> by both call sites).
        ///
        /// <para>Not cached: unlike the in-process transcoder, the answer really can change between
        /// batches, because the process backing it is torn down between them.</para>
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                try
                {
                    Bundle? reply = SendAsync(TranscodeService.MsgProbe, null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    return reply?.GetBoolean(TranscodeService.KeyOk) == true;
                }
                catch (Exception ex)
                {
                    WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                        $"Transcode service is not reachable ({ex.GetType().Name}: {ex.Message}); " +
                        ".wma soundtracks cannot be transcoded.");
                    return false;
                }
            }
        }

        public async Task<AudioTranscodeResult> TranscodeToOggVorbisAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Bundle request = new Bundle();
            request.PutString(TranscodeService.KeyInput, inputPath);
            request.PutString(TranscodeService.KeyOutput, outputPath);

            Bundle? reply;
            try
            {
                reply = await SendAsync(TranscodeService.MsgTranscode, request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AudioTranscodeResult.Failed($"{ex.GetType().Name}: {ex.Message}");
            }

            if (reply == null)
            {
                return AudioTranscodeResult.Failed("Transcode service returned no reply.");
            }

            return reply.GetBoolean(TranscodeService.KeyOk)
                ? AudioTranscodeResult.Succeeded()
                : AudioTranscodeResult.Failed(
                    reply.GetString(TranscodeService.KeyError) ?? "(no error text)");
        }

        private async Task<Bundle?> SendAsync(int what, Bundle? data, CancellationToken cancellationToken)
        {
            Messenger remote = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var completion = new TaskCompletionSource<Bundle?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            int id;
            lock (_gate)
            {
                id = ++_nextRequestId;
                _pending[id] = completion;
            }

            try
            {
                Message message = Message.Obtain(null, what);
                message.Arg1 = id;
                message.ReplyTo = _replyTo;
                if (data != null)
                {
                    message.Data = data;
                }

                remote.Send(message);

                using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                {
                    Task<Bundle?> reply = completion.Task;
                    if (await Task.WhenAny(reply, Task.Delay(RequestTimeout, cancellationToken))
                            .ConfigureAwait(false) != reply)
                    {
                        // The far end is gone or wedged. Drop the binding so the next call starts a
                        // brand-new process rather than talking to a corpse.
                        Disconnect();
                        throw new TimeoutException(
                            $"Transcode service did not answer within {RequestTimeout.TotalMinutes:0} minutes.");
                    }

                    return await reply.ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _pending.Remove(id);
                }
            }
        }

        private Task<Messenger> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_remote != null)
                {
                    return Task.FromResult(_remote);
                }
            }

            return ConnectAsync(cancellationToken);
        }

        private async Task<Messenger> ConnectAsync(CancellationToken cancellationToken)
        {
            var connected = new TaskCompletionSource<Messenger>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Connection connection = new Connection(this, connected);

            Intent intent = new Intent(_context, Java.Lang.Class.FromType(typeof(TranscodeService)));
            if (!_context.BindService(intent, connection, Bind.AutoCreate))
            {
                throw new InvalidOperationException("Could not bind the transcode service.");
            }

            lock (_gate)
            {
                _connection = connection;
                // Replies land on the main looper. All the handler does is complete a
                // TaskCompletionSource, so it costs the UI thread nothing measurable, and it saves
                // owning a second HandlerThread purely to receive them.
                _replyTo ??= new Messenger(new ReplyHandler(Looper.MainLooper!, this));
            }

            Task<Messenger> wait = connected.Task;
            if (await Task.WhenAny(wait, Task.Delay(ConnectTimeout, cancellationToken))
                    .ConfigureAwait(false) != wait)
            {
                Disconnect();
                throw new TimeoutException("The transcode service did not start in time.");
            }

            return await wait.ConfigureAwait(false);
        }

        private void Disconnect()
        {
            Connection? connection;
            lock (_gate)
            {
                connection = _connection;
                _connection = null;
                _remote = null;
            }

            if (connection == null)
            {
                return;
            }

            try { _context.UnbindService(connection); }
            catch (Exception) { /* already gone — which is the normal case here */ }
        }

        /// <summary>Fails every in-flight request. Called when the far end disappears, so a batch
        /// reports a per-file error instead of stalling the install for ever.</summary>
        private void FailPending(string reason)
        {
            List<TaskCompletionSource<Bundle?>> waiters;
            lock (_gate)
            {
                waiters = new List<TaskCompletionSource<Bundle?>>(_pending.Values);
                _pending.Clear();
            }

            foreach (TaskCompletionSource<Bundle?> waiter in waiters)
            {
                waiter.TrySetException(new InvalidOperationException(reason));
            }
        }

        private void CompleteRequest(int id, Bundle? data)
        {
            TaskCompletionSource<Bundle?>? completion;
            lock (_gate)
            {
                _pending.TryGetValue(id, out completion);
            }

            completion?.TrySetResult(data);
        }

        private sealed class Connection : Java.Lang.Object, IServiceConnection
        {
            private readonly RemoteAudioTranscoder _owner;
            private readonly TaskCompletionSource<Messenger> _connected;

            public Connection(RemoteAudioTranscoder owner, TaskCompletionSource<Messenger> connected)
            {
                _owner = owner;
                _connected = connected;
            }

            public void OnServiceConnected(ComponentName? name, IBinder? service)
            {
                if (service == null)
                {
                    _connected.TrySetException(
                        new InvalidOperationException("Transcode service bound with a null binder."));
                    return;
                }

                Messenger remote = new Messenger(service);
                lock (_owner._gate)
                {
                    _owner._remote = remote;
                }

                _connected.TrySetResult(remote);
            }

            public void OnServiceDisconnected(ComponentName? name)
            {
                // Expected: the service kills its own process when the batch goes quiet. Only an
                // in-flight request makes it a problem, and that one gets a failure rather than a
                // wait that never ends.
                _connected.TrySetException(
                    new InvalidOperationException("The transcode process ended before it connected."));
                _owner.FailPending("The transcode process ended mid-request.");

                // UNBIND, don't just forget the binder. While a binding is outstanding Android
                // treats the process ending as a crash and restarts it ("Scheduling restart of
                // crashed service … for connection"), which with a self-terminating service is an
                // endless spawn/idle/kill loop. Dropping the binding is also what makes the next
                // batch get a genuinely new process rather than a resurrected one.
                _owner.Disconnect();
            }
        }

        private sealed class ReplyHandler : Handler
        {
            private readonly RemoteAudioTranscoder _owner;

            public ReplyHandler(Looper looper, RemoteAudioTranscoder owner) : base(looper)
            {
                _owner = owner;
            }

            public override void HandleMessage(Message msg)
            {
                // Same recycling rule as the service side: read it now, use it later.
                _owner.CompleteRequest(msg.Arg1, msg.Data);
            }
        }
    }
}
