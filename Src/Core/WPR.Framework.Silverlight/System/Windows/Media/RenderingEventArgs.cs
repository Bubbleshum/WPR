using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.RenderingEventArgs</c>.</summary>
    public sealed class RenderingEventArgs : EventArgs
    {
        internal RenderingEventArgs(TimeSpan renderingTime) => RenderingTime = renderingTime;

        public TimeSpan RenderingTime { get; }
    }
}
