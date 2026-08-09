using System;

namespace Microsoft.Phone.Controls
{
    public sealed class ObscuredEventArgs : EventArgs
    {
        public ObscuredEventArgs(bool isLocked)
        {
            IsLocked = isLocked;
        }

        public ObscuredEventArgs() { }

        /// <summary>
        /// True when the frame was obscured because the device locked; false when obscured for
        /// another reason (incoming notification, etc.).
        /// </summary>
        public bool IsLocked { get; }
    }
}
