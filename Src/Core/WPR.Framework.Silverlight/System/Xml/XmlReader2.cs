using System.Xml;
using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Xml.XmlReader</c>'s URI-taking factory methods.
    ///
    /// <para><b>This is the member the Battlewagon bug went through</b>, and the reason the fix
    /// could not stop at <see cref="global::System.IO.File"/>:
    /// <see cref="XmlReader.Create(string)"/> resolves its argument as a URI and opens the
    /// <see cref="global::System.IO.FileStream"/> deep inside <c>XmlDownloadManager</c>, so no
    /// amount of shimming the <c>System.IO</c> surface would ever see the path. See
    /// <see cref="ContentPaths"/>.</para>
    /// </summary>
    public static class XmlReader2
    {
        public static XmlReader Create(string inputUri) =>
            XmlReader.Create(ContentPaths.Resolve(inputUri)!);

        public static XmlReader Create(string inputUri, XmlReaderSettings? settings) =>
            XmlReader.Create(ContentPaths.Resolve(inputUri)!, settings);
    }
}
