using System.Globalization;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The app model and system classes: <c>CoreApplication</c> (Properties, GetCurrentView, Exit),
    /// launch activation arguments, <c>Launcher</c>, <c>ThreadPoolTimer</c>, <c>VibrationDevice</c>,
    /// <c>NetworkInformation</c> and the Windows.Globalization basics.
    /// </summary>
    public sealed partial class WinRtRuntime
    {
        /// <summary>
        /// Arguments for <c>CoreApplicationView.Activated</c> on a tile launch, as WP8 raised it:
        /// Kind Launch, PreviousExecutionState NotRunning, no splash screen object, no arguments.
        /// For whoever raises the event (it is not raised yet).
        /// </summary>
        public long CreateLaunchActivatedEventArgs(string arguments = "") => Classes.LaunchActivatedEventArgs(arguments);

        /// <summary>
        /// Queues every ThreadPoolTimer that is due as a deferred call, to run at the frame loop's
        /// next drain. Called by the frame loops once a frame, before they drain.
        /// </summary>
        private void QueueDueTimers() => _systemClasses?.QueueDueTimers();

        private sealed partial class SystemClasses
        {
            private void RegisterAppModel()
            {
                RegisterCoreApplication();
            }

            private void RegisterSystem()
            {
                RegisterLauncher();
                RegisterTimers();
                RegisterVibration();
                RegisterAudioRouting();
                RegisterNetwork();
                RegisterGlobalization();
            }

            // -----------------------------------------------------------------------------------
            // CoreApplication: the probe's members, plus Properties, GetCurrentView and Exit.
            // -----------------------------------------------------------------------------------

            private readonly List<long> _exitingHandlers = new();

            private void RegisterCoreApplication()
            {
                long properties = 0;
                Factory(
                    CoreApplicationClass,
                    I(
                        Meta("Windows.ApplicationModel.Core.ICoreApplication"),
                        ("get_Id", c => c.RetStr(string.Empty)),
                        ("add_Suspending", _ => _rt.AddLifecycleHandler(_rt._suspendingHandlers)()),
                        ("remove_Suspending", _ => _rt.RemoveLifecycleHandler(_rt._suspendingHandlers)()),
                        ("add_Resuming", _ => _rt.AddLifecycleHandler(_rt._resumingHandlers)()),
                        ("remove_Resuming", _ => _rt.RemoveLifecycleHandler(_rt._resumingHandlers)()),
                        ("get_Properties", c => c.RetObj(properties != 0 ? properties : properties = PropertySet().Primary)),
                        ("GetCurrentView", c => c.RetObj(_rt._coreApplicationView)),
                        ("Run", _ => _rt.CoreApplicationRun())),
                    I(
                        Meta("Windows.ApplicationModel.Core.ICoreApplicationExit"),
                        ("Exit", c =>
                        {
                            // The game asked to end. Exiting runs first, as on a device.
                            void End() => _e.Stop("the image called CoreApplication.Exit()");
                            if (!InvokeThenReturn(_exitingHandlers, h => [h, 0, 0], S_OK, "CoreApplication.Exiting", End))
                            {
                                c.Ok();
                                End();
                            }
                        }),
                        ("add_Exiting", c =>
                        {
                            _exitingHandlers.Add(c.P(0));
                            c.RetU64((ulong)_exitingHandlers.Count);
                            _rt.KeepAlive(c.P(0));
                        }),
                        ("remove_Exiting", c =>
                        {
                            long token = c.P64(0);
                            if (token >= 1 && token <= _exitingHandlers.Count)
                            {
                                _exitingHandlers[(int)token - 1] = 0;
                            }

                            c.Ok();
                        })),
                    I(
                        Meta("Windows.ApplicationModel.Core.ICoreApplicationUseCount"),
                        ("IncrementApplicationUseCount", c => c.Ok()),
                        ("DecrementApplicationUseCount", c => c.Ok())));
            }

            public long LaunchActivatedEventArgs(string arguments)
            {
                ComClass cls = Class("Windows.ApplicationModel.Activation.LaunchActivatedEventArgs", [
                    I(
                        Meta("Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs"),
                        ("get_Arguments", c => c.RetStr((string)c.Self.State!)),
                        ("get_TileId", c => c.RetStr("App"))),
                    I(
                        Meta("Windows.ApplicationModel.Activation.IActivatedEventArgs"),
                        ("get_Kind", c => c.RetU32(0)),                    // ActivationKind.Launch
                        ("get_PreviousExecutionState", c => c.RetU32(0)),  // ApplicationExecutionState.NotRunning
                        ("get_SplashScreen", c => c.RetObj(0))),
                ]);
                return New(cls, arguments).Primary;
            }

            // -----------------------------------------------------------------------------------
            // Launcher.
            // -----------------------------------------------------------------------------------

            private void RegisterLauncher()
            {
                Member launchUri = c =>
                {
                    string? uri = UriText(c.P(0));
                    bool launched = false;
                    if (uri is null)
                    {
                        c.Fail(E_INVALIDARG);
                        return;
                    }

                    if (_rt.UriLauncher is { } launcher)
                    {
                        try
                        {
                            launched = launcher(uri);
                        }
                        catch (Exception ex)
                        {
                            Note($"Launcher.LaunchUriAsync({uri}): host launcher threw {ex.GetType().Name}");
                        }
                    }
                    else
                    {
                        Note($"Launcher.LaunchUriAsync({uri}): no launcher on this host; answered false");
                    }

                    c.RetObj(OperationOfBool(launched));
                };

                Member launchFile = c =>
                {
                    // No other app here can open a file the game hands over; "no app was launched" is the documented answer.
                    Note($"Launcher.LaunchFileAsync({StateOf<ItemState>(c.P(0))?.GuestPath ?? "?"}): no app to open it; answered false");
                    c.RetObj(OperationOfBool(false));
                };

                Factory(
                    "Windows.System.Launcher",
                    I(
                        Meta("Windows.System.ILauncherStatics"),
                        ("LaunchFileAsync", launchFile),
                        ("LaunchFileAsync/2", launchFile),
                        ("LaunchUriAsync", launchUri),
                        ("LaunchUriAsync/2", launchUri)));
            }

            // -----------------------------------------------------------------------------------
            // ThreadPoolTimer: fired from the frame loop, whole frames at a time.
            // -----------------------------------------------------------------------------------

            /// <summary>One frame at 60 Hz in TimeSpan ticks (100 ns).</summary>
            private const long FrameTicks = 166_667;

            public sealed class TimerState
            {
                public long Handler { get; init; }

                public long Destroyed { get; init; }

                public long Ticks { get; init; }

                public bool Periodic { get; init; }

                public int Frames { get; init; }

                public int Due { get; set; }

                public bool Cancelled { get; set; }

                public long Pointer { get; set; }
            }

            private readonly List<TimerState> _timers = new();

            /// <summary>Timers created, fired and cancelled, for the report.</summary>
            public int TimersFired { get; private set; }

            private Member CreateTimer(bool periodic, bool withDestroyed) => c =>
            {
                long handler = c.P(0);
                long ticks = Math.Max(0, c.P64(1));
                if (handler == 0)
                {
                    c.Fail(E_POINTER);
                    return;
                }

                int frames = (int)Math.Max(1, (ticks + FrameTicks - 1) / FrameTicks);
                TimerState state = new()
                {
                    Handler = handler,
                    Destroyed = withDestroyed ? c.P(2) : 0,
                    Ticks = ticks,

                    // A periodic timer with a zero period fires once, as WinRT documents.
                    Periodic = periodic && ticks > 0,
                    Frames = frames,
                    Due = _rt.ProcessEventsCalls + frames,
                };

                ComClass cls = Class("Windows.System.Threading.ThreadPoolTimer", [
                    I(
                        Meta("Windows.System.Threading.IThreadPoolTimer"),
                        ("get_Period", t => t.RetI64(t.State<TimerState>().Periodic ? t.State<TimerState>().Ticks : 0)),
                        ("get_Delay", t => t.RetI64(t.State<TimerState>().Periodic ? 0 : t.State<TimerState>().Ticks)),
                        ("Cancel", t =>
                        {
                            TimerState s = t.State<TimerState>();
                            if (!s.Cancelled)
                            {
                                s.Cancelled = true;
                                _timers.Remove(s);
                                if (s.Destroyed != 0 && _rt.LooksLikeDelegate(s.Destroyed))
                                {
                                    _e.QueueDeferredCall("TimerDestroyedHandler::Invoke", DelegateInvoke(s.Destroyed), s.Destroyed, s.Pointer);
                                }
                            }

                            t.Ok();
                        })),
                ]);

                state.Pointer = New(cls, state).Primary;
                _timers.Add(state);
                c.RetObj(state.Pointer);
                _rt.KeepAlive(handler);
            };

            private long DelegateInvoke(long handler) => _e.ReadUInt32(_e.ReadUInt32(handler) + (SlotHandlerInvoke * 4));

            public void QueueDueTimers()
            {
                int now = _rt.ProcessEventsCalls;
                foreach (TimerState timer in _timers.ToList())
                {
                    if (timer.Cancelled || now < timer.Due)
                    {
                        continue;
                    }

                    if (_rt.LooksLikeDelegate(timer.Handler))
                    {
                        TimersFired++;
                        _e.QueueDeferredCall("TimerElapsedHandler::Invoke", DelegateInvoke(timer.Handler), timer.Handler, timer.Pointer);
                    }

                    if (timer.Periodic)
                    {
                        // A periodic timer that fell behind fires once, not once per missed period.
                        timer.Due = now + timer.Frames;
                    }
                    else
                    {
                        _timers.Remove(timer);
                    }
                }
            }

            private void RegisterTimers()
            {
                Factory(
                    "Windows.System.Threading.ThreadPoolTimer",
                    I(
                        Meta("Windows.System.Threading.IThreadPoolTimerStatics"),
                        ("CreatePeriodicTimer", CreateTimer(periodic: true, withDestroyed: false)),
                        ("CreateTimer", CreateTimer(periodic: false, withDestroyed: false)),
                        ("CreatePeriodicTimer/2", CreateTimer(periodic: true, withDestroyed: true)),
                        ("CreateTimer/2", CreateTimer(periodic: false, withDestroyed: true))));
            }

            // -----------------------------------------------------------------------------------
            // VibrationDevice (Windows.Phone.Devices.Notification). Not in the generated metadata:
            // the layout is WP8's own, and the class answers any IID with its one interface.
            // -----------------------------------------------------------------------------------

            /// <summary>The longest vibration WP8 accepted: 5 seconds.</summary>
            private const long MaximumVibrationTicks = 5 * TimeSpan.TicksPerSecond;

            private void RegisterVibration()
            {
                InterfaceDef device = Manual(
                    "Windows.Phone.Devices.Notification.IVibrationDevice",
                    new Guid("1b4a6595-cfcf-4e14-b9f8-1b3bc6a1c2f2"),
                    ["Vibrate(System.TimeSpan) : Void", "Cancel() : Void"]);
                InterfaceDef statics = Manual(
                    "Windows.Phone.Devices.Notification.IVibrationDeviceStatics",
                    new Guid("332fd2f1-1c69-4c91-949e-4bb67a85bdc7"),
                    ["GetDefault() : Object"]);

                ComClass cls = Class(
                    "Windows.Phone.Devices.Notification.VibrationDevice",
                    [
                        I(
                            device,
                            ("Vibrate", c =>
                            {
                                long ticks = c.P64(0);
                                if (ticks < 0 || ticks > MaximumVibrationTicks)
                                {
                                    c.Fail(E_INVALIDARG);
                                    return;
                                }

                                if (_rt.VibrateHost is { } vibrate)
                                {
                                    try
                                    {
                                        vibrate(TimeSpan.FromTicks(ticks));
                                    }
                                    catch (Exception ex)
                                    {
                                        Note($"VibrationDevice.Vibrate: host threw {ex.GetType().Name}");
                                    }
                                }

                                c.Ok();
                            }),
                            ("Cancel", c =>
                            {
                                try
                                {
                                    _rt.CancelVibrationHost?.Invoke();
                                }
                                catch (Exception ex)
                                {
                                    Note($"VibrationDevice.Cancel: host threw {ex.GetType().Name}");
                                }

                                c.Ok();
                            })),
                    ],
                    lenient: true);

                // Every WP8 phone had a motor, so GetDefault always answers a device: with no host
                // motor behind it, Vibrate succeeds and nothing shakes.
                long instance = 0;
                ComObject factory = New(
                    Class("Windows.Phone.Devices.Notification.VibrationDevice (factory)", [I(statics, ("GetDefault", c => c.RetObj(instance != 0 ? instance : instance = New(cls, null).Primary)))], lenient: true),
                    null);
                _factoryObjects["Windows.Phone.Devices.Notification.VibrationDevice"] = factory;
                _rt._factories["Windows.Phone.Devices.Notification.VibrationDevice"] = factory.Primary;
            }

            // -----------------------------------------------------------------------------------
            // AudioRoutingManager (Windows.Phone.Media.Devices). Not in the generated metadata;
            // the layout is WP8's own (windows.phone.media.devices.h), members in declaration order
            // from slot 6. The statics IID is confirmed by Spark2's LuaDevice.dll, which builds it on
            // the stack before GetActivationFactoryByPCWSTR. The phone answers as a handset with
            // its loudspeaker and earpiece and nothing plugged in or paired.
            // -----------------------------------------------------------------------------------

            /// <summary>AudioRoutingEndpoint: Default, Earpiece, Speakerphone, Bluetooth, WiredHeadset, WiredHeadsetSpeakerOnly, BluetoothWithNoiseAndEchoCancellation.</summary>
            internal const int AudioEndpointDefault = 0, AudioEndpointEarpiece = 1, AudioEndpointSpeakerphone = 2;

            /// <summary>AvailableAudioRoutingEndpoints flags: None 0, Earpiece 1, Speakerphone 2, Bluetooth 4.</summary>
            internal const uint AvailableEarpieceAndSpeakerphone = 1 | 2;

            internal static readonly Guid IidAudioRoutingManager = new("79340d20-71cc-4526-9f29-fc8d2486418b");
            internal static readonly Guid IidAudioRoutingManagerStatics = new("977fb2a4-5590-4a6f-adde-6a3d0ad58250");

            private int _audioEndpoint = AudioEndpointSpeakerphone;
            private readonly List<long> _audioEndpointHandlers = new();

            private void RegisterAudioRouting()
            {
                const string name = "Windows.Phone.Media.Devices.AudioRoutingManager";
                InterfaceDef manager = Manual(
                    "Windows.Phone.Media.Devices.IAudioRoutingManager",
                    IidAudioRoutingManager,
                    [
                        "GetAudioEndpoint() : Int32",
                        "SetAudioEndpoint(Int32) : Void",
                        "add_AudioEndpointChanged(Object) : Int64",
                        "remove_AudioEndpointChanged(Int64) : Void",
                        "get_AvailableAudioEndpoints() : UInt32",
                    ]);
                InterfaceDef statics = Manual(
                    "Windows.Phone.Media.Devices.IAudioRoutingManagerStatics",
                    IidAudioRoutingManagerStatics,
                    ["GetDefault() : Object"]);

                long instance = 0;
                ComClass cls = Class(
                    name,
                    [
                        I(
                            manager,
                            ("GetAudioEndpoint", c => c.RetI32(_audioEndpoint)),
                            ("SetAudioEndpoint", c =>
                            {
                                // Only an endpoint the phone has: nothing is paired or plugged in.
                                int wanted = (int)c.P(0);
                                if (wanted is not (AudioEndpointDefault or AudioEndpointEarpiece or AudioEndpointSpeakerphone))
                                {
                                    Note($"AudioRoutingManager.SetAudioEndpoint({wanted}): not available on this phone");
                                    c.Fail(E_INVALIDARG);
                                    return;
                                }

                                // Default routes media to the loudspeaker, as a phone with nothing attached does.
                                int routed = wanted == AudioEndpointDefault ? AudioEndpointSpeakerphone : wanted;
                                bool changed = routed != _audioEndpoint;
                                _audioEndpoint = routed;
                                c.Ok();
                                if (changed)
                                {
                                    // The change is reported later, from the system, never on the caller's stack.
                                    foreach (long handler in _audioEndpointHandlers.Where(h => h != 0 && _rt.LooksLikeDelegate(h)))
                                    {
                                        long invoke = _e.ReadUInt32(_e.ReadUInt32(handler) + (SlotHandlerInvoke * 4));
                                        _e.QueueDeferredCall("AudioRoutingManager.AudioEndpointChanged", invoke, handler, c.Self.Primary, 0);
                                    }
                                }
                            }),
                            ("add_AudioEndpointChanged", c =>
                            {
                                long handler = c.P(0);
                                if (handler == 0)
                                {
                                    c.Fail(E_INVALIDARG);
                                    return;
                                }

                                _audioEndpointHandlers.Add(handler);
                                _rt.KeepAlive(handler);
                                c.RetI64(_audioEndpointHandlers.Count);
                            }),
                            ("remove_AudioEndpointChanged", c =>
                            {
                                long token = c.P64(0);
                                if (token >= 1 && token <= _audioEndpointHandlers.Count)
                                {
                                    _audioEndpointHandlers[(int)token - 1] = 0;
                                }

                                c.Ok();
                            }),
                            ("get_AvailableAudioEndpoints", c => c.RetU32(AvailableEarpieceAndSpeakerphone))),
                    ]);

                ComObject factory = New(
                    Class(name + " (factory)", [I(statics, ("GetDefault", c => c.RetObj(instance != 0 ? instance : instance = New(cls, null).Primary)))]),
                    null);
                _factoryObjects[name] = factory;
                _rt._factories[name] = factory.Primary;
            }

            // -----------------------------------------------------------------------------------
            // NetworkInformation: this runtime has no network (HTTP is answered offline), so there is
            // no connection profile, which is how WP8 said "offline".
            // -----------------------------------------------------------------------------------

            private readonly List<long> _networkHandlers = new();

            private void RegisterNetwork()
            {
                string profile = Sig.Rc("Windows.Networking.Connectivity.ConnectionProfile", "Windows.Networking.Connectivity.IConnectionProfile");
                Factory(
                    "Windows.Networking.Connectivity.NetworkInformation",
                    I(
                        Meta("Windows.Networking.Connectivity.INetworkInformationStatics"),
                        ("GetConnectionProfiles", c => c.RetObj(ObjectView(profile, []))),
                        ("GetInternetConnectionProfile", c => c.RetObj(0)),
                        ("GetLanIdentifiers", c => c.RetObj(ObjectView(Sig.Rc("Windows.Networking.Connectivity.LanIdentifier", "Windows.Networking.Connectivity.ILanIdentifier"), []))),
                        ("GetHostNames", c => c.RetObj(ObjectView(Sig.Rc("Windows.Networking.HostName", "Windows.Networking.IHostName"), []))),
                        ("add_NetworkStatusChanged", c =>
                        {
                            _networkHandlers.Add(c.P(0));
                            c.RetU64((ulong)_networkHandlers.Count);
                            _rt.KeepAlive(c.P(0));
                        }),
                        ("remove_NetworkStatusChanged", c => c.Ok())));
            }

            // -----------------------------------------------------------------------------------
            // Windows.Globalization: ApplicationLanguages, Language, GeographicRegion.
            // -----------------------------------------------------------------------------------

            private string _primaryLanguageOverride = string.Empty;

            /// <summary>The device's language, as a BCP-47 tag.</summary>
            private static string UserLanguage
            {
                get
                {
                    string name = CultureInfo.CurrentUICulture.Name;
                    return name.Length == 0 ? "en-US" : name;
                }
            }

            /// <summary>
            /// The app's language list: the override, else the user's language when the manifest
            /// declares it (or its neutral form), else the manifest's default.
            /// </summary>
            private List<string> AppLanguages()
            {
                if (_primaryLanguageOverride.Length > 0)
                {
                    return [_primaryLanguageOverride];
                }

                string user = UserLanguage;
                string neutral = user.Split('-')[0];
                string? match = Package.Languages.FirstOrDefault(l => l.Equals(user, StringComparison.OrdinalIgnoreCase))
                    ?? Package.Languages.FirstOrDefault(l => l.Split('-')[0].Equals(neutral, StringComparison.OrdinalIgnoreCase));
                return [match ?? Package.DefaultLanguage];
            }

            private static CultureInfo? Culture(string tag)
            {
                try
                {
                    CultureInfo culture = CultureInfo.GetCultureInfo(tag);
                    return culture.Name.Length == 0 && tag.Length > 0 ? null : culture;
                }
                catch (CultureNotFoundException)
                {
                    return null;
                }
            }

            private static string Script(CultureInfo culture) => culture.Name.ToLowerInvariant() switch
            {
                var n when n.Contains("hant") || n is "zh-tw" or "zh-hk" or "zh-mo" => "Hant",
                var n when n.StartsWith("zh") => "Hans",
                var n when n.StartsWith("ja") => "Jpan",
                var n when n.StartsWith("ko") => "Kore",
                var n when n.StartsWith("ru") || n.StartsWith("uk") || n.StartsWith("bg") || n.StartsWith("sr-cyrl") => "Cyrl",
                var n when n.StartsWith("ar") || n.StartsWith("fa") => "Arab",
                var n when n.StartsWith("he") => "Hebr",
                var n when n.StartsWith("el") => "Grek",
                var n when n.StartsWith("th") => "Thai",
                _ => "Latn",
            };

            private long Language(CultureInfo culture)
            {
                ComClass cls = Class("Windows.Globalization.Language", [
                    I(
                        Meta("Windows.Globalization.ILanguage"),
                        ("get_LanguageTag", c => c.RetStr(c.State<CultureInfo>().Name)),
                        ("get_DisplayName", c => c.RetStr(c.State<CultureInfo>().EnglishName)),
                        ("get_NativeName", c => c.RetStr(c.State<CultureInfo>().NativeName)),
                        ("get_Script", c => c.RetStr(Script(c.State<CultureInfo>())))),
                ]);
                return New(cls, culture).Primary;
            }

            private static RegionInfo? Region(string code)
            {
                try
                {
                    return code.Length is 2 or 3 ? new RegionInfo(code) : null;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            private static RegionInfo UserRegion()
            {
                try
                {
                    return RegionInfo.CurrentRegion.TwoLetterISORegionName.Length == 2 ? RegionInfo.CurrentRegion : new RegionInfo("US");
                }
                catch (ArgumentException)
                {
                    return new RegionInfo("US");
                }
            }

            private long GeographicRegion(RegionInfo region)
            {
                ComClass cls = Class("Windows.Globalization.GeographicRegion", [
                    I(
                        Meta("Windows.Globalization.IGeographicRegion"),
                        ("get_Code", c => c.RetStr(c.State<RegionInfo>().TwoLetterISORegionName)),
                        ("get_CodeTwoLetter", c => c.RetStr(c.State<RegionInfo>().TwoLetterISORegionName)),
                        ("get_CodeThreeLetter", c => c.RetStr(c.State<RegionInfo>().ThreeLetterISORegionName)),
                        ("get_DisplayName", c => c.RetStr(c.State<RegionInfo>().EnglishName)),
                        ("get_NativeName", c => c.RetStr(c.State<RegionInfo>().NativeName)),
                        ("get_CurrenciesInUse", c => c.RetObj(StringView([c.State<RegionInfo>().ISOCurrencySymbol])))),
                ]);
                return New(cls, region).Primary;
            }

            private void RegisterGlobalization()
            {
                Factory(
                    "Windows.Globalization.ApplicationLanguages",
                    I(
                        Meta("Windows.Globalization.IApplicationLanguagesStatics"),
                        ("get_PrimaryLanguageOverride", c => c.RetStr(_primaryLanguageOverride)),
                        ("put_PrimaryLanguageOverride", c =>
                        {
                            string tag = c.Str(0);
                            if (tag.Length > 0 && Culture(tag) is null)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            _primaryLanguageOverride = tag;
                            c.Ok();
                        }),
                        ("get_Languages", c => c.RetObj(StringView(AppLanguages()))),
                        ("get_ManifestLanguages", c => c.RetObj(StringView(Package.Languages)))));

                Factory(
                    "Windows.Globalization.Language",
                    I(
                        Meta("Windows.Globalization.ILanguageFactory"),
                        ("CreateLanguage", c =>
                        {
                            if (Culture(c.Str(0)) is { } culture)
                            {
                                c.RetObj(Language(culture));
                            }
                            else
                            {
                                c.Fail(E_INVALIDARG);
                            }
                        })),
                    I(
                        Meta("Windows.Globalization.ILanguageStatics"),
                        ("IsWellFormed", c => c.RetBool(Culture(c.Str(0)) is not null)),
                        ("get_CurrentInputMethodLanguageTag", c => c.RetStr(UserLanguage))));

                Factory(
                    "Windows.Globalization.GeographicRegion",
                    I(ActivationFactory, ("ActivateInstance", c => c.RetObj(GeographicRegion(UserRegion())))),
                    I(
                        Meta("Windows.Globalization.IGeographicRegionFactory"),
                        ("CreateGeographicRegion", c =>
                        {
                            if (Region(c.Str(0)) is { } region)
                            {
                                c.RetObj(GeographicRegion(region));
                            }
                            else
                            {
                                c.Fail(E_INVALIDARG);
                            }
                        })),
                    I(
                        Meta("Windows.Globalization.IGeographicRegionStatics"),
                        ("IsSupported", c => c.RetBool(Region(c.Str(0)) is not null))));
            }
        }
    }
}
