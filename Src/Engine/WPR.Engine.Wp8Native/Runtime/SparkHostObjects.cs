namespace WPR.Wp8Native
{
    /// <summary>
    /// What the Spark2 engine's popup asks the player: a title, a message and up to three buttons.
    /// A button the shell was told to leave out is null (the game passes the literal <c>"empty"</c>).
    /// </summary>
    /// <param name="Cancel">Answered as response 1.</param>
    /// <param name="Button1">Answered as response 2.</param>
    /// <param name="Button2">Answered as response 3.</param>
    public sealed record SparkPopup(string Title, string Message, string? Cancel, string? Button1, string? Button2)
    {
        /// <summary>The response a button press records: 1 cancel, 2 button1, 3 button2.</summary>
        public const int ResponseCancel = 1, ResponseButton1 = 2, ResponseButton2 = 3;

        /// <summary>
        /// The answer when nobody can be asked: the first button the popup shows, in the shell's
        /// own order (cancel, button1, button2), as WPR answers MC4's confirm popup with its first
        /// button. 0 - no answer, as on the phone - when the popup has no buttons at all.
        /// </summary>
        public int DefaultResponse => Cancel is not null ? ResponseCancel
            : Button1 is not null ? ResponseButton1
            : Button2 is not null ? ResponseButton2
            : 0;

        /// <summary>Whether <paramref name="response"/> names a button this popup shows (0, no answer yet, always does).</summary>
        public bool Offers(int response) => response switch
        {
            0 => true,
            ResponseCancel => Cancel is not null,
            ResponseButton1 => Button1 is not null,
            ResponseButton2 => Button2 is not null,
            _ => false,
        };
    }

    /// <summary>
    /// The two managed objects Assassin's Creed Pirates' shell (<c>SparkApplicationXaml.dll</c>)
    /// hands its Spark2 engine, rebuilt as host COM objects: <c>SparkApp.CSharpFunctions</c>
    /// (its <c>CSharpClass</c>, passed to the <c>SparkAppBridge</c> constructor) and
    /// <c>LuaMobileSDK.ICallback</c> (its Flurry analytics adapter, passed to the
    /// <c>LuaMobileSDKBridge</c> constructor).
    /// </summary>
    /// <remarks>
    /// <para>WPR does not run the managed shell, so these stand where the CLR's COM callable wrappers
    /// stood: IInspectable objects in guest memory whose vtables the native engine calls. Both are
    /// <see cref="SystemClasses"/> classes, so QueryInterface answers by IID (IUnknown, IInspectable,
    /// IAgileObject, IWeakReferenceSource and the interface itself; anything else is
    /// <c>E_NOINTERFACE</c>), GetIids lists the interface, and GetRuntimeClassName answers the
    /// interface's name, which is what a CCW of a non-WinRT class reports.</para>
    /// <para>The vtables are the package winmds' own: <c>SparkApp.winmd</c> and
    /// <c>LuaMobileSDK.winmd</c>, members in declaration order from slot 6, ARM hard-float layout
    /// (<see cref="WinRtSystemInterfaces.Layout"/>). When the component's metadata is loaded
    /// (<see cref="StartComponent"/>), creating an object checks that layout against it and records any
    /// difference as slot drift, which the contract tests require to be empty.</para>
    /// <para>Every method answers as the real shell does on a phone with WPR's capabilities: no
    /// microphone, the game's own media player stopped and in the game's control, no network (as
    /// <c>NetworkInformation</c> here), links opened through <see cref="UriLauncher"/>, a popup
    /// answered with its first button unless <see cref="SparkPopupHost"/> answers it, and analytics
    /// dropped. Calls are counted in <see cref="SparkHostCalls"/>; anything worth reading
    /// (popups, links, analytics sessions) goes to <see cref="SystemClassLog"/> with a <c>[spark]</c>
    /// prefix.</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        /// <summary>IID of <c>SparkApp.CSharpFunctions</c>, from its GuidAttribute in SparkApp.winmd.</summary>
        internal static readonly Guid IidSparkCSharpFunctions = new("427c39ff-d636-3db5-a84d-973d60db98bb");

        /// <summary>IID of <c>LuaMobileSDK.ICallback</c>, from its GuidAttribute in LuaMobileSDK.winmd.</summary>
        internal static readonly Guid IidLuaMobileCallback = new("c7bb211b-e6f6-3a85-a4c7-5477d3903cc2");

        internal const string SparkCSharpFunctionsName = "SparkApp.CSharpFunctions";

        internal const string LuaMobileCallbackName = "LuaMobileSDK.ICallback";

        /// <summary>
        /// <c>SparkApp.CSharpFunctions</c>, member signatures exactly as SparkApp.winmd declares them,
        /// in vtable order (first member at slot 6).
        /// </summary>
        internal static readonly string[] SparkCSharpFunctionsMembers =
        [
            "MicrophoneCreate() : Void",
            "MicrophoneDestroy() : Void",
            "MicrophoneStartRecording() : Void",
            "MicrophoneStopRecording() : Void",
            "MicrophoneGetDecibel() : Double",
            "GetMediaPlayerState() : String",
            "GameHasControlMediaPlayer() : Boolean",
            "StopMediaPlayer() : Void",
            "PauseMediaPlayer() : Void",
            "PlayMediaPlayer() : Void",
            "NavigateToUrl(String) : Void",
            "ShowPopup(String, String, String, String, String, Boolean, Boolean, Boolean, Boolean, Boolean) : Void",
            "GetResponse() : Int32",
            "ResetResponse() : Void",
            "IsInternetAvailable() : Boolean",
        ];

        /// <summary><c>LuaMobileSDK.ICallback</c>, as LuaMobileSDK.winmd declares it.</summary>
        internal static readonly string[] LuaMobileCallbackMembers =
        [
            "StartNewSession(String) : Void",
            "LogEvent(String, System.Collections.Generic.IDictionary`2<String,String>, Boolean) : Void",
            "EndTimedEvent(String, System.Collections.Generic.IDictionary`2<String,String>) : Void",
            "SetAge(Int32) : Void",
            "SetGender(Int32) : Void",
            "SetLocation(Double, Double, Single) : Void",
            "SetUseHttps() : Void",
        ];

        /// <summary>
        /// Answers a Spark2 popup: the response (0 none, 1 cancel, 2 button1, 3 button2) its
        /// <c>GetResponse</c> will report. Null, or an answer naming a button the popup does not
        /// show, falls back to <see cref="SparkPopup.DefaultResponse"/>.
        /// </summary>
        public Func<SparkPopup, int>? SparkPopupHost { get; set; }

        /// <summary>Calls made on the Spark host objects, by <c>Interface::Member</c>.</summary>
        public Dictionary<string, int> SparkHostCalls { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// A new <c>SparkApp.CSharpFunctions</c> for the <c>SparkAppBridge</c> constructor
        /// (<c>__ISparkAppBridgeFactory.CreateInstance</c>), as the shell's <c>new CSharpClass()</c>.
        /// </summary>
        /// <returns>A guest interface pointer to <c>CSharpFunctions</c>, holding one reference.</returns>
        internal long CreateCSharpFunctions()
        {
            CheckSparkLayout(SparkCSharpFunctionsName, IidSparkCSharpFunctions, SparkCSharpFunctionsMembers);
            return Classes.NewSparkCSharpFunctions();
        }

        /// <summary>
        /// A new <c>LuaMobileSDK.ICallback</c> for the <c>LuaMobileSDKBridge</c> constructor, as the
        /// shell's <c>new FlurryInterfaceImpl()</c>.
        /// </summary>
        /// <returns>A guest interface pointer to <c>ICallback</c>, holding one reference.</returns>
        internal long CreateLuaMobileCallback()
        {
            CheckSparkLayout(LuaMobileCallbackName, IidLuaMobileCallback, LuaMobileCallbackMembers);
            return Classes.NewLuaMobileCallback();
        }

        /// <summary>
        /// The differences between an interface as built here and as <paramref name="metadata"/>
        /// declares it: IID, member count, and each member's name, slot, parameter and return types.
        /// Empty when they agree, or when the metadata does not have the interface.
        /// </summary>
        internal static List<string> CompareSparkLayout(WinmdReader metadata, string interfaceName, Guid iid, string[] members)
        {
            var drift = new List<string>();
            if (metadata.Interface(interfaceName) is not { } layout)
            {
                return drift;
            }

            if (layout.Iid is { } declared && declared != iid)
            {
                drift.Add($"{interfaceName}: IID is {declared} in the winmd, {iid} here");
            }

            if (layout.Methods.Count != members.Length)
            {
                drift.Add($"{interfaceName}: {layout.Methods.Count} members in the winmd, {members.Length} here");
            }

            for (int i = 0; i < Math.Min(layout.Methods.Count, members.Length); i++)
            {
                WinmdReader.MethodLayout method = layout.Methods[i];
                string ours = members[i];
                string winmd = $"{method.Name}({string.Join(", ", method.ParameterTypes)}) : {method.ReturnType}";
                if (method.Slot != InspectableSlots + i || winmd != ours)
                {
                    drift.Add($"{interfaceName} slot {InspectableSlots + i}: code says {ours}, winmd says {winmd} at slot {method.Slot}");
                }
            }

            return drift;
        }

        private void CheckSparkLayout(string interfaceName, Guid iid, string[] members)
        {
            if (_metadata is not null)
            {
                foreach (string line in CompareSparkLayout(_metadata, interfaceName, iid, members))
                {
                    if (!_slotDrift.Contains(line))
                    {
                        _slotDrift.Add(line);
                    }
                }
            }
        }

        private sealed partial class SystemClasses
        {
            /// <summary>The state of one CSharpFunctions: the popup answer <c>GetResponse</c> reports.</summary>
            private sealed class SparkFunctionsState
            {
                /// <summary>CSharpClass.m_response: 0 until a button is pressed, then 1-3.</summary>
                public int Response { get; set; }

                public SparkPopup? LastPopup { get; set; }
            }

            /// <summary>The shell passes this for a button it should leave out.</summary>
            private const string SparkNoButton = "empty";

            /// <summary>XNA's MediaState.Stopped.ToString(), what CSharpClass reports for a player nothing plays on.</summary>
            private const string SparkMediaStopped = "Stopped";

            private void SparkCount(string member)
                => _rt.SparkHostCalls[member] = _rt.SparkHostCalls.GetValueOrDefault(member) + 1;

            /// <summary>Counts the call; the first few of each go to the log as well.</summary>
            private void SparkNote(string member, string line)
            {
                SparkCount(member);
                if (_rt.SparkHostCalls[member] <= 5)
                {
                    Note("[spark] " + line);
                }
            }

            /// <summary>Writes a result through the trailing out-pointer, or fails E_POINTER when there is none to write.</summary>
            private bool SparkResult(RtCall c, int size)
            {
                if (_rt.IsWritable(c.ResultPointer, size))
                {
                    return true;
                }

                c.Fail(E_POINTER);
                return false;
            }

            public long NewSparkCSharpFunctions()
            {
                const string Face = "CSharpFunctions";
                ComClass cls = Class(SparkCSharpFunctionsName, [
                    I(
                        Manual(SparkCSharpFunctionsName, IidSparkCSharpFunctions, SparkCSharpFunctionsMembers),

                        // Microphone.Default is null here: there is no microphone. The shell's
                        // m_decibels starts at 0 and is only moved by a BufferReady that never comes,
                        // so every member succeeds and the level stays 0.
                        // void MicrophoneCreate(): CSharpClass subscribes BufferReady; nothing to subscribe to.
                        ("MicrophoneCreate", c =>
                        {
                            SparkNote($"{Face}::MicrophoneCreate", "MicrophoneCreate: no microphone on this host; the level stays 0 dB");
                            c.Ok();
                        }),

                        // void MicrophoneDestroy(): stops the microphone if it was started.
                        ("MicrophoneDestroy", c =>
                        {
                            SparkCount($"{Face}::MicrophoneDestroy");
                            c.Ok();
                        }),

                        // void MicrophoneStartRecording(): starts 500 ms buffers, resets the level to 0.
                        ("MicrophoneStartRecording", c =>
                        {
                            SparkNote($"{Face}::MicrophoneStartRecording", "MicrophoneStartRecording: no microphone; nothing is recorded");
                            c.Ok();
                        }),

                        // void MicrophoneStopRecording().
                        ("MicrophoneStopRecording", c =>
                        {
                            SparkCount($"{Face}::MicrophoneStopRecording");
                            c.Ok();
                        }),

                        // double MicrophoneGetDecibel(): 92.8 + 20 log10(rms) of the last buffer; 0 with none.
                        ("MicrophoneGetDecibel", c =>
                        {
                            SparkCount($"{Face}::MicrophoneGetDecibel");
                            if (SparkResult(c, 8))
                            {
                                c.RetDouble(0.0);
                            }
                        }),

                        // String GetMediaPlayerState(): MediaPlayer.State.ToString(). The managed
                        // MediaPlayer belongs to the shell and nothing on this bridge plays a Song on
                        // it, so it is always Stopped.
                        ("GetMediaPlayerState", c =>
                        {
                            SparkCount($"{Face}::GetMediaPlayerState");
                            if (SparkResult(c, 4))
                            {
                                c.RetStr(SparkMediaStopped);
                            }
                        }),

                        // bool GameHasControlMediaPlayer(): MediaPlayer.GameHasControl. True: no other
                        // app's music is playing over the game here (WP7's MediaPlayer shim says the same).
                        ("GameHasControlMediaPlayer", c =>
                        {
                            SparkCount($"{Face}::GameHasControlMediaPlayer");
                            if (SparkResult(c, 1))
                            {
                                c.RetBool(true);
                            }
                        }),

                        // void Stop/Pause/PlayMediaPlayer(): MediaPlayer.Stop / Pause / Resume of a player
                        // with nothing queued, which leave it Stopped.
                        ("StopMediaPlayer", c =>
                        {
                            SparkCount($"{Face}::StopMediaPlayer");
                            c.Ok();
                        }),
                        ("PauseMediaPlayer", c =>
                        {
                            SparkCount($"{Face}::PauseMediaPlayer");
                            c.Ok();
                        }),
                        ("PlayMediaPlayer", c =>
                        {
                            SparkCount($"{Face}::PlayMediaPlayer");
                            c.Ok();
                        }),

                        ("NavigateToUrl", c => SparkNavigateToUrl(c, $"{Face}::NavigateToUrl")),
                        ("ShowPopup", c => SparkShowPopup(c, $"{Face}::ShowPopup")),

                        // int GetResponse(): the popup answer, 0 until there is one. Polled.
                        ("GetResponse", c =>
                        {
                            SparkCount($"{Face}::GetResponse");
                            if (SparkResult(c, 4))
                            {
                                c.RetI32(c.State<SparkFunctionsState>().Response);
                            }
                        }),

                        // void ResetResponse(): back to 0, ready for the next popup.
                        ("ResetResponse", c =>
                        {
                            SparkCount($"{Face}::ResetResponse");
                            c.State<SparkFunctionsState>().Response = 0;
                            c.Ok();
                        }),

                        // bool IsInternetAvailable(): DeviceNetworkInformation.IsNetworkAvailable.
                        // Without an Xbox Live host: false. This runtime has no network (HTTP is
                        // answered offline, and NetworkInformation.GetInternetConnectionProfile answers
                        // null), so saying otherwise would send the game to services that can never
                        // answer. With one (WPR's game host, over the achievement store and WPR Hub):
                        // true, because Assassin's Creed Pirates gates its Achievements and Leaderboard
                        // menus on this and nothing else ("Check your internet connection to access
                        // this menu"), and those menus are served by the host.
                        ("IsInternetAvailable", c =>
                        {
                            SparkCount($"{Face}::IsInternetAvailable");
                            if (SparkResult(c, 1))
                            {
                                c.RetBool(_rt.XboxHost is not null);
                            }
                        })),
                ]);

                return New(cls, new SparkFunctionsState()).Primary;
            }

            /// <summary>
            /// <c>void NavigateToUrl(String url)</c>: the shell prefixes <c>http://</c> unless the text
            /// already starts with <c>http://</c>, <c>https://</c> or <c>zune:</c>, then calls
            /// <c>Launcher.LaunchUriAsync</c> without waiting. Here the result goes to the same
            /// <see cref="UriLauncher"/> hook that <c>LaunchUriAsync</c> uses, so the same rule
            /// applies: only http and https leave the game (<c>WebLinkPolicy</c>); <c>zune:</c>
            /// marketplace links are refused. Always S_OK - the method is void and the shell never
            /// reported the outcome.
            /// </summary>
            private void SparkNavigateToUrl(RtCall c, string member)
            {
                SparkCount(member);
                string url = c.Str(0);
                if (url.Length == 0)
                {
                    // The shell would build new Uri("http://") and throw; nothing is opened.
                    Note("[spark] NavigateToUrl(\"\"): no URL; nothing opened");
                    c.Ok();
                    return;
                }

                if (!url.StartsWith("http://", StringComparison.Ordinal) &&
                    !url.StartsWith("https://", StringComparison.Ordinal) &&
                    !url.StartsWith("zune:", StringComparison.Ordinal))
                {
                    url = "http://" + url;
                }

                bool opened = false;
                string outcome;
                if (_rt.UriLauncher is { } launcher)
                {
                    try
                    {
                        opened = launcher(url);
                        outcome = opened ? "opened" : "not opened";
                    }
                    catch (Exception ex)
                    {
                        outcome = $"host launcher threw {ex.GetType().Name}";
                    }
                }
                else
                {
                    outcome = "no launcher on this host";
                }

                Note($"[spark] NavigateToUrl({url}): {outcome}");
                c.Ok();
            }

            /// <summary>
            /// <c>void ShowPopup(title, message, cancel, button1, button2, five bold flags)</c>: the
            /// shell opens a Silverlight popup with a button for each text that is not
            /// <c>"empty"</c> and returns at once; the engine then polls <c>GetResponse</c>
            /// (1 cancel, 2 button1, 3 button2) and calls <c>ResetResponse</c>. There is no page to
            /// open it on, so it is answered now: by <see cref="SparkPopupHost"/> when set, else with
            /// <see cref="SparkPopup.DefaultResponse"/>. The bold flags are styling and are ignored.
            /// </summary>
            private void SparkShowPopup(RtCall c, string member)
            {
                SparkCount(member);
                string? Button(int index)
                {
                    string text = c.Str(index);
                    return text == SparkNoButton ? null : text;
                }

                var popup = new SparkPopup(c.Str(0), c.Str(1), Button(2), Button(3), Button(4));
                int answer = popup.DefaultResponse;
                string by = "default (first button)";
                if (_rt.SparkPopupHost is { } host)
                {
                    try
                    {
                        int asked = host(popup);
                        if (popup.Offers(asked))
                        {
                            answer = asked;
                            by = "host";
                        }
                        else
                        {
                            by = $"default; host answered {asked}, which is not a button of this popup";
                        }
                    }
                    catch (Exception ex)
                    {
                        by = $"default; host threw {ex.GetType().Name}";
                    }
                }

                SparkFunctionsState state = c.State<SparkFunctionsState>();
                state.LastPopup = popup;
                state.Response = answer;
                string buttons = string.Join(" | ", new[] { popup.Cancel, popup.Button1, popup.Button2 }.Select(b => b ?? "-"));
                Note($"[spark] ShowPopup \"{popup.Title}\": \"{popup.Message}\" [{buttons}] -> response {answer} ({by})");
                c.Ok();
            }

            public long NewLuaMobileCallback()
            {
                // Flurry analytics: WPR sends no analytics anywhere, so every event is dropped. Each
                // member is void and the shell's adapter reported nothing back, so S_OK is the whole contract.
                const string Face = "ICallback";
                ComClass cls = Class(LuaMobileCallbackName, [
                    I(
                        Manual(LuaMobileCallbackName, IidLuaMobileCallback, LuaMobileCallbackMembers),

                        // void StartNewSession(String apiKey): Api.StartSession(apiKey).
                        ("StartNewSession", c =>
                        {
                            SparkNote($"{Face}::StartNewSession", $"Flurry StartNewSession({c.Str(0)}): analytics are not sent");
                            c.Ok();
                        }),

                        // void LogEvent(String id, IDictionary<String,String> parameters, Boolean timed).
                        // The parameter map is the game's and is not read.
                        ("LogEvent", c =>
                        {
                            SparkNote($"{Face}::LogEvent", $"Flurry LogEvent({c.Str(0)}, timed={(c.P(2) & 0xFF) != 0}): dropped");
                            c.Ok();
                        }),

                        // void EndTimedEvent(String id, IDictionary<String,String> parameters).
                        ("EndTimedEvent", c =>
                        {
                            SparkNote($"{Face}::EndTimedEvent", $"Flurry EndTimedEvent({c.Str(0)}): dropped");
                            c.Ok();
                        }),

                        // void SetAge(Int32) / SetGender(Int32: 0 male, 1 female, else unknown).
                        ("SetAge", c =>
                        {
                            SparkCount($"{Face}::SetAge");
                            c.Ok();
                        }),
                        ("SetGender", c =>
                        {
                            SparkCount($"{Face}::SetGender");
                            c.Ok();
                        }),

                        // void SetLocation(Double latitude, Double longitude, Single accuracy): d0, d1,
                        // s4 under the hard-float ABI, no core words. Not recorded beyond the count:
                        // a location is personal data and goes nowhere.
                        ("SetLocation", c =>
                        {
                            SparkCount($"{Face}::SetLocation");
                            c.Ok();
                        }),

                        // void SetUseHttps(): Api.SetSecureTransportEnabled().
                        ("SetUseHttps", c =>
                        {
                            SparkCount($"{Face}::SetUseHttps");
                            c.Ok();
                        })),
                ]);

                return New(cls, null).Primary;
            }
        }
    }
}
