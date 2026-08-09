using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Phone.Shell
{
    public class ActivatedEventArgs : EventArgs
    {
        // Settable (internal) so PhoneApplicationService can build it via object-initializer
        // when it needs to force the preserved flag; the bool ctor is kept for callers that
        // construct it positionally.
        public bool IsApplicationInstancePreserved { get; internal set; }

        public ActivatedEventArgs() { }

        public ActivatedEventArgs(bool isApplicationInstancePreserved)
        {
            IsApplicationInstancePreserved = isApplicationInstancePreserved;
        }
    }
}
