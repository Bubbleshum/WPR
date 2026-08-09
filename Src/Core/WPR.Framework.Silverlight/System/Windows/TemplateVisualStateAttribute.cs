using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.TemplateVisualStateAttribute</c>. Marks a visual
    /// state the control template participates in. Designer hint only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class TemplateVisualStateAttribute : Attribute
    {
        public string? Name { get; set; }
        public string? GroupName { get; set; }
    }
}
