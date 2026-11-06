namespace System.Windows
{
    /// <summary>
    /// Shim for <c>System.Windows.Clipboard</c>. WP7.5/WP8 let an app put text on the clipboard
    /// and ask whether text is there; reading it back was reserved to the system (GetText threw
    /// <see cref="Security.SecurityException"/> in a third-party app).
    /// </summary>
    /// <remarks>
    /// The text is kept in this process: WPR has no seam to the platform clipboard yet, so a copy
    /// does not reach other apps. Marmalade's s3eClipboard uses it.
    /// </remarks>
    public static class Clipboard
    {
        private static string? _text;

        public static void SetText(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            _text = text;
        }

        public static bool ContainsText() => !string.IsNullOrEmpty(_text);

        public static string GetText() => _text ?? string.Empty;
    }
}
