namespace WPR.XboxLive
{
    /// <summary>
    /// The registry the managed <c>Microsoft.Xbox</c> facade (WPR.Framework.Xbox) reads its
    /// <see cref="IXboxLiveHost"/> from. The facade's types are constructed by game code, so the
    /// host cannot reach them any other way.
    /// </summary>
    /// <remarks>
    /// Per launch, not per process: the host is keyed by the running title's ProductId.
    /// <c>ApplicationLaunch</c> sets it before the game is constructed and clears it at teardown.
    /// Null means no host (the WP8 probe, a harness that composes none): the facade then answers
    /// as an offline Xbox Live would, a signed-in guest with no achievements and empty boards.
    /// The WP8 native runtime takes its host through its constructor instead, because the probe
    /// compiles that runtime without this file.
    /// </remarks>
    public static class XboxLiveBackend
    {
        private static IXboxLiveHost? _host;

        public static IXboxLiveHost? Host => _host;

        public static void SetHost(IXboxLiveHost? host) => _host = host;
    }
}
