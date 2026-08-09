// Pointer-events → WP-style gestures bridge.
//
// Sits between Avalonia's PhoneApplicationFrameView pointer handlers and:
//   - Microsoft.Phone.Controls.Toolkit GestureListener attached events (Tap,
//     DoubleTap, Flick, DragStarted/Delta/Completed, Hold, etc.) — the form
//     user XAML reaches for: <toolkit:GestureService.GestureListener>
//     <toolkit:GestureListener Tap="OnTap"/></toolkit:GestureService.GestureListener>
//   - Panorama / Pivot internal swipe (separate path — see PanoramaInteraction)
//
// We don't try to faithfully model WP's inertia / friction. The interactions
// we drive are click-equivalent (tap) and horizontal-swipe-with-flick — enough
// for menu navigation and panorama paging.

using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Tracks one ongoing pointer interaction. Created in OnPointerPressed,
    /// updated in OnPointerMoved, finalised in OnPointerReleased — turns the
    /// stream of points into a tap or a flick. Held briefly on the side so the
    /// FrameView can dispatch the right gesture to the hit element.
    /// </summary>
    internal sealed class PointerInteraction
    {
        public Point StartPos;
        public Point LastPos;
        public DateTime StartTime;
        public DateTime LastMoveTime;
        public bool IsDragging;     // true once movement exceeded TapSlop
        public IReadOnlyList<UIElement>? HitChain;

        /// <summary>Threshold (in pixels) for a press-then-move to become a drag
        /// rather than a tap. Real WP uses ~12 device-independent units.</summary>
        public const double TapSlop = 12.0;

        /// <summary>Minimum velocity (px/sec) along an axis for the release to
        /// count as a flick.</summary>
        public const double FlickVelocity = 200.0;
    }
}
