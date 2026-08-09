using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.TemplatePartAttribute</c>. Marks a part name
    /// the control template is expected to expose. Designer hint only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class TemplatePartAttribute : Attribute
    {
        public string? Name { get; set; }
        public Type? Type { get; set; }
    }
}
