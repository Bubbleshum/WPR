namespace WPR.Wp8Native
{
    /// <summary>
    /// The C# message thread of a Mandreel title's managed page, played by the host: a guest thread
    /// that loops on the component's <c>wait_event</c> and answers what the page would.
    /// </summary>
    /// <remarks>
    /// <para>Mandreel's WP8 template (<c>__MandreelWP8Direct3DInterop.Direct3DInterop</c>: Fruit Ninja
    /// 2013, Jetpack Joyride) talks to its page through two auto-reset events. The native side posts
    /// a message with <c>SetEvent(request)</c> and blocks in <c>WaitForSingleObjectEx(reply, INFINITE)</c>;
    /// the page's <c>MainPage.Alpha.Beta</c>, started with <c>new Thread(alpha.Beta).Start()</c> from
    /// the Loaded handler just before <c>CreateContentProvider</c>, sits in <c>wait_event(out size)</c>,
    /// reads the message (<c>get_message_event_data</c>/<c>2</c>), acts on it and, for a question,
    /// answers with <c>set_message_event_data</c>. Calling back into the component is what releases the
    /// native side.</para>
    /// <para>With no one on the other end the first message was a deadlock: the native side waited for
    /// ever, the ConcRT worker that had been running drained its queue and exited, and the run stopped
    /// with "guest thread 2 exited and no thread is runnable" five to eight frames in (and on Android,
    /// where it was the top WPR Hub crash group of 0.2.06). Before a worker existed the wait simply
    /// timed out, which is why the first two messages appeared to work.</para>
    /// <para>The protocol, read off both titles' <c>MainPage</c>: <c>wait_event</c> answers 1 (a
    /// command, <c>ProcessMessage</c>), 2 (an index for <c>dispatch_async_uithread</c>, which the page
    /// calls on the UI thread) or 3 (a question, <c>ProcessMessage2</c>, whose string answer goes back
    /// through <c>set_message_event_data</c>). Answers are a purchased copy on an English phone with no
    /// music of its own playing.</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private const int MandreelCommand = 1, MandreelDispatch = 2, MandreelQuestion = 3;

        /// <summary>What the page answers when <c>DeviceExtendedProperties["DeviceUniqueId"]</c> exists: Base64 of 20 bytes.</summary>
        private const string MandreelDeviceId = "V1BSAAAAQACAAAAAAAAAAQAAAAA=";

        private static readonly string[] MandreelMembers =
            ["wait_event", "get_message_event_data", "get_message_event_data2", "set_message_event_data", "dispatch_async_uithread"];

        /// <summary>Whether <paramref name="className"/> is Mandreel's interop class, with the message members the page's thread uses.</summary>
        public static bool HasMandreelMessages(WinmdReader metadata, string className) =>
            MandreelMembers.All(m => metadata.FindMethod(className, m) is not null);

        private bool _mandreelStarted;
        private bool _mandreelNavigatedTo = true;
        private bool _mandreelPromptCanceled;
        private long _mandreelInterface;
        private long _mandreelSlots;
        private long _mandreelBuffer;
        private int _mandreelBufferSize;
        private readonly Dictionary<string, int> _mandreelSeen = new(StringComparer.Ordinal);

        private int MandreelSlot(string method) => _metadata!.FindMethod(_shell!.ClassId, method)!.Value.Method.Slot;

        /// <summary>Starts the message thread (once), then <paramref name="then"/> on the calling thread.</summary>
        private void StartMandreelMessageThread(Action then)
        {
            _mandreelStarted = true;
            (WinmdReader.InterfaceLayout layout, _) = _metadata!.FindMethod(_shell!.ClassId, "wait_event")!.Value;
            WithInterface(layout, pointer =>
            {
                if (pointer == 0)
                {
                    Log($"mandreel: {layout.FullName} not available; no message thread");
                    then();
                    return;
                }

                _mandreelInterface = pointer;
                // The thread's own out-parameters: the host's scratch belongs to the main thread,
                // which may be in the middle of a call of its own when this one runs.
                _mandreelSlots = _emulator.AllocateHeap(16);
                long gate = _emulator.RegisterVtableMethod("MainPage.Alpha.Beta (Mandreel message thread)", MandreelWait);
                int id = _emulator.CreateGuestThread("Mandreel messages", ArmEmulator.ThumbEntry(gate), 0);
                Log($"mandreel: message thread {id} started on {layout.FullName}");
                then();
            });
        }

        /// <summary>One turn of <c>Beta</c>'s loop: <c>wait_event(out size)</c>, on the message thread.</summary>
        private void MandreelWait()
        {
            _emulator.WriteUInt32(_mandreelSlots, 0);
            _emulator.WriteUInt32(_mandreelSlots + 4, 0);
            MandreelCall("wait_event", [_mandreelSlots, _mandreelSlots + 4], hr =>
            {
                int kind = (int)_emulator.ReadUInt32(_mandreelSlots + 4);
                int size = (int)Math.Clamp(_emulator.ReadUInt32(_mandreelSlots), 0, 1 << 20);
                if ((hr & 0x80000000) != 0)
                {
                    Log($"mandreel: wait_event failed 0x{hr:X8}; the message thread ends");
                    if (!_emulator.EndCurrentGuestThread("wait_event failed"))
                    {
                        _emulator.Stop("the Mandreel message thread ended and no thread is runnable");
                    }

                    return;
                }

                switch (kind)
                {
                    case MandreelCommand:
                        MandreelRead("get_message_event_data", size, message =>
                        {
                            MandreelCommandReceived(message);
                            MandreelWait();
                        });
                        break;
                    case MandreelDispatch:
                        MandreelRead("get_message_event_data", size, message =>
                        {
                            // Dispatcher.BeginInvoke(() => dispatch_async_uithread(idx)): on the main
                            // thread, at the next turn of the frame loop.
                            if (int.TryParse(message, out int index))
                            {
                                _followUps.Enqueue(new XamlShellStep("dispatch_async_uithread", index));
                            }

                            MandreelNote(kind, "dispatch_async", message, null);
                            MandreelWait();
                        });
                        break;
                    case MandreelQuestion:
                        MandreelRead("get_message_event_data2", size, question =>
                        {
                            string answer = MandreelAnswer(question);
                            MandreelNote(kind, question, question, answer);
                            byte[] bytes = [.. answer.Select(c => c <= '\u007f' ? (byte)c : (byte)'?')];
                            long buffer = MandreelBufferOf(bytes.Length);
                            _emulator.WriteMemory(buffer, bytes);
                            MandreelCall("set_message_event_data", [bytes.Length, buffer], _ => MandreelWait());
                        });
                        break;
                    default:
                        MandreelNote(kind, $"kind {kind}", null, null);
                        MandreelWait();
                        break;
                }
            });
        }

        /// <summary>A <c>[Out] byte[]</c> of <paramref name="size"/> filled by <paramref name="method"/>, as ASCII.</summary>
        private void MandreelRead(string method, int size, Action<string> then)
        {
            long buffer = MandreelBufferOf(size);
            MandreelCall(method, [size, buffer], _ =>
            {
                Span<byte> bytes = size <= 1024 ? stackalloc byte[size] : new byte[size];
                _emulator.ReadMemory(buffer, bytes);
                then(System.Text.Encoding.Latin1.GetString(bytes));
            });
        }

        private long MandreelBufferOf(int size)
        {
            if (size > _mandreelBufferSize)
            {
                _mandreelBufferSize = Math.Max(size, 1024);
                _mandreelBuffer = _emulator.AllocateHeap(_mandreelBufferSize);
            }

            return _mandreelBuffer;
        }

        /// <summary>Calls a member of the interop interface on the current (message) thread; <paramref name="then"/> gets the HRESULT.</summary>
        private void MandreelCall(string method, long[] arguments, Action<long> then)
        {
            long vtable = _emulator.ReadUInt32(_mandreelInterface);
            long function = _emulator.ReadUInt32(vtable + (MandreelSlot(method) * 4));
            _emulator.CallEmulated($"Direct3DInterop::{method}", function, [_mandreelInterface, .. arguments], onReturn: () => then(Arg(0)), recycle: true);
        }

        /// <summary><c>MainPage.ProcessMessage</c>: commands. Only the ones that change an answer are acted on.</summary>
        private void MandreelCommandReceived(string message)
        {
            switch (message)
            {
                case "reset_NavigatedTo":
                    _mandreelNavigatedTo = false;
                    break;
                case "PromptExit":
                    // "Are you sure you want to quit the game?" after the player asked to leave: the
                    // page's OK button, NavigationService.GoBack() off its only page, closes the app.
                    _mandreelPromptCanceled = false;
                    BackLog.Add("Back: Mandreel PromptExit -> let through, closing");
                    BackPressDelivered?.Invoke(false);
                    break;
            }

            // save_picture, purchaseGame, PromptUpdate and the open_* web links are UI the host does
            // not have; logged and dropped, as a page whose dispatcher never ran them would.
            MandreelNote(MandreelCommand, message, message, null);
        }

        /// <summary><c>MainPage.ProcessMessage2</c>: questions, answered as a full copy would on an English phone.</summary>
        private string MandreelAnswer(string question) => question switch
        {
            "get_deviceId" => MandreelDeviceId,
            // CultureInfo.CurrentUICulture "en-US" -> "en" (the page's own mapping keeps the language part).
            "get_language" => Win32Core.DeviceLocale.Split('-')[0],
            // MediaPlayer.State was not Playing at start-up, so my_music_state stayed 0: the game's own music.
            "get_music_state" => "0",
            "check_NavigatedTo" => _mandreelNavigatedTo ? "1" : "0",
            "check_promptExit" => _mandreelPromptCanceled ? "1" : "0",
            "licenseStatus" => "1",
            "check_LicensePurchaseResult" => "2",
            _ => "error",
        };

        /// <summary>The first few of each message, for the log; a game may ask the same question every frame.</summary>
        private void MandreelNote(int kind, string key, string? message, string? answer)
        {
            int seen = _mandreelSeen.GetValueOrDefault(key);
            _mandreelSeen[key] = seen + 1;
            if (seen < 3)
            {
                Log($"mandreel: message {kind} \"{message}\"{(answer is null ? "" : $" -> \"{answer}\"")}");
            }
        }
    }
}
