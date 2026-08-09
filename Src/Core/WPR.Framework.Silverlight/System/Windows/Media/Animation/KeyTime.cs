using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Animation.KeyTime</c>. Tri-state value: a
    /// concrete <see cref="TimeSpan"/>, <see cref="Uniform"/>, or <see cref="Paced"/>.
    /// </summary>
    public struct KeyTime : IEquatable<KeyTime>
    {
        // 0 = uninitialized (treated as Uniform), 1 = TimeSpan, 2 = Uniform, 3 = Paced, 4 = Percent.
        private readonly int _kind;
        private readonly TimeSpan _timeSpan;
        private readonly double _percent;

        private KeyTime(int kind, TimeSpan ts, double pct)
        {
            _kind = kind;
            _timeSpan = ts;
            _percent = pct;
        }

        public static KeyTime FromTimeSpan(TimeSpan ts) => new KeyTime(1, ts, 0);
        public static KeyTime FromPercent(double percent) => new KeyTime(4, default, percent);
        public static KeyTime Uniform => new KeyTime(2, default, 0);
        public static KeyTime Paced => new KeyTime(3, default, 0);

        public TimeSpan TimeSpan => _kind == 1 ? _timeSpan : default;
        public double Percent => _kind == 4 ? _percent : 0;
        public KeyTimeType Type => _kind switch
        {
            1 => KeyTimeType.TimeSpan,
            2 => KeyTimeType.Uniform,
            3 => KeyTimeType.Paced,
            4 => KeyTimeType.Percent,
            _ => KeyTimeType.Uniform,
        };

        public static implicit operator KeyTime(TimeSpan ts) => FromTimeSpan(ts);

        public bool Equals(KeyTime other) =>
            _kind == other._kind && _timeSpan == other._timeSpan && _percent == other._percent;
        public override bool Equals(object? obj) => obj is KeyTime k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(_kind, _timeSpan, _percent);
        public static bool operator ==(KeyTime a, KeyTime b) => a.Equals(b);
        public static bool operator !=(KeyTime a, KeyTime b) => !a.Equals(b);
    }
}
