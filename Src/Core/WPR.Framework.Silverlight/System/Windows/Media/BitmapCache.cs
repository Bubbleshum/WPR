using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.BitmapCache</c>.</summary>
    public class BitmapCache : CacheMode
    {
        public double RenderAtScale { get; set; } = 1.0;
    }
}
