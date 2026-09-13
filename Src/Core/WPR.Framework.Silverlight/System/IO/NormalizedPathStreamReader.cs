using System.IO;
using WPR.Engine.Content;
using System.Text;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// <see cref="StreamReader"/> that normalises a Windows-style path before opening it.
    /// Subclass for the same reason as <see cref="NormalizedPathFileStream"/>.
    ///
    /// <para>Only the path-taking constructors are declared. The stream-taking overloads are
    /// deliberately absent: they have nothing to normalise, and declaring them would make the
    /// patcher's signature match ambiguous.</para>
    /// </summary>
    public class NormalizedPathStreamReader : StreamReader
    {
        public NormalizedPathStreamReader(string path)
            : base(ContentPaths.Resolve(path)!)
        {
        }

        public NormalizedPathStreamReader(string path, Encoding encoding)
            : base(ContentPaths.Resolve(path)!, encoding)
        {
        }

        public NormalizedPathStreamReader(string path, bool detectEncodingFromByteOrderMarks)
            : base(ContentPaths.Resolve(path)!, detectEncodingFromByteOrderMarks)
        {
        }
    }
}
