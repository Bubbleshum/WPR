using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Imaging.BitmapCreateOptions</c>.</summary>
    [Flags]
    public enum BitmapCreateOptions
    {
        None             = 0,
        DelayCreation    = 2,
        IgnoreImageCache = 8,
        BackgroundCreation = 0x10,
    }
}
