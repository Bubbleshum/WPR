using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Per-Panorama interaction state attached via a weak conditional map so we
    /// don't have to subclass <c>Microsoft.Phone.Controls.Panorama</c> (we can't —
    /// it's in the patched user-side DLL). Renderer reads <see cref="CurrentIndex"/>
    /// to decide which PanoramaItem to draw; the pointer pipeline updates it via
    /// <see cref="Advance"/> when a horizontal flick lands.
    /// </summary>
    internal sealed class PanoramaState
    {
        /// <summary>Currently-visible item index. Clamped to [0, count-1] in Advance.</summary>
        public int CurrentIndex;

        /// <summary>Horizontal drag offset in pixels — non-zero while the user
        /// is mid-drag. Renderer applies this as a translation so the swipe
        /// feels live; reset to 0 when the gesture ends.</summary>
        public double DragOffset;

        /// <summary>True while a pointer is down and the drag has crossed the
        /// tap-slop threshold horizontally — locks subsequent moves into the
        /// pan path rather than letting them dispatch as taps to children.</summary>
        public bool IsDragging;

        public void Advance(int delta, int childCount)
        {
            if (childCount <= 0) return;
            int next = CurrentIndex + delta;
            if (next < 0) next = 0;
            if (next >= childCount) next = childCount - 1;
            CurrentIndex = next;
            DragOffset = 0;
            IsDragging = false;
        }
    }
}
