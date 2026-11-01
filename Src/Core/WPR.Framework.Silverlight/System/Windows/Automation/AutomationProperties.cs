namespace System.Windows.Automation
{
    /// <summary>
    /// Shim for <c>System.Windows.Automation.AutomationProperties</c>: the accessibility names
    /// XAML attaches to elements (<c>AutomationProperties.Name="Search"</c>). Stored, read by
    /// nothing: WPR has no screen reader.
    /// </summary>
    public static class AutomationProperties
    {
        public static readonly DependencyProperty NameProperty =
            DependencyProperty.RegisterAttached("Name", typeof(string), typeof(AutomationProperties), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty AutomationIdProperty =
            DependencyProperty.RegisterAttached("AutomationId", typeof(string), typeof(AutomationProperties), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty HelpTextProperty =
            DependencyProperty.RegisterAttached("HelpText", typeof(string), typeof(AutomationProperties), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty LabeledByProperty =
            DependencyProperty.RegisterAttached("LabeledBy", typeof(UIElement), typeof(AutomationProperties), new PropertyMetadata((object?)null));

        public static string GetName(DependencyObject element) => (string?)element.GetValue(NameProperty) ?? string.Empty;

        public static void SetName(DependencyObject element, string value) => element.SetValue(NameProperty, value);

        public static string GetAutomationId(DependencyObject element) => (string?)element.GetValue(AutomationIdProperty) ?? string.Empty;

        public static void SetAutomationId(DependencyObject element, string value) => element.SetValue(AutomationIdProperty, value);

        public static string GetHelpText(DependencyObject element) => (string?)element.GetValue(HelpTextProperty) ?? string.Empty;

        public static void SetHelpText(DependencyObject element, string value) => element.SetValue(HelpTextProperty, value);

        public static UIElement? GetLabeledBy(DependencyObject element) => (UIElement?)element.GetValue(LabeledByProperty);

        public static void SetLabeledBy(DependencyObject element, UIElement value) => element.SetValue(LabeledByProperty, value);
    }
}
