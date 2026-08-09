using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.StyleTypedPropertyAttribute</c>. Decorates a
    /// control class to advertise that one of its style-typed dependency properties
    /// should target a specific element type. Designer hint only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class StyleTypedPropertyAttribute : Attribute
    {
        public string? Property { get; set; }
        public Type? StyleTargetType { get; set; }
    }
}
