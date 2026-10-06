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
                ]),
        };

        public static XamlShell? ForComponent(string componentDll) => Known.GetValueOrDefault(Path.GetFileName(componentDll));

        public static IEnumerable<string> ComponentDlls => Known.Keys;
    }
}
