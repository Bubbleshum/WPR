using System.Xml.Linq;
using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Xml.Linq.XDocument</c>'s file-loading factories.
    /// See <see cref="ContentPaths"/>.
    /// </summary>
    public static class XDocument2
    {
        public static XDocument Load(string uri) =>
            XDocument.Load(ContentPaths.Resolve(uri)!);

        public static XDocument Load(string uri, LoadOptions options) =>
            XDocument.Load(ContentPaths.Resolve(uri)!, options);
    }
}
