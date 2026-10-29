using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace System.Windows.Data
{
    /// <summary>Shim for <c>System.Windows.Data.RelativeSource</c>.</summary>
    public class RelativeSource
    {
        public RelativeSourceMode Mode { get; set; } = RelativeSourceMode.TemplatedParent;
        public RelativeSource() { }
        public RelativeSource(RelativeSourceMode mode) { Mode = mode; }
    }
}
