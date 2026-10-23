namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Silverlight's <c>System.Windows.Controls.UserControl</c> shim. The original
    /// is a thin wrapper around <see cref="ContentControl"/> intended as a base
    /// for user-authored composite controls and pages (Minesweeper's MainPage,
    /// most WP7 settings dialogs, etc.). For our purposes a direct subclass of
    /// ContentControl is sufficient — same Content property, same layout pass.
    ///
    /// Without this shim, types deriving from <c>System.Windows.Controls.UserControl</c>
    /// fail to load with <see cref="System.TypeLoadException"/>: the broken WP-era
    /// <c>System.Windows.dll</c> type-forwards the symbol to a target that doesn't
    /// exist on net8, and the CLR raises during the consuming type's class
    /// initialiser. <c>WPR.ApplicationPatcher</c> rewrites the assembly reference
    /// to point here at install time; this file is the rewrite target.
    /// </summary>
    public class UserControl : ContentControl
    {
        public UserControl() { }

        /// <summary>
        /// Silverlight's UserControl declares its own <c>protected UIElement Content</c>, and a
        /// user control setting its content in code calls that setter. It is the same value as
        /// <see cref="ContentControl.Content"/>; protected, so XAML and reflection still find
        /// the public one.
        /// </summary>
        protected new UIElement? Content
        {
            get => base.Content as UIElement;
            set => base.Content = value;
        }
    }
}
