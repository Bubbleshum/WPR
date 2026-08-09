using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability.Threading
{
    /// <summary>Shim for <c>System.Windows.Threading.DispatcherTimer</c>. Wraps a
    /// system timer so user code that schedules periodic callbacks doesn't break.</summary>
    public class DispatcherTimer
    {
        private System.Timers.Timer? _timer;

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
        public bool IsEnabled => _timer?.Enabled ?? false;

        public event EventHandler? Tick;

        public void Start()
        {
            Stop();
            _timer = new System.Timers.Timer(Math.Max(1, Interval.TotalMilliseconds));
            _timer.AutoReset = true;
            _timer.Elapsed += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
            _timer.Start();
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
    }
}
