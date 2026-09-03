using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA.Input
{
    /// <summary>
    /// Wraps the real <see cref="IInputBackend"/> and writes a synthesised finger into the touch
    /// pipeline after the platform drain has run, so a keyboard (and later a gamepad) can stand in
    /// for taps and swipes.
    ///
    /// <para><b>Why a decorator and not a GameComponent — this is the trap.</b>
    /// <c>TouchPanel.Update()</c> runs <c>GestureDetector.OnUpdate()</c>, snapshots the previous
    /// touches, and only THEN calls <c>UpdateTouchPanelState()</c>, which writes real fingers and
    /// clears every slot it owns. A component runs earlier in the tick, so anything it wrote is
    /// erased in the same frame: the state would never appear in <c>TouchPanel.GetState()</c>
    /// while gestures, which take a different route, would work — making it look as though the
    /// touch plumbing itself was broken. Sitting inside the drain call makes this the last writer
    /// by construction, every frame, with no ordering left to get wrong. The keyboard-tilt
    /// emulator IS a component, so that precedent does not transfer here.</para>
    ///
    /// <para><b>Both channels are written.</b> <c>SetFinger</c> fills <c>GetState()</c>;
    /// <c>INTERNAL_onTouchEvent</c> feeds <c>GestureDetector</c>, i.e. <c>ReadGesture()</c>. They
    /// are not connected to each other, and WP7 titles use both.</para>
    ///
    /// <para>Everything that is not touch forwards unchanged, so this stays transparent to the
    /// rest of the RHI.</para>
    /// </summary>
    internal sealed class SyntheticTouchInputBackend : IInputBackend
    {
        /// <summary>
        /// Finger id for the synthetic touch. Sits just below the mouse-as-touch id
        /// (<c>int.MaxValue</c>) and far above the small positive ids SDL gives real fingers, so a
        /// game tracking touches by <c>TouchLocation.Id</c> can never confuse the three.
        ///
        /// <para>Constant for the life of a gesture on purpose: <c>GestureDetector</c> tracks one
        /// active finger id and abandons the gesture if it changes mid-drag.</para>
        /// </summary>
        private const int SyntheticFingerId = int.MaxValue - 1;

        private readonly IInputBackend _inner;
        private readonly IKeyboardEmulationHost _host;
        private bool _wasDown;
        private int _traceCount;

        internal SyntheticTouchInputBackend(IInputBackend inner, IKeyboardEmulationHost host)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _host = host ?? throw new ArgumentNullException(nameof(host));

            // Claim one slot below the mouse slot for the life of the launch. The platform drain
            // reads this to know where to stop writing and clearing.
            TouchPanel.ReservedFingerSlots = 1;

            // FrameworkDispatcher only pumps TouchPanel.Update() when a touch device is believed to
            // exist. On desktop MouseAsTouch already sets this, but a head that turns the mouse path
            // off, or a device with no touchscreen, would otherwise never tick the pipeline and the
            // injector would silently do nothing.
            TouchPanel.TouchDeviceExists = true;
        }

        public void UpdateTouchPanelState()
        {
            // Real fingers and the mouse first: this writes slots 0..FirstReservedFingerSlot-1 plus
            // the mouse slot, and clears what it does not use. Our slot is excluded by the
            // reservation, so what we write below survives the tick.
            _inner.UpdateTouchPanelState();

            int slot = TouchPanel.FirstReservedFingerSlot;
            SyntheticTouchSample sample = _host.AdvanceSyntheticTouch();

            // Stay out of the way of a real touch. GestureDetector promotes any second finger to a
            // Pinch, so overlapping would emit gestures the user never performed. A gesture already
            // in flight is allowed to finish, because dropping it would lose its Released edge.
            if (sample.Active && !_wasDown && TouchPanel.HasRealTouchDown)
            {
                TouchPanel.SetFinger(slot, TouchPanel.NO_FINGER, Vector2.Zero);
                return;
            }

            if (!sample.Active)
            {
                if (_wasDown)
                {
                    // Nothing running any more: let SetFinger turn the previous Pressed/Moved into
                    // a Released so GetState() reports the lift.
                    TouchPanel.SetFinger(slot, TouchPanel.NO_FINGER, Vector2.Zero);
                    _wasDown = false;
                }
                return;
            }

            // --- state channel ---
            TouchPanel.SetFinger(slot, SyntheticFingerId, sample.Position);

            // --- gesture channel ---
            // INTERNAL_onTouchEvent takes NORMALISED coordinates and scales them by
            // DisplayWidth/Height itself, whereas SetFinger above takes display space. Passing
            // display coords here would put the gesture off screen by a factor of the display size.
            float w = TouchPanel.DisplayWidth > 0 ? TouchPanel.DisplayWidth : 1;
            float h = TouchPanel.DisplayHeight > 0 ? TouchPanel.DisplayHeight : 1;

            TouchLocationState state =
                sample.JustPressed ? TouchLocationState.Pressed :
                sample.JustReleased ? TouchLocationState.Released :
                                      TouchLocationState.Moved;

            TouchPanel.INTERNAL_onTouchEvent(
                SyntheticFingerId,
                state,
                sample.Position.X / w,
                sample.Position.Y / h,
                sample.Delta.X / w,
                sample.Delta.Y / h);

            if (_traceCount < 20 && state != TouchLocationState.Moved)
            {
                _traceCount += 1;
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-input] synthetic touch {state} at ({sample.Position.X:F0},{sample.Position.Y:F0}) slot={slot}");
            }

            _wasDown = !sample.JustReleased;
        }

        // ---- everything else forwards unchanged ----
        public Keys GetKeyFromScancode(Keys scancode) => _inner.GetKeyFromScancode(scancode);
        public void StartTextInput() => _inner.StartTextInput();
        public void StopTextInput() => _inner.StopTextInput();
        public void SetTextInputRectangle(Rectangle rectangle) => _inner.SetTextInputRectangle(rectangle);
        public void GetMouseState(IntPtr window, out int x, out int y, out ButtonState left, out ButtonState middle, out ButtonState right, out ButtonState x1, out ButtonState x2)
            => _inner.GetMouseState(window, out x, out y, out left, out middle, out right, out x1, out x2);
        public void SetMousePosition(IntPtr window, int x, int y) => _inner.SetMousePosition(window, x, y);
        public bool GetRelativeMouseMode() => _inner.GetRelativeMouseMode();
        public void SetRelativeMouseMode(bool enable) => _inner.SetRelativeMouseMode(enable);
        public GamePadCapabilities GetGamePadCapabilities(int index) => _inner.GetGamePadCapabilities(index);
        public GamePadState GetGamePadState(int index, GamePadDeadZone deadZoneMode) => _inner.GetGamePadState(index, deadZoneMode);
        public bool SetGamePadVibration(int index, float leftMotor, float rightMotor) => _inner.SetGamePadVibration(index, leftMotor, rightMotor);
        public bool SetGamePadTriggerVibration(int index, float leftTrigger, float rightTrigger) => _inner.SetGamePadTriggerVibration(index, leftTrigger, rightTrigger);
        public string GetGamePadGUID(int index) => _inner.GetGamePadGUID(index);
        public void SetGamePadLightBar(int index, Color color) => _inner.SetGamePadLightBar(index, color);
        public bool GetGamePadGyro(int index, out Vector3 gyro) => _inner.GetGamePadGyro(index, out gyro);
        public bool GetGamePadAccelerometer(int index, out Vector3 accel) => _inner.GetGamePadAccelerometer(index, out accel);
        public TouchPanelCapabilities GetTouchCapabilities() => _inner.GetTouchCapabilities();
    }
}
