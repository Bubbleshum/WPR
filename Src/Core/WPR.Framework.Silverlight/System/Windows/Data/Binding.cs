namespace WPR.SilverlightCompability
{
    /// <summary>
    /// XAML markup extension descriptor. Captures the binding's intent (path, mode,
    /// optional explicit source). Materialized as a <see cref="BindingExpression"/>
    /// when actually attached to a target via <c>FrameworkElement.SetBinding</c>.
    /// </summary>
    public class Binding
    {
        public string Path { get; set; } = "";
        public BindingMode Mode { get; set; } = BindingMode.OneWay;
        public object? Source { get; set; }

        /// <summary>
        /// The <c>x:Name</c> of the element to bind against, instead of the DataContext.
        /// </summary>
        /// <remarks>
        /// This is how a UserControl's internal markup reaches the control's OWN properties —
        /// the control names itself and its parts bind through that name. Carcassonne's
        /// <c>MenuButton</c> is the worked example: <c>&lt;UserControl x:Name="MyControl"&gt;</c>
        /// with <c>Text="{Binding Title, ElementName=MyControl}"</c> on the label inside it.
        /// </remarks>
        public string? ElementName { get; set; }

        /// <summary>
        /// Binds against something located by relationship rather than by name — in practice
        /// <see cref="RelativeSourceMode.Self"/>, the element the binding is applied to.
        /// </summary>
        public RelativeSource? RelativeSource { get; set; }

        public Binding() { }
        public Binding(string path) { Path = path; }
    }
}
