using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Marks the property that receives a type's default XAML children.
    /// Mirrors Silverlight's <c>System.Windows.Markup.ContentPropertyAttribute</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class ContentPropertyAttribute : Attribute
    {
        public string Name { get; }
        public ContentPropertyAttribute(string name) { Name = name; }
    }
}
