namespace System.Device.Location
{
    /// <summary>
    /// Shim for <c>System.Device.Location.GeoPositionPermission</c>.
    ///
    /// Whether the user has allowed the app to read location. WPR has no location provider and
    /// no consent prompt, so <see cref="GeoCoordinateWatcher.Permission"/> reports
    /// <see cref="Denied"/> — see the note there for why that is the right answer rather than
    /// <see cref="Granted"/>.
    /// </summary>
    public enum GeoPositionPermission
    {
        Unknown,
        Granted,
        Denied
    }
}
