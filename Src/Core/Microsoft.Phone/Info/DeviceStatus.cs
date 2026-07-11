namespace Microsoft.Phone.Info
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Info.DeviceStatus</c>. Static device-info accessors. Most
    /// callers hit <c>DeviceName</c>/<c>DeviceManufacturer</c>, which the patcher's MemberPatches
    /// already redirect to <c>WPR.WindowsCompability.DeviceStatus</c>; but the type itself must
    /// exist here because unpatched members (e.g. <c>DeviceHardwareVersion</c>, used by AC
    /// Pirates) still reference <c>Microsoft.Phone.Info.DeviceStatus</c> directly. Values mirror
    /// the WindowsCompability shim for consistency.
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
