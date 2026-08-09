using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.PropertyPath</c>.
    /// Stores the dotted path; we don't resolve it (Bindings carry their own Path string).</summary>
    public class PropertyPath
    {
        public string Path { get; }
        public PropertyPath(string path) { Path = path; }
        public PropertyPath(object _) { Path = string.Empty; }
        public override string ToString() => Path;
    }
}
