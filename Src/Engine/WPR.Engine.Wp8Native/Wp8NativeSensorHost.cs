using System.Numerics;
using WPR.Engine.Sensors;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The platform's motion sensors, offered to a WP8 native title's WinRT
    /// <c>Gyrometer</c>/<c>Accelerometer</c> (<see cref="WinRtRuntime.Sensors"/>).
    /// </summary>
    /// <remarks>
    /// Read from <see cref="SensorBackend"/> when the game first asks, so the answer is the
    /// platform's: the gyroscope on a phone that has one, none on a PC; the hardware accelerometer
    /// on Android, the keyboard tilt emulator on the desktop.
    /// </remarks>
    internal sealed class Wp8NativeSensorHost : ISensorHost
    {
        public IHostSensor? Gyroscope =>
            SensorBackend.Gyroscope is { IsSupported: true } gyroscope
                ? new Sensor(gyroscope.Start, gyroscope.Stop, () => gyroscope.CurrentRotationRate)
                : null;

        public IHostSensor? Accelerometer =>
            SensorBackend.Accelerometer is { IsSupported: true } accelerometer
                ? new Sensor(accelerometer.Start, accelerometer.Stop, () => accelerometer.CurrentAcceleration)
                : null;

        private sealed class Sensor(Action start, Action stop, Func<Vector3> read) : IHostSensor
        {
            public void Start() => start();

            public void Stop() => stop();

            public (double X, double Y, double Z) Read()
            {
                Vector3 value = read();
                return (value.X, value.Y, value.Z);
            }
        }
    }
}
