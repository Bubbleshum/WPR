using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Duration</c>. In Silverlight this is a tri-state
    /// value used by Storyboard / animations: a real <see cref="TimeSpan"/>, the
    /// special <see cref="Automatic"/> ("let the system pick"), or
    /// <see cref="Forever"/>. The renderer doesn't run timelines yet, but
    /// Storyboard-bearing XAML and any user IL that touches Duration (Minesweeper's
    /// MainPage InitializeComponent does) needs the type to resolve and the usual
    /// members to JIT. Behaviour mirrors WPF/SL semantics enough for comparisons
    /// and TimeSpan→Duration assignments to compile and execute.
    /// </summary>
    public struct Duration : IEquatable<Duration>
    {
        // Tri-state: 0 = uninitialized (treated as Automatic), 1 = HasTimeSpan,
        // 2 = Automatic (explicit), 3 = Forever. Default(Duration) matches
        // Silverlight where the parameterless struct equals Automatic.
        private readonly int _kind;
        private readonly TimeSpan _timeSpan;

        public Duration(TimeSpan timeSpan)
        {
            if (timeSpan < TimeSpan.Zero)
                throw new ArgumentException("Duration cannot be negative.", nameof(timeSpan));
            _kind = 1;
            _timeSpan = timeSpan;
        }

        private Duration(int kind)
        {
            _kind = kind;
            _timeSpan = default;
        }

        public static Duration Automatic => new Duration(2);
        public static Duration Forever => new Duration(3);

        public bool HasTimeSpan => _kind == 1;

        public TimeSpan TimeSpan
        {
            get
            {
                if (_kind != 1)
                    throw new InvalidOperationException("Duration does not have a TimeSpan.");
                return _timeSpan;
            }
        }

        public static implicit operator Duration(TimeSpan timeSpan) => new Duration(timeSpan);

        public Duration Add(Duration duration)
        {
            if (HasTimeSpan && duration.HasTimeSpan)
                return new Duration(_timeSpan + duration._timeSpan);
            // Forever + anything finite = Forever; Automatic dominates otherwise.
            if (_kind == 3 || duration._kind == 3) return Forever;
            return Automatic;
        }

        public Duration Subtract(Duration duration)
        {
            if (HasTimeSpan && duration.HasTimeSpan)
                return new Duration(_timeSpan - duration._timeSpan);
            return Automatic;
        }

        public bool Equals(Duration other)
        {
            if (_kind != other._kind) return false;
            return _kind != 1 || _timeSpan == other._timeSpan;
        }

        public override bool Equals(object? obj) => obj is Duration d && Equals(d);

        public override int GetHashCode() =>
            _kind == 1 ? _timeSpan.GetHashCode() : _kind.GetHashCode();

        public static bool operator ==(Duration t1, Duration t2) => t1.Equals(t2);
        public static bool operator !=(Duration t1, Duration t2) => !t1.Equals(t2);

        // Ordering: Automatic < anything else; Forever > anything else; finite by
        // TimeSpan. Mirrors Silverlight's documented behaviour.
        public static bool operator <(Duration t1, Duration t2)  => Compare(t1, t2) < 0;
        public static bool operator >(Duration t1, Duration t2)  => Compare(t1, t2) > 0;
        public static bool operator <=(Duration t1, Duration t2) => Compare(t1, t2) <= 0;
        public static bool operator >=(Duration t1, Duration t2) => Compare(t1, t2) >= 0;

        public static int Compare(Duration t1, Duration t2)
        {
            // Automatic == Automatic, otherwise smallest.
            if (t1._kind == 2 && t2._kind == 2) return 0;
            if (t1._kind == 2) return -1;
            if (t2._kind == 2) return 1;
            if (t1._kind == 3 && t2._kind == 3) return 0;
            if (t1._kind == 3) return 1;
            if (t2._kind == 3) return -1;
            return TimeSpan.Compare(t1._timeSpan, t2._timeSpan);
        }

        public static Duration Plus(Duration duration) => duration;

        public static Duration operator +(Duration t1, Duration t2) => t1.Add(t2);
        public static Duration operator -(Duration t1, Duration t2) => t1.Subtract(t2);
        public static Duration operator +(Duration duration) => duration;

        public override string ToString()
        {
            return _kind switch
            {
                1 => _timeSpan.ToString(),
                3 => "Forever",
                _ => "Automatic",
            };
        }
    }
}
