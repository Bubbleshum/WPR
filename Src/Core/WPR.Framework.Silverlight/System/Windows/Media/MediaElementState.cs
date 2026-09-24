namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.MediaElementState</c>.
    /// </summary>
    /// <remarks>
    /// The numeric values are Silverlight's and are load-bearing: game IL compares
    /// <c>MediaElement.CurrentState</c> against a raw constant rather than a named member
    /// (Cut the Rope's <c>GamePage.OnDraw</c> does <c>bne.un</c> against <c>3</c>, i.e.
    /// <see cref="Playing"/>, to skip its XNA draw while a cutscene is on screen). Renumbering
    /// this enum would silently invert that test.
    /// </remarks>
    public enum MediaElementState
    {
        Closed = 0,
        Opening = 1,
        Buffering = 2,
        Playing = 3,
        Paused = 4,
        Stopped = 5,
        Individualizing = 6,
        AcquiringLicense = 7,
    }
}
