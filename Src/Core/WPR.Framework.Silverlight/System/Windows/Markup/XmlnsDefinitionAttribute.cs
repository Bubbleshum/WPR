using System;

namespace WPR.SilverlightCompability.Markup
{
    /// <summary>
    /// Shim for <c>System.Windows.Markup.XmlnsDefinitionAttribute</c>. Assembly-level
    /// declaration that maps an xmlns URI to a CLR namespace. Designer/XAML loader
    /// hint — our XamlReader uses its own resolution, so this attribute is inert.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class XmlnsDefinitionAttribute : Attribute
    {
        public string XmlNamespace { get; }
        public string ClrNamespace { get; }
        public string? AssemblyName { get; set; }

        public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace)
        {
            XmlNamespace = xmlNamespace;
            ClrNamespace = clrNamespace;
        }
    }
}
