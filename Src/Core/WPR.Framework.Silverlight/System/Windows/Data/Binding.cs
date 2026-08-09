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

        public Binding() { }
        public Binding(string path) { Path = path; }
    }
}
