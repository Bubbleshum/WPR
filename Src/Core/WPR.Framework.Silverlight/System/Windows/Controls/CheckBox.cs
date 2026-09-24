namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.CheckBox</c>.</summary>
    /// <remarks>
    /// Everything it has — <c>IsChecked</c>, <c>Checked</c>, <c>Unchecked</c>,
    /// <c>Indeterminate</c> — is declared on <see cref="ToggleButton"/> in Silverlight, so it is
    /// inherited rather than redeclared. A redeclared member is not merely duplication here: game
    /// IL naming <c>ToggleButton::set_IsChecked</c> would bind to a DIFFERENT property from the one
    /// the XAML set, and the two would silently disagree.
    /// </remarks>
    public class CheckBox : ToggleButton
    {
    }
}
