using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Devices.Sensors
{
    public class AccelerometerReadingEventArgs : EventArgs
    {
        private double x;
        private double y;
        private double z;
        private DateTimeOffset timestamp;

        public AccelerometerReadingEventArgs(double x, double y, double z, DateTimeOffset? timestamp = null)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.timestamp = timestamp ?? DateTimeOffset.Now;
        }

        public double X => this.x;
        public double Y => this.y;
        public double Z => this.z;
        public DateTimeOffset Timestamp => this.timestamp;
    }
}
