namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.FlowDirection</c>. Direction text and layout flow within a
    /// <see cref="FrameworkElement"/>. The WP app template's <c>InitializeLanguage()</c> parses
    /// this from a localized resource string and assigns it to the root frame.
    /// </summary>
    public enum FlowDirection
    {
        LeftToRight = 0,
        RightToLeft = 1,
    }
}
