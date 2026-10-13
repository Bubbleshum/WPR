using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.MouseButtonEventArgs</c>.</summary>
    /// <remarks>
    /// Derives from <see cref="MouseEventArgs"/>, which holds the position and
    /// <see cref="MouseEventArgs.GetPosition"/> — see the remarks there for why the hierarchy has
    /// to match Silverlight's.
    /// </remarks>
    public class MouseButtonEventArgs : MouseEventArgs
    {
        /// <summary>Silverlight's click count; WP7 delivers single presses only.</summary>
        public int ClickCount => 1;
    }
}
