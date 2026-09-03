using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using WPR.Xna.Rhi;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace WPR.Input.Keyboard
{
    /// <summary>
    /// Everything a desktop keyboard means, for all three devices it stands in for: tilt (over
    /// <see cref="KeyboardTiltBindings"/> and <see cref="KeyboardAccelerometerHost"/>), the hardware
    /// Back button, and synthetic touch gestures (over <see cref="KeyboardTouchBindings"/>).
    ///
    /// <para>The division is <b>mechanism below, meaning above</b>: the FNA backend reads the
    /// keyboard, measures the viewport and writes into the touch pipeline; everything that needs to
    /// know what a key <em>means</em> — which binding it matches, how a swipe is shaped over time —
    /// is here. That is why this side owns the gesture state machine and the backend only asks
    /// "where should the finger be this tick".</para>
    ///
    /// <para>Registered once at launcher startup from <c>ServicesSetup.Start()</c>, like every
    /// other head-supplied implementation. Android registers nothing: it has a real accelerometer
    /// and no keyboard, so the backend attaches no components and installs no touch injector.</para>
    /// </summary>
    public sealed class KeyboardEmulationHost : IKeyboardEmulationHost
    {
        /// <summary>
        /// Edge-detection state. It lives here rather than in the component because only this
        /// side knows which keys are interesting — the backend reports the whole pressed set and
        /// this decides what changed.
        ///
        /// <para>Per-instance and not reset between launches on purpose: one instance is
        /// registered for the process, and a launch begins with no keys held, so a stale "was
        /// down" would have to survive the game exiting mid-press. If that ever matters, reset in
        /// <see cref="PrepareForLaunch"/> — it already runs at exactly the right moment.</para>
        /// </summary>
        private bool _prevLeft, _prevRight, _prevForward, _prevBackward;

        public void PrepareForLaunch()
        {
            KeyboardTiltBindings.ApplyConfigurationToHost();

            // Per-game touch bindings. Loaded here rather than at startup because the file lives
            // in the game's own install folder, and PrepareForLaunch is the one moment that is
            // guaranteed to run after WprHostEnvironment.CurrentInstallFolder is set and before
            // the game can press anything.
            KeyboardTouchBindingFile.LoadForCurrentGame();
        }

        public bool IsOverlayEnabled => WPR.Common.Configuration.Current?.TiltOverlayEnabled == true;

        public void ReportOrientation(DisplayOrientation orientation) =>
            KeyboardAccelerometerHost.Orientation = orientation;

        public System.Numerics.Vector3 ScreenAcceleration => KeyboardAccelerometerHost.CurrentScreenAcceleration;

        /// <summary>
        /// Resolved per call against Configuration, so a Controls-page edit reaches a running
        /// game on the next keypress with no restart — the same rule the tilt bindings follow.
        /// </summary>
        public bool IsBackKey(XnaKeys key) => KeyboardBackBinding.IsBackKey(key);

        // ---- Synthetic touch -------------------------------------------------------------
        //
        // One gesture at a time, deliberately. Two overlapping synthetic fingers would land in
        // GestureDetector as a Pinch (it promotes any second finger), so a second key pressed
        // mid-gesture is ignored rather than queued — queuing would replay it later, out of
        // context, which is worse than dropping it.

        private readonly object _touchGate = new object();
        private KeyboardTouchBinding? _gesture;
        private System.Diagnostics.Stopwatch? _gestureClock;
        private bool _gestureStarted;
        private System.Numerics.Vector2 _lastTouchPos;

        public bool NotifyKeyDown(XnaKeys key)
        {
            if (!KeyboardTouchBindings.Any) return false;

            KeyboardTouchBinding? binding = KeyboardTouchBindings.Resolve(key.ToString());
            if (binding == null) return false;

            lock (_touchGate)
            {
                if (_gesture != null) return false;   // one at a time

                _gesture = binding;
                _gestureClock = System.Diagnostics.Stopwatch.StartNew();
                _gestureStarted = false;
                _lastTouchPos = new System.Numerics.Vector2(binding.StartX, binding.StartY);
            }

            System.Diagnostics.Trace.WriteLine("[wpr-input] gesture start " + binding);
            return true;
        }

        public SyntheticTouchSample AdvanceSyntheticTouch()
        {
            KeyboardTouchBinding binding;
            long elapsedMs;
            bool firstTick;

            lock (_touchGate)
            {
                if (_gesture == null || _gestureClock == null) return SyntheticTouchSample.Inactive;
                binding = _gesture;
                elapsedMs = _gestureClock.ElapsedMilliseconds;
                firstTick = !_gestureStarted;
                _gestureStarted = true;
            }

            // Floor at ~2 ticks at 60Hz so the press and the release never share a frame:
            // GestureDetector needs a frame in between to register anything, and SetFinger derives
            // Pressed-vs-Moved from the previous tick, so a single-frame gesture is invisible to
            // both channels. Applies to taps and swipes alike.
            int duration = Math.Max(binding.DurationMs, 34);

            float t = duration <= 0 ? 1f : Math.Min(1f, (float)elapsedMs / duration);

            System.Numerics.Vector2 from = new System.Numerics.Vector2(binding.StartX, binding.StartY);
            System.Numerics.Vector2 to = binding.Kind == KeyboardTouchGestureKind.Tap
                ? from
                : new System.Numerics.Vector2(binding.EndX, binding.EndY);

            System.Numerics.Vector2 pos = from + (to - from) * t;

            // The release is emitted on the tick the clock runs out, and the gesture is retired
            // only after that sample has been handed over — dropping it a tick earlier would lose
            // the Released edge and leave GestureDetector holding an open gesture forever.
            bool finished = elapsedMs >= duration;
            if (finished)
            {
                lock (_touchGate)
                {
                    _gesture = null;
                    _gestureClock = null;
                    _gestureStarted = false;
                }
            }

            System.Numerics.Vector2 delta = firstTick
                ? System.Numerics.Vector2.Zero
                : pos - _lastTouchPos;
            _lastTouchPos = pos;

            return new SyntheticTouchSample(
                active: true,
                position: new Vector2(pos.X, pos.Y),
                delta: new Vector2(delta.X, delta.Y),
                justPressed: firstTick,
                justReleased: finished);
        }

        public void ReportPressedKeys(XnaKeys[] pressedKeys)
        {
            bool left = false, right = false, forward = false, backward = false;

            if (pressedKeys != null)
            {
                foreach (XnaKeys k in pressedKeys)
                {
                    TiltDirection? dir = KeyboardTiltBindings.ResolveXnaKey(k);
                    if (!dir.HasValue) continue;
                    switch (dir.Value)
                    {
                        case TiltDirection.Left:     left = true;     break;
                        case TiltDirection.Right:    right = true;    break;
                        case TiltDirection.Forward:  forward = true;  break;
                        case TiltDirection.Backward: backward = true; break;
                    }
                }
            }

            // Transitions only — KeyboardAccelerometerHost models key-down/key-up, not held state.
            if (left     != _prevLeft)     KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Left,     left);
            if (right    != _prevRight)    KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Right,    right);
            if (forward  != _prevForward)  KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Forward,  forward);
            if (backward != _prevBackward) KeyboardAccelerometerHost.NotifyTiltKey(TiltDirection.Backward, backward);

            _prevLeft = left;
            _prevRight = right;
            _prevForward = forward;
            _prevBackward = backward;
        }
    }
}
