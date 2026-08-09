using System;

namespace WPR.SilverlightCompability.Markup
{
    /// <summary>
    /// Shim for <c>System.Windows.Markup.XmlnsPrefixAttribute</c>. Assembly-level
    /// hint mapping an xmlns URI to a recommended prefix. Designer hint only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class XmlnsPrefixAttribute : Attribute
    {
        public string XmlNamespace { get; }
        public string Prefix { get; }

        public XmlnsPrefixAttribute(string xmlNamespace, string prefix)
        {
            XmlNamespace = xmlNamespace;
            Prefix = prefix;
        }
    }
}
