using System.IO;
using WPR.Engine.Content;
using System.Text;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// <see cref="StreamWriter"/> that normalises a Windows-style path before opening it.
    /// Subclass for the same reason as <see cref="NormalizedPathFileStream"/>.
    /// </summary>
    public class NormalizedPathStreamWriter : StreamWriter
    {
        public NormalizedPathStreamWriter(string path)
            : base(ContentPaths.Normalize(path)!)
        {
        }

        public NormalizedPathStreamWriter(string path, bool append)
            : base(ContentPaths.Normalize(path)!, append)
        {
        }

        public NormalizedPathStreamWriter(string path, bool append, Encoding encoding)
            : base(ContentPaths.Normalize(path)!, append, encoding)
        {
        }
    }
}
