using System;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using WPR.Engine.Sensors;

namespace Windows.Devices.Sensors
{
    /// <summary>
    /// Shim for <c>Windows.Devices.Sensors.Accelerometer</c>, the WinRT accelerometer a managed WP8
    /// title reads (Marmalade's s3eAccelerometer does). It reads the same
    /// <see cref="IAccelerometerProvider"/> as WP7's <c>Microsoft.Devices.Sensors.Accelerometer</c>:
    /// the phone's real sensor on Android, the keyboard tilt emulator on Windows. Both APIs report
    /// g in the phone's own frame (flat, face up, is (0, 0, -1)), so readings pass through unchanged.
    /// </summary>
    /// <remarks>
    /// <para><see cref="GetDefault"/> answers null when the platform has no accelerometer, as WinRT
    /// did on a device without one; WP8 titles were written to handle that.</para>
    /// <para>The provider counts its readers, so each instance starts it once, when the first
    /// <c>ReadingChanged</c> or <c>Shaken</c> handler is added, and stops it once, when the last is
    /// removed. Readings are raised on the provider's thread, as WinRT raised them on a sensor
    /// thread rather than the UI thread.</para>
    /// <para>Shaken is never raised: no provider detects a shake.</para>
    /// </remarks>
    public sealed class Accelerometer
    {
        /// <summary>The fastest interval WP8 phones reported, in milliseconds.</summary>
        private const uint MinimumIntervalMs = 16;

        private readonly IAccelerometerProvider _provider;
        private readonly object _gate = new();
        private EventRegistrationTokenTable<TypedEventHandler<Accelerometer, AccelerometerReadingChangedEventArgs>>? _readingChanged;
        private EventRegistrationTokenTable<TypedEventHandler<Accelerometer, AccelerometerShakenEventArgs>>? _shaken;
        private readonly global::System.Collections.Generic.HashSet<EventRegistrationToken> _tokens = new();
        private uint _reportInterval;

        private Accelerometer(IAccelerometerProvider provider)
        {
            _provider = provider;
        }

        /// <summary>The platform's accelerometer, or null when it has none.</summary>
        public static Accelerometer? GetDefault() =>
            SensorBackend.Accelerometer is { IsSupported: true } provider ? new Accelerometer(provider) : null;

        public uint MinimumReportInterval => MinimumIntervalMs;

        /// <summary>Stored only: the provider delivers at its own rate. 0 means "the default", as in WinRT.</summary>
        public uint ReportInterval => _reportInterval;

        /// <summary>WinRT names the setter put_ReportInterval, which is what game IL calls.</summary>
        public void put_ReportInterval(uint value) =>
            _reportInterval = value == 0 ? 0 : Math.Max(value, MinimumIntervalMs);

        public AccelerometerReading GetCurrentReading() => new(_provider.CurrentAcceleration);

        public EventRegistrationToken add_ReadingChanged(TypedEventHandler<Accelerometer, AccelerometerReadingChangedEventArgs> handler)
        {
            lock (_gate)
            {
                EventRegistrationToken token = EventRegistrationTokenTable<TypedEventHandler<Accelerometer, AccelerometerReadingChangedEventArgs>>
                    .GetOrCreateEventRegistrationTokenTable(ref _readingChanged).AddEventHandler(handler);
                AddedHandler(token);
                return token;
            }
        }

        public void remove_ReadingChanged(EventRegistrationToken token)
        {
            lock (_gate)
            {
                _readingChanged?.RemoveEventHandler(token);
                RemovedHandler(token);
            }
        }

        public EventRegistrationToken add_Shaken(TypedEventHandler<Accelerometer, AccelerometerShakenEventArgs> handler)
        {
            lock (_gate)
            {
                EventRegistrationToken token = EventRegistrationTokenTable<TypedEventHandler<Accelerometer, AccelerometerShakenEventArgs>>
                    .GetOrCreateEventRegistrationTokenTable(ref _shaken).AddEventHandler(handler);
                AddedHandler(token);
                return token;
            }
        }

        public void remove_Shaken(EventRegistrationToken token)
        {
            lock (_gate)
            {
                _shaken?.RemoveEventHandler(token);
                RemovedHandler(token);
            }
        }

        private void AddedHandler(EventRegistrationToken token)
        {
            // A null handler gets the default token and adds nothing.
            if (token.Equals(default(EventRegistrationToken)) || !_tokens.Add(token))
            {
                return;
            }

            if (_tokens.Count == 1)
            {
                _provider.ReadingChanged += OnProviderReading;
                _provider.Start();
            }
        }

        private void RemovedHandler(EventRegistrationToken token)
        {
            if (_tokens.Remove(token) && _tokens.Count == 0)
            {
                _provider.ReadingChanged -= OnProviderReading;
                _provider.Stop();
            }
        }

        private void OnProviderReading(Vector3 acceleration) =>
            _readingChanged?.InvocationList?.Invoke(this, new AccelerometerReadingChangedEventArgs(new AccelerometerReading(acceleration)));
    }

    /// <summary>Shim for <c>Windows.Devices.Sensors.AccelerometerReading</c>: g on each axis of the phone's frame.</summary>
    public sealed class AccelerometerReading
    {
        internal AccelerometerReading(Vector3 acceleration)
        {
            AccelerationX = acceleration.X;
            AccelerationY = acceleration.Y;
            AccelerationZ = acceleration.Z;
            Timestamp = DateTimeOffset.Now;
        }

        public double AccelerationX { get; }

        public double AccelerationY { get; }

        public double AccelerationZ { get; }

        public DateTimeOffset Timestamp { get; }
    }

    /// <summary>Shim for <c>Windows.Devices.Sensors.AccelerometerReadingChangedEventArgs</c>.</summary>
    public sealed class AccelerometerReadingChangedEventArgs
    {
        internal AccelerometerReadingChangedEventArgs(AccelerometerReading reading) => Reading = reading;

        public AccelerometerReading Reading { get; }
    }

    /// <summary>Shim for <c>Windows.Devices.Sensors.AccelerometerShakenEventArgs</c>.</summary>
    public sealed class AccelerometerShakenEventArgs
    {
        internal AccelerometerShakenEventArgs() => Timestamp = DateTimeOffset.Now;

        public DateTimeOffset Timestamp { get; }
    }
}
