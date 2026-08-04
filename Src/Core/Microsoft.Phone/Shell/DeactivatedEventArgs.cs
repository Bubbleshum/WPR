using System;

namespace Microsoft.Phone.Shell
{
    public class DeactivatedEventArgs : EventArgs
    {
        // Settable (internal) so PhoneApplicationService can build it via object-initializer;
        // the ctor overloads are kept for positional construction.
        public DeactivationReason Reason { get; internal set; } = DeactivationReason.UserAction;

        public DeactivatedEventArgs() { }

        public DeactivatedEventArgs(DeactivationReason reason) => Reason = reason;
    }
}
