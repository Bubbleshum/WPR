using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Animation.RepeatBehavior</c>. Either a finite
    /// iteration count (<c>new RepeatBehavior(3)</c>), a duration, or
    /// <see cref="Forever"/>.
    /// </summary>
    public struct RepeatBehavior : IEquatable<RepeatBehavior>
    {
        // 0 = count (default 1), 1 = duration, 2 = forever.
        private readonly int _kind;
        private readonly double _count;
        private readonly TimeSpan _duration;

        public RepeatBehavior(double count)
        {
            _kind = 0;
            _count = count;
            _duration = default;
        }

        public RepeatBehavior(TimeSpan duration)
        {
            _kind = 1;
            _count = 0;
            _duration = duration;
        }

        private RepeatBehavior(int kind)
        {
            _kind = kind;
            _count = 0;
            _duration = default;
        }

        public static RepeatBehavior Forever => new RepeatBehavior(2);

        public bool HasCount => _kind == 0;
        public bool HasDuration => _kind == 1;
        public double Count => _kind == 0 ? _count : 0;
        public TimeSpan Duration => _kind == 1 ? _duration : default;

        public bool Equals(RepeatBehavior other) =>
            _kind == other._kind && _count == other._count && _duration == other._duration;
        public override bool Equals(object? obj) => obj is RepeatBehavior r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(_kind, _count, _duration);
        public static bool operator ==(RepeatBehavior a, RepeatBehavior b) => a.Equals(b);
        public static bool operator !=(RepeatBehavior a, RepeatBehavior b) => !a.Equals(b);
    }
}
