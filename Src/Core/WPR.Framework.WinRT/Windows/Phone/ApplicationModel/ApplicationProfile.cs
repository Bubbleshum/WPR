namespace Windows.Phone.ApplicationModel
{
    /// <summary>
    /// Shim for <c>Windows.Phone.ApplicationModel.ApplicationProfile</c>: which profile the phone
    /// is running the app under. <see cref="ApplicationProfileModes.Alternate"/> is Kid's Corner;
    /// WPR has no such profile, so the answer is always <see cref="ApplicationProfileModes.Default"/>.
    /// YouTube reads it to decide its kids mode (<c>MainViewModel.IsInKidsZone</c>).
    /// </summary>
    public static class ApplicationProfile
    {
        public static ApplicationProfileModes Modes => ApplicationProfileModes.Default;
    }

    /// <summary>Shim for <c>Windows.Phone.ApplicationModel.ApplicationProfileModes</c>.</summary>
    [global::System.Flags]
    public enum ApplicationProfileModes : uint
    {
        Default = 0,
        Alternate = 1,
    }
}
