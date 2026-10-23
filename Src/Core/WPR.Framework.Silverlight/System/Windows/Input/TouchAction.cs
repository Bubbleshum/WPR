using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Input.TouchAction</c>.</summary>
    /// <remarks>
    /// The values are Silverlight's, and they are load-bearing: game IL compares against the
    /// constants, not the names. The WP Toolkit's GestureListener tests <c>Action != 3</c> for "the
    /// finger is still down"; this enum used to start at 0, which made every point read as down
    /// and an Up as a Move.
    /// </remarks>
    public enum TouchAction
    {
        Down = 1,
        Move = 2,
        Up = 3,
    }
}
