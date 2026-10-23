using System;
using global::System.Collections.Generic;
using global::System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.UI.Input;

namespace Windows.Foundation
{
    /// <summary>Shim for <c>Windows.Foundation.TypedEventHandler`2</c>.</summary>
    public delegate void TypedEventHandler<TSender, TResult>(TSender sender, TResult args);
}

namespace Windows.Devices.Input
{
    /// <summary>Shim for <c>Windows.Devices.Input.PointerDeviceType</c>.</summary>
    public enum PointerDeviceType
    {
        Touch = 0,
        Pen = 1,
        Mouse = 2,
    }

    /// <summary>Shim for <c>Windows.Devices.Input.PointerDevice</c>: a phone's touchscreen.</summary>
    public sealed class PointerDevice
    {
        internal static readonly PointerDevice Touchscreen = new();

        private PointerDevice()
        {
        }

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public bool IsIntegrated => true;

        public uint MaxContacts => 8;
    }
}

namespace Windows.System
{
    /// <summary>Shim for <c>Windows.System.VirtualKeyModifiers</c>.</summary>
    [Flags]
    public enum VirtualKeyModifiers : uint
    {
        None = 0,
        Control = 1,
        Menu = 2,
        Shift = 4,
        Windows = 8,
    }
}

namespace Windows.UI.Core
{
    /// <summary>Shim for <c>Windows.UI.Core.PointerEventArgs</c>.</summary>
    public sealed class PointerEventArgs
    {
        internal PointerEventArgs(PointerPoint point) => CurrentPoint = point;

        public PointerPoint CurrentPoint { get; }

        public Windows.System.VirtualKeyModifiers KeyModifiers => Windows.System.VirtualKeyModifiers.None;

        public bool Handled { get; private set; }

        public void put_Handled(bool value) => Handled = value;

        public IList<PointerPoint> GetIntermediatePoints() => new List<PointerPoint> { CurrentPoint };
    }
}

namespace Windows.UI.Input
{
    /// <summary>Shim for <c>Windows.UI.Input.GestureSettings</c>.</summary>
    [Flags]
    public enum GestureSettings : uint
    {
        None = 0,
        Tap = 1,
        DoubleTap = 2,
        Hold = 4,
        HoldWithMouse = 8,
        RightTap = 16,
        Drag = 32,
        ManipulationTranslateX = 64,
        ManipulationTranslateY = 128,
        ManipulationTranslateRailsX = 256,
        ManipulationTranslateRailsY = 512,
        ManipulationRotate = 1024,
        ManipulationScale = 2048,
        ManipulationTranslateInertia = 4096,
        ManipulationRotateInertia = 8192,
        ManipulationScaleInertia = 16384,
        CrossSlide = 32768,
        ManipulationMultipleFingerPanning = 65536,
    }

    /// <summary>Shim for <c>Windows.UI.Input.HoldingState</c>.</summary>
    public enum HoldingState
    {
        Started = 0,
        Completed = 1,
        Canceled = 2,
    }

    /// <summary>Shim for <c>Windows.UI.Input.CrossSlidingState</c>.</summary>
    public enum CrossSlidingState
    {
        Started = 0,
        Dragging = 1,
        Selecting = 2,
        SelectSpeedBumping = 3,
        SpeedBumping = 4,
        Rearranging = 5,
        Completed = 6,
    }

    /// <summary>Shim for <c>Windows.UI.Input.ManipulationDelta</c> (a WinRT struct: public fields).</summary>
    public struct ManipulationDelta
    {
        public Point Translation;
        public float Scale;
        public float Rotation;
        public float Expansion;
    }

    /// <summary>Shim for <c>Windows.UI.Input.ManipulationVelocities</c>.</summary>
    public struct ManipulationVelocities
    {
        public Point Linear;
        public float Angular;
        public float Expansion;
    }

    /// <summary>Shim for <c>Windows.UI.Input.CrossSlideThresholds</c>.</summary>
    public struct CrossSlideThresholds
    {
        public float SelectionStart;
        public float SpeedBumpStart;
        public float SpeedBumpEnd;
        public float RearrangeStart;
    }

    /// <summary>Shim for <c>Windows.UI.Input.PointerPointProperties</c>.</summary>
    public sealed class PointerPointProperties
    {
        internal PointerPointProperties(bool primary, bool inContact)
        {
            IsPrimary = primary;
            IsLeftButtonPressed = inContact;
        }

        public bool IsPrimary { get; }

        public bool IsInRange => true;

        public bool IsLeftButtonPressed { get; }

        public bool IsRightButtonPressed => false;

        public bool IsMiddleButtonPressed => false;

        public int MouseWheelDelta => 0;

        public float Pressure => IsLeftButtonPressed ? 0.5f : 0f;

        public bool IsCanceled => false;
    }

    /// <summary>Shim for <c>Windows.UI.Input.PointerPoint</c>: one touch sample.</summary>
    public sealed class PointerPoint
    {
        private static uint _frame;

        internal PointerPoint(uint id, Point position, bool inContact, bool primary)
        {
            PointerId = id;
            Position = position;
            RawPosition = position;
            IsInContact = inContact;
            Properties = new PointerPointProperties(primary, inContact);
            Timestamp = (ulong)(global::System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / global::System.Diagnostics.Stopwatch.Frequency));
            FrameId = ++_frame;
        }

        public uint PointerId { get; }

        public Point Position { get; }

        public Point RawPosition { get; }

        public bool IsInContact { get; }

        public ulong Timestamp { get; }

        public uint FrameId { get; }

        public PointerPointProperties Properties { get; }

        public PointerDevice PointerDevice => PointerDevice.Touchscreen;
    }

    /// <summary>Shim for <c>Windows.UI.Input.TappedEventArgs</c>.</summary>
    public sealed class TappedEventArgs
    {
        internal TappedEventArgs(Point position, uint count)
        {
            Position = position;
            TapCount = count;
        }

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public Point Position { get; }

        public uint TapCount { get; }
    }

    /// <summary>Shim for <c>Windows.UI.Input.RightTappedEventArgs</c>.</summary>
    public sealed class RightTappedEventArgs
    {
        internal RightTappedEventArgs(Point position) => Position = position;

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public Point Position { get; }
    }

    /// <summary>Shim for <c>Windows.UI.Input.HoldingEventArgs</c>.</summary>
    public sealed class HoldingEventArgs
    {
        internal HoldingEventArgs(Point position, HoldingState state)
        {
            Position = position;
            HoldingState = state;
        }

        public HoldingState HoldingState { get; }

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public Point Position { get; }
    }

    /// <summary>Shim for <c>Windows.UI.Input.CrossSlidingEventArgs</c>. Never raised: WPR's recogniser does not cross-slide.</summary>
    public sealed class CrossSlidingEventArgs
    {
        internal CrossSlidingEventArgs()
        {
        }

        public CrossSlidingState CrossSlidingState => CrossSlidingState.Completed;

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public Point Position => default;
    }

    /// <summary>Shim for <c>Windows.UI.Input.ManipulationStartedEventArgs</c>.</summary>
    public sealed class ManipulationStartedEventArgs
    {
        internal ManipulationStartedEventArgs(Point position, ManipulationDelta cumulative)
        {
            Position = position;
            Cumulative = cumulative;
        }

        public ManipulationDelta Cumulative { get; }

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public Point Position { get; }
    }

    /// <summary>Shim for <c>Windows.UI.Input.ManipulationUpdatedEventArgs</c>.</summary>
    public sealed class ManipulationUpdatedEventArgs
    {
        internal ManipulationUpdatedEventArgs(Point position, ManipulationDelta delta, ManipulationDelta cumulative, ManipulationVelocities velocities)
        {
            Position = position;
            Delta = delta;
            Cumulative = cumulative;
            Velocities = velocities;
        }

        public ManipulationDelta Cumulative { get; }

        public ManipulationDelta Delta { get; }

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public Point Position { get; }

        public ManipulationVelocities Velocities { get; }
    }

    /// <summary>Shim for <c>Windows.UI.Input.ManipulationCompletedEventArgs</c>.</summary>
    public sealed class ManipulationCompletedEventArgs
    {
        internal ManipulationCompletedEventArgs(Point position, ManipulationDelta cumulative, ManipulationVelocities velocities)
        {
            Position = position;
            Cumulative = cumulative;
            Velocities = velocities;
        }

        public ManipulationDelta Cumulative { get; }

        public PointerDeviceType PointerDeviceType => PointerDeviceType.Touch;

        public Point Position { get; }

        public ManipulationVelocities Velocities { get; }
    }

    /// <summary>
    /// Shim for <c>Windows.UI.Input.GestureRecognizer</c>: taps, holds and one-finger
    /// translation manipulations from the pointer events a title feeds it. WinRT's recogniser
    /// also did pinch, rotation, inertia and cross-slide; those are not raised here.
    /// </summary>
    /// <remarks>
    /// Thresholds are WinRT's: movement within <see cref="TapSlop"/> DIPs is still a tap; a
    /// press held <see cref="HoldTime"/> without moving is a hold, reported when it lifts (WinRT
    /// reported Started while the finger was still down, from a timer: a title fed only on
    /// pointer events by WPR has no thread to raise it from).
    /// </remarks>
    public sealed class GestureRecognizer
    {
        private const double TapSlop = 10;
        private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(800);

        private EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, TappedEventArgs>>? _tapped;
        private EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, RightTappedEventArgs>>? _rightTapped;
        private EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, HoldingEventArgs>>? _holding;
        private EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, CrossSlidingEventArgs>>? _crossSliding;
        private EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, ManipulationStartedEventArgs>>? _started;
        private EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, ManipulationUpdatedEventArgs>>? _updated;
        private EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, ManipulationCompletedEventArgs>>? _completed;

        private uint? _pointer;
        private Point _start;
        private Point _last;
        private ulong _startTime;
        private ulong _lastTime;
        private bool _manipulating;
        private ManipulationVelocities _velocity;

        public GestureSettings GestureSettings { get; private set; }

        public void put_GestureSettings(GestureSettings value) => GestureSettings = value;

        public bool IsActive => _pointer.HasValue;

        public bool IsInertial => false;

        public bool CrossSlideExact { get; private set; }

        public void put_CrossSlideExact(bool value) => CrossSlideExact = value;

        public bool CrossSlideHorizontally { get; private set; }

        public void put_CrossSlideHorizontally(bool value) => CrossSlideHorizontally = value;

        public CrossSlideThresholds CrossSlideThresholds { get; private set; }

        public void put_CrossSlideThresholds(CrossSlideThresholds value) => CrossSlideThresholds = value;

        public bool ShowGestureFeedback { get; private set; } = true;

        public void put_ShowGestureFeedback(bool value) => ShowGestureFeedback = value;

        public bool ManipulationExact { get; private set; }

        public void put_ManipulationExact(bool value) => ManipulationExact = value;

        public bool AutoProcessInertia { get; private set; } = true;

        public void put_AutoProcessInertia(bool value) => AutoProcessInertia = value;

        public EventRegistrationToken add_Tapped(TypedEventHandler<GestureRecognizer, TappedEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, TappedEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _tapped).AddEventHandler(handler);

        public void remove_Tapped(EventRegistrationToken token) => _tapped?.RemoveEventHandler(token);

        public EventRegistrationToken add_RightTapped(TypedEventHandler<GestureRecognizer, RightTappedEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, RightTappedEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _rightTapped).AddEventHandler(handler);

        public void remove_RightTapped(EventRegistrationToken token) => _rightTapped?.RemoveEventHandler(token);

        public EventRegistrationToken add_Holding(TypedEventHandler<GestureRecognizer, HoldingEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, HoldingEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _holding).AddEventHandler(handler);

        public void remove_Holding(EventRegistrationToken token) => _holding?.RemoveEventHandler(token);

        public EventRegistrationToken add_CrossSliding(TypedEventHandler<GestureRecognizer, CrossSlidingEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, CrossSlidingEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _crossSliding).AddEventHandler(handler);

        public void remove_CrossSliding(EventRegistrationToken token) => _crossSliding?.RemoveEventHandler(token);

        public EventRegistrationToken add_ManipulationStarted(TypedEventHandler<GestureRecognizer, ManipulationStartedEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, ManipulationStartedEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _started).AddEventHandler(handler);

        public void remove_ManipulationStarted(EventRegistrationToken token) => _started?.RemoveEventHandler(token);

        public EventRegistrationToken add_ManipulationUpdated(TypedEventHandler<GestureRecognizer, ManipulationUpdatedEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, ManipulationUpdatedEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _updated).AddEventHandler(handler);

        public void remove_ManipulationUpdated(EventRegistrationToken token) => _updated?.RemoveEventHandler(token);

        public EventRegistrationToken add_ManipulationCompleted(TypedEventHandler<GestureRecognizer, ManipulationCompletedEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<GestureRecognizer, ManipulationCompletedEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _completed).AddEventHandler(handler);

        public void remove_ManipulationCompleted(EventRegistrationToken token) => _completed?.RemoveEventHandler(token);

        public bool CanBeDoubleTap(PointerPoint value) => false;

        public void ProcessDownEvent(PointerPoint value)
        {
            if (_pointer.HasValue)
            {
                return; // one finger at a time
            }

            _pointer = value.PointerId;
            _start = _last = value.Position;
            _startTime = _lastTime = value.Timestamp;
            _manipulating = false;
            _velocity = default;
        }

        public void ProcessMoveEvents(IList<PointerPoint> value)
        {
            foreach (PointerPoint point in value)
            {
                if (point.PointerId != _pointer)
                {
                    continue;
                }

                if (!_manipulating && Distance(point.Position, _start) > TapSlop && WantsManipulation)
                {
                    _manipulating = true;
                    _started?.InvocationList?.Invoke(this, new ManipulationStartedEventArgs(_start, Cumulative(_start)));
                }

                if (_manipulating)
                {
                    double seconds = Math.Max(1e-3, (point.Timestamp - _lastTime) / 1_000_000.0);
                    var delta = Delta(_last, point.Position);
                    _velocity = new ManipulationVelocities
                    {
                        // WinRT reports DIPs per millisecond.
                        Linear = new Point(delta.Translation.X / (seconds * 1000), delta.Translation.Y / (seconds * 1000)),
                    };
                    _updated?.InvocationList?.Invoke(this, new ManipulationUpdatedEventArgs(point.Position, delta, Cumulative(point.Position), _velocity));
                }

                _last = point.Position;
                _lastTime = point.Timestamp;
            }
        }

        public void ProcessUpEvent(PointerPoint value)
        {
            if (value.PointerId != _pointer)
            {
                return;
            }

            if (_manipulating)
            {
                _completed?.InvocationList?.Invoke(this, new ManipulationCompletedEventArgs(value.Position, Cumulative(value.Position), _velocity));
            }
            else if (Distance(value.Position, _start) <= TapSlop)
            {
                bool held = TimeSpan.FromTicks((long)(value.Timestamp - _startTime) * 10) >= HoldTime;
                if (held && (GestureSettings & GestureSettings.Hold) != 0)
                {
                    _holding?.InvocationList?.Invoke(this, new HoldingEventArgs(_start, HoldingState.Started));
                    _holding?.InvocationList?.Invoke(this, new HoldingEventArgs(_start, HoldingState.Completed));
                }
                else if ((GestureSettings & GestureSettings.Tap) != 0)
                {
                    _tapped?.InvocationList?.Invoke(this, new TappedEventArgs(value.Position, 1));
                }
            }

            _pointer = null;
            _manipulating = false;
        }

        public void ProcessMouseWheelEvent(PointerPoint value, bool isShiftKeyDown, bool isControlKeyDown)
        {
        }

        public void ProcessInertia()
        {
        }

        public void CompleteGesture()
        {
            _pointer = null;
            _manipulating = false;
        }

        private bool WantsManipulation => (GestureSettings & (GestureSettings.ManipulationTranslateX | GestureSettings.ManipulationTranslateY
            | GestureSettings.ManipulationTranslateRailsX | GestureSettings.ManipulationTranslateRailsY | GestureSettings.Drag
            | GestureSettings.ManipulationScale | GestureSettings.ManipulationRotate)) != 0;

        private ManipulationDelta Cumulative(Point at) => Delta(_start, at);

        private static ManipulationDelta Delta(Point from, Point to) => new()
        {
            Translation = new Point(to.X - from.X, to.Y - from.Y),
            Scale = 1f,
        };

        private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
    }
}

namespace Windows.Phone.Input.Interop
{
    using Windows.UI.Core;

    /// <summary>Shim for <c>Windows.Phone.Input.Interop.IDrawingSurfaceManipulationHandler</c>.</summary>
    public interface IDrawingSurfaceManipulationHandler
    {
        void SetManipulationHost(DrawingSurfaceManipulationHost manipulationHost);
    }

    /// <summary>
    /// Shim for <c>Windows.Phone.Input.Interop.DrawingSurfaceManipulationHost</c>: the pointer
    /// events of a <c>DrawingSurfaceBackgroundGrid</c>, which WPR raises from its touch input
    /// (<see cref="WPR.WinRT.ManipulationInput"/>).
    /// </summary>
    public sealed class DrawingSurfaceManipulationHost
    {
        private EventRegistrationTokenTable<TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs>>? _pressed;
        private EventRegistrationTokenTable<TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs>>? _moved;
        private EventRegistrationTokenTable<TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs>>? _released;

        internal DrawingSurfaceManipulationHost()
        {
        }

        public EventRegistrationToken add_PointerPressed(TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _pressed).AddEventHandler(handler);

        public void remove_PointerPressed(EventRegistrationToken token) => _pressed?.RemoveEventHandler(token);

        public EventRegistrationToken add_PointerMoved(TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _moved).AddEventHandler(handler);

        public void remove_PointerMoved(EventRegistrationToken token) => _moved?.RemoveEventHandler(token);

        public EventRegistrationToken add_PointerReleased(TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs> handler) => EventRegistrationTokenTable<TypedEventHandler<DrawingSurfaceManipulationHost, PointerEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _released).AddEventHandler(handler);

        public void remove_PointerReleased(EventRegistrationToken token) => _released?.RemoveEventHandler(token);

        internal void Raise(int action, uint id, double x, double y, bool primary)
        {
            var args = new PointerEventArgs(new PointerPoint(id, new Point(x, y), action != 2, primary));
            var list = action switch { 0 => _pressed, 1 => _moved, _ => _released };
            list?.InvocationList?.Invoke(this, args);
        }
    }
}

namespace WPR.WinRT
{
    using Windows.Phone.Input.Interop;

    /// <summary>
    /// Feeds the host's touch input to a title's <see cref="DrawingSurfaceManipulationHost"/>.
    /// <see cref="Attach"/> is called with what the title passes to
    /// <c>DrawingSurfaceBackgroundGrid.SetBackgroundManipulationHandler</c>; <see cref="Report"/>
    /// once per finger sample, in page coordinates (DIPs).
    /// </summary>
    public static class ManipulationInput
    {
        private static DrawingSurfaceManipulationHost? _host;
        private static uint? _primary;
        private static int _traced;

        public static bool IsAttached => _host != null;

        /// <summary>Hands <paramref name="handler"/> its host; false when it is not a manipulation handler.</summary>
        public static bool Attach(object? handler)
        {
            if (handler is not IDrawingSurfaceManipulationHandler h)
            {
                if (_host != null) System.Diagnostics.Trace.WriteLine("[wpr-input] background manipulation handler cleared");
                _host = null;
                return false;
            }

            var host = new DrawingSurfaceManipulationHost();
            h.SetManipulationHost(host);
            _host = host;
            System.Diagnostics.Trace.WriteLine($"[wpr-input] background manipulation handler {handler.GetType().FullName} attached");
            return true;
        }

        /// <summary>One sample: <paramref name="action"/> 0 pressed, 1 moved, 2 released.</summary>
        public static void Report(int id, int action, double x, double y)
        {
            DrawingSurfaceManipulationHost? host = _host;
            if (host == null)
            {
                return;
            }

            uint pointer = unchecked((uint)id);
            if (action == 0 && _primary == null)
            {
                _primary = pointer;
            }

            bool primary = _primary == pointer;
            if (action != 1 && _traced < 20)
            {
                _traced++;
                System.Diagnostics.Trace.WriteLine($"[wpr-input] background pointer {(action == 0 ? "pressed" : "released")} id={pointer} at ({x:F0},{y:F0}) primary={primary}");
            }

            host.Raise(action, pointer, x, y, primary);
            if (action == 2 && primary)
            {
                _primary = null;
            }
        }

        public static void Reset()
        {
            _host = null;
            _primary = null;
            _traced = 0;
        }
    }
}
