namespace WPR
{
    public enum ApplicationInstallError
    {
        None,
        MissingManifestFiles,
        InvalidManifestFiles,
        NotDecrypted,
        NotSupportedAppType,
        // WP8 "Modern Native" (C++/CX, WinRT) app — native ARM/x86 PE, no managed IL to host,
        // and no Silverlight AppManifest.xaml. Distinct from MissingManifestFiles so the UI can
        // give an honest reason instead of blaming a missing file.
        ModernNativeUnsupported,
        UnexpectedError,
        PatchFailed,
        ConvertFailed,
        Canceled
    }
}
