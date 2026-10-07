namespace WPR.Wp8Native
{
    /// <summary>
    /// The managed pages of the Direct3D/XAML titles WPR knows how to host, written out from
    /// their decompiled MainPage/App code. Keyed by the component DLL the manifest's
    /// <c>ActivatableClasses</c> names.
    /// </summary>
    public static class XamlShells
    {
        private static readonly SizeF Wvga = new(480, 800);

        private static readonly Dictionary<string, XamlShell> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            // Amazing Alex (Rovio, Fusion engine). App.Application_Launching creates FusionMain,
            // subscribes OpenInput/CloseInput/SendEmail (the text box and the email composer) and
            // calls Launching; MainPage's DrawingSurfaceBackground_Loaded sets the sizes, the
            // landscape orientation (PageOrientation.LandscapeLeft -> 1) and hands over the
            // content provider and itself as the manipulation handler.
            ["AlexXamlComponent.dll"] = new XamlShell(
                "AlexXamlComponent.dll",
                "FusionComponent.FusionMain",
                Launch:
                [
                    new("add_OpenInput", XamlShell.Callback),
                    new("add_CloseInput", XamlShell.Callback),
                    new("add_SendEmail", XamlShell.Callback),
                    new("Launching"),
                ],
                Loaded:
                [
                    new("set_WindowBounds", Wvga),
                    new("set_NativeResolution", Wvga),
                    new("set_RenderResolution", Wvga),
                    new("set_m_TextInputContent", ""),
                    new("set_ScaleFactor", 100f),
                    new("set_Orientation", 1),
                    new("CreateContentProvider"),
                    new("SetManipulationHost", XamlShell.ManipulationHost),
                    new("set_m_enableMusic", true),
                ])
            {
                // MainPage_BackKeyPress: if (!IsSafeToQuit()) { m_backPressed = true; e.Cancel = true; }
                // and otherwise the press closes the app, whose Application_Closing calls
                // MainPage.Closing -> FusionMain.Closing.
                BackKey = new XamlBackKey(
                    "IsSafeToQuit",
                    Otherwise: [new("set_m_backPressed", true)],
                    BeforeQuit: [new("Closing")]),
            },

            // Modern Combat 4 (Gameloft). The component is a singleton (Direct3DBackground.GetInstance);
            // App.Application_Launching sets the licence, MainPage's constructor subscribes nineteen
            // static events - the C# side of movies, popups, store, Facebook, GLLive and device
            // queries - and initialises the promotions and Xbox modules; the background's Loaded
            // handler sets the sizes and hands over the content provider. Answers are what the page
            // would give on a phone with no network and the game in control of music.
            ["MC4HybridComponent.dll"] = new XamlShell(
                "MC4HybridComponent.dll",
                "MC4Component.Direct3DBackground",
                Launch:
                [
                    new("SetFullVersion", true, false),
                    new("add_LaunchGLLiveEvent", XamlShell.Callback),
                    // VideoPlayer plays data/briefing/*.mp4 and reports the end; there is no player
                    // here yet, so the movie ends at once.
                    new("add_ShowMovieEvent", new XamlCallback(null, new XamlShellStep("SetShowMovieFinish"))),
                    new("add_StopMovieEvent", XamlShell.Callback),
                    new("add_ReplayMovieEvent", XamlShell.Callback),
                    new("add_LaunchMarketPlaceEvent", XamlShell.Callback),
                    new("add_LaunchReviewEvent", XamlShell.Callback),
                    new("add_SetAutoLockScreenEnabledEvent", XamlShell.Callback),
                    new("add_ShowAlertEvent", XamlShell.Callback),
                    new("add_FBWallPostEvent", XamlShell.Callback),
                    new("add_NetworkTypeEnabledEvent", new XamlCallback(0)),
                    new("add_IsWifiAvailabeEvent", new XamlCallback(false)),
                    new("add_HideGameloftLogoEvent", new XamlCallback(null, new XamlShellStep("SetLogoVisible", false))),
                    // Guide.BeginShowMessageBox, answered with the first button (SetPopupResult(index + 1)).
                    new("add_ShowConfirmMessagePopupEvent", new XamlCallback(null, new XamlShellStep("SetPopupResult", 1))),
                    new("add_IsPhoneMusicPlayingEvent", new XamlCallback(false)),
                    new("add_PausePhoneMusicEvent", XamlShell.Callback),
                    new("add_ResumePhoneMusicEvent", XamlShell.Callback),
                    new("add_GetDeviceFirmwareVersionEvent", new XamlCallback("8.0.10521.0")),
                    new("add_GetRegionCodeEvent", new XamlCallback("US")),
                    new("add_GetDeviceNameEvent", new XamlCallback("WP8")),
                    new("InitIGPModule"),
                    new("FakeInitXBLUser"),
                    new("SetIGPState", false),
                    new("SetGLLiveState", false),
                    new("SetPhoneMusicPlaying", false),
                ],
                Loaded:
                [
                    new("set_WindowBounds", Wvga),
                    new("set_NativeResolution", Wvga),
                    new("set_RenderResolution", Wvga),
                    new("CreateContentProvider"),
                    new("SetManipulationHost", XamlShell.ManipulationHost),
                ])
            {
                InstanceFrom = "GetInstance",
                // OnBackKeyPress: e.Cancel = OnBackButtonPressed() (GLLive and the video player are
                // never open here).
                BackKey = new XamlBackKey(
                    "OnBackButtonPressed",
                    Otherwise: [],
                    BeforeQuit: [],
                    QuitWhen: false),
            },
        };

        public static XamlShell? ForComponent(string componentDll) => Known.GetValueOrDefault(Path.GetFileName(componentDll));

        public static IEnumerable<string> ComponentDlls => Known.Keys;
    }
}
