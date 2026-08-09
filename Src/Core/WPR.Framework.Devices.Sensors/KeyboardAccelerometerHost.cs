using System;
using Microsoft.Xna.Framework;

namespace Microsoft.Devices.Sensors
{
    /// <summary>
    /// Direction the user wants to tilt the (virtual) phone. Forward = top edge moves away
    /// from the user (positive Y in WP7 axes), Backward = top edge moves toward the user.
    /// </summary>
    public enum TiltDirection
    {
        Left,
        Right,
        Forward,
        Backward,
    }

    /// <summary>
    /// Desktop-only keyboard-to-accelerometer bridge. The launcher (Silverlight or XNA host)
    /// translates configured keys into <see cref="NotifyTiltKey"/> calls; this class smooths
    /// the per-axis target toward the current value and fires <see cref="ReadingTick"/> at
    /// ~60Hz while at least one consumer (an <see cref="Accelerometer"/> or an overlay) has
    /// called <see cref="Acquire"/>. Consumers reading <see cref="CurrentReading"/> ad-hoc
    /// (e.g. an overlay's Render path) still see the latest smoothed value.
    /// </summary>
    public static class KeyboardAccelerometerHost
    {
        private static readonly object _gate = new object();
        private static bool _left, _right, _forward, _backward;
        // Screen-relative current tilt: +X = user's screen-right, +Y = user's screen-up
        // ("forward"). Reading is rotated into the device frame at build time based on the
        // current Orientation so a game in landscape sees readings consistent with its layout.
        private static double _currentX, _currentY;
        private static System.Timers.Timer? _timer;
        private static int _refCount;
        private static DateTime _lastTickUtc;

        /// <summary>
        /// Peak per-axis acceleration (in g-units) reached while a direction key is held.
        /// 0.7 ≈ sin(45°) — a comfortable tilt for menu navigation games. Reading clamps so
        /// the magnitude stays inside the unit sphere (so Z stays real).
        /// </summary>
        public static double Sensitivity { get; set; } = 0.7;

        /// <summary>
        /// Exponential approach rate (per second). Larger values mean the synthesized reading
        /// snaps to the new target faster after a key press/release. 8.0 ≈ ~125ms time-constant.
        /// </summary>
        public static double SmoothingPerSec { get; set; } = 8.0;

        /// <summary>
        /// Master switch — when false, all keys are ignored and the reported reading sits flat
        /// at (0,0,-1). Lets us keep the simulator paused if the user disables it entirely.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Display orientation of the game receiving the synthesized reading. Set by the host
        /// (Silverlight frame view or XNA input component) when the game flips orientation.
        /// Used to rotate the user's screen-relative key intent into the device-coord frame
        /// that the WP7 <c>Accelerometer</c> contract reports — without this, pressing the
        /// "tilt forward" key in a landscape game would tilt sideways from the game's POV.
        /// </summary>
        public static DisplayOrientation Orientation { get; set; } = DisplayOrientation.Portrait;

        /// <summary>
        /// Fired at ~60Hz while at least one consumer holds the simulation active via
        /// <see cref="Acquire"/>. Subscribers run on the timer thread — do not touch UI
        /// state without marshalling.
        /// </summary>
        public static event EventHandler<AccelerometerReading>? ReadingTick;

        /// <summary>
        /// Latest synthesized reading, rotated into the device-portrait frame the WP7
        /// Accelerometer contract expects games to read. Use this when handing the value
        /// to a user game.
        /// </summary>
        public static AccelerometerReading CurrentReading
        {
            get
            {
                lock (_gate)
                {
                    return BuildReading_NoLock(applyOrientation: true);
                }
            }
        }

        /// <summary>
        /// Latest synthesized reading in screen-relative coords — +X = screen-right, +Y =
        /// screen-up ("forward"). Use this for on-screen visualisation so the indicator dial
        /// always tracks the user's key presses (W moves the dot up regardless of whether
        /// the game is landscape) instead of mirroring the device-frame rotation.
        /// </summary>
        public static AccelerometerReading CurrentScreenReading
        {
            get
            {
                lock (_gate)
                {
                    return BuildReading_NoLock(applyOrientation: false);
                }
            }
        }

        /// <summary>True if any of the four tilt-direction keys is currently held down.</summary>
        public static bool AnyKeyDown
        {
            get { lock (_gate) { return _left || _right || _forward || _backward; } }
        }

        public static void NotifyTiltKey(TiltDirection dir, bool down)
        {
            lock (_gate)
            {
                switch (dir)
                {
                    case TiltDirection.Left:     _left = down;     break;
                    case TiltDirection.Right:    _right = down;    break;
                    case TiltDirection.Forward:  _forward = down;  break;
                    case TiltDirection.Backward: _backward = down; break;
                }
            }
        }

        /// <summary>
        /// Drop every "key down" state, reset the current reading to rest, and return the
        /// orientation to Portrait. Use when the host window loses focus or a game ends, so
        /// a missed key-release doesn't leave the virtual phone permanently tilted and a
        /// next-game launch doesn't inherit the previous title's orientation.
        /// </summary>
        public static void ResetAll()
        {
            lock (_gate)
            {
                _left = _right = _forward = _backward = false;
                _currentX = 0;
                _currentY = 0;
                Orientation = DisplayOrientation.Portrait;
            }
        }

        /// <summary>
        /// Begin (or refcount-up) the 60Hz simulation tick. Matching <see cref="Release"/> required.
        /// </summary>
        public static void Acquire()
        {
            lock (_gate)
            {
                _refCount++;
                if (_refCount == 1)
                {
                    _lastTickUtc = DateTime.UtcNow;
                    if (_timer == null)
                    {
                        _timer = new System.Timers.Timer { Interval = 16.0, AutoReset = true };
                        _timer.Elapsed += OnTick;
                    }
                    _timer.Start();
                }
            }
        }

        public static void Release()
        {
            lock (_gate)
            {
                if (_refCount <= 0) return;
                _refCount--;
                if (_refCount == 0)
                {
                    _timer?.Stop();
                }
            }
        }

        /// <summary>
        /// Drops ALL simulator state left behind by a game that has exited: clears the
        /// <see cref="ReadingTick"/> subscriber list, zeroes the refcount and stops the tick timer.
        /// The host MUST call this when a game shuts down.
        ///
        /// <para><b>Why (found 2026-08-08).</b> <c>Accelerometer.Start()</c> subscribes an INSTANCE
        /// method to this STATIC event and calls <see cref="Acquire"/>; only <c>Accelerometer.Stop()</c>
        /// undoes both, and WP7 games routinely just exit without stopping their sensors. The result was
        /// a permanent leak per launch: this static event kept the <c>Accelerometer</c> alive, which kept
        /// the GAME's own reading handlers alive, which pinned the game's collectible AssemblyLoadContext
        /// so it could never unload — and the 60 Hz timer kept running, invoking an ever-growing handler
        /// list, so CPU cost climbed with every game launched in a session (the user-visible "it slows
        /// the computer down"). Games cannot be relied on to clean up; the host must.</para>
        /// </summary>
        public static void ResetForNewLaunch()
        {
            lock (_gate)
            {
                ReadingTick = null;
                _refCount = 0;
                _timer?.Stop();
                _left = _right = _forward = _backward = false;
                _currentX = 0.0;
                _currentY = 0.0;
            }
        }

        private static AccelerometerReading BuildReading_NoLock(bool applyOrientation)
        {
            double xs = _currentX, ys = _currentY;
            double xd, yd;
            if (!applyOrientation)
            {
                // Screen-relative — used by on-screen overlays so visuals match keys 1:1.
                xd = xs; yd = ys;
            }
            else
            {
                // Rotation matrix from screen-intent to the device-frame reading the game
                // consumes. Calibrated against Hydro Thunder Go on desktop (LandscapeRight):
                //
                //   tilt up (W)    => "tilt left" in the game's reading
                //   tilt down (S)  => "tilt right"
                //   tilt left (A)  => the corresponding forward/back axis flip
                //   tilt right (D) => the opposite
                //
                // The signs differ from the textbook gravity-vector derivation because the
                // WP7 racing games we target invert at least one axis internally; the table
                // below is the one that produces correct in-game steering response.
                switch (Orientation)
                {
                    case DisplayOrientation.LandscapeLeft:
                        xd = -ys; yd =  xs; break;
                    case DisplayOrientation.LandscapeRight:
                        xd =  ys; yd = -xs; break;
                    case DisplayOrientation.Portrait:
                    default:
                        xd = xs; yd = ys; break;
                }
            }

            // Clamp into the unit disk so Z stays a real number.
            double mag2 = xd * xd + yd * yd;
            if (mag2 > 1.0)
            {
                double scale = 1.0 / Math.Sqrt(mag2);
                xd *= scale;
                yd *= scale;
                mag2 = 1.0;
            }
            double zd = -Math.Sqrt(1.0 - mag2);
            return new AccelerometerReading
            {
                Acceleration = new Vector3((float)xd, (float)yd, (float)zd),
                Timestamp = DateTimeOffset.Now,
            };
        }

        private static void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            AccelerometerReading r;
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                double dt = Math.Max(0.001, Math.Min(0.1, (now - _lastTickUtc).TotalSeconds));
                _lastTickUtc = now;

                double targetX = 0, targetY = 0;
                if (Enabled)
                {
                    if (_right)    targetX += Sensitivity;
                    if (_left)     targetX -= Sensitivity;
                    if (_forward)  targetY += Sensitivity;
                    if (_backward) targetY -= Sensitivity;
                }

                double alpha = 1.0 - Math.Exp(-SmoothingPerSec * dt);
                _currentX += (targetX - _currentX) * alpha;
                _currentY += (targetY - _currentY) * alpha;

                r = BuildReading_NoLock(applyOrientation: true);
            }
            try { ReadingTick?.Invoke(null, r); }
            catch { /* user-game handler throws shouldn't tear down the tick */ }
        }
    }
}
