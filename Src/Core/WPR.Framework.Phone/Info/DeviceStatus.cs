namespace Microsoft.Phone.Info
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Info.DeviceStatus</c>. Static device-info accessors.
    ///
    /// <para>This is the sole implementation. A duplicate used to live in
    /// <c>WPR.WindowsCompability</c> with two of these members (<c>DeviceName</c>,
    /// <c>DeviceManufacturer</c>) MemberPatch-redirected to it — returning the identical strings
    /// this type already returns, so the redirect bought nothing, while every other member
    /// (e.g. <c>DeviceHardwareVersion</c>, used by AC Pirates) bound here directly. Both the
    /// duplicate and the two redirects were removed; games now bind this facade for all members,
    /// with no patcher involvement.</para>
    /// </summary>
    public static class DeviceStatus
    {
        public static string DeviceName => "WPRunner 2025";
        public static string DeviceManufacturer => "Microsoft";
        public static string DeviceHardwareVersion => "1.0.0.0";
        public static string DeviceFirmwareVersion => "1.0.0.0";

        // Memory counters — return plausible WP-era values so games that gate features on them
        // (or just log them) behave. 512 MB device, 256 MB per-app cap.
        public static long DeviceTotalMemory => 512L * 1024 * 1024;
        public static long ApplicationCurrentMemoryUsage => 32L * 1024 * 1024;
        public static long ApplicationPeakMemoryUsage => 48L * 1024 * 1024;
        public static long ApplicationMemoryUsageLimit => 256L * 1024 * 1024;
    }
}
