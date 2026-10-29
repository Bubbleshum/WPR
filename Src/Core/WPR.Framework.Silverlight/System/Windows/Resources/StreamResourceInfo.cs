using System.IO;

namespace System.Windows.Resources
{
    /// <summary>Shim for <c>System.Windows.Resources.StreamResourceInfo</c>: a retrieved resource stream and its content type.</summary>
    public class StreamResourceInfo
    {
        public Stream? Stream { get; set; }
        public string? ContentType { get; set; }
    }
}
