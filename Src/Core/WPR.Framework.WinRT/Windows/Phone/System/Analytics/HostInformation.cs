namespace Windows.Phone.System.Analytics
{
    /// <summary>
    /// Shim for <c>Windows.Phone.System.Analytics.HostInformation</c>: an identifier for this device,
    /// the same for every title of one publisher. Games key analytics and save data on it, so it is
    /// a fixed value, the one the WP8 native runtime answers (<c>WinRtRuntime.RegisterHostInformation</c>).
    /// </summary>
    public static class HostInformation
    {
        public static string PublisherHostId => "57505200-0000-4000-8000-000000000001";
    }
}
