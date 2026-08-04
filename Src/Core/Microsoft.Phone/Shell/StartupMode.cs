using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Integer values match the real WP7 SDK — games compare against literal
    /// integers in IL (Asphalt 5: <c>if ((int)PhoneApplicationService.Current.StartupMode == 1)</c>
    /// to detect a fresh launch). Using the C# default ordering of <c>Launch=0, Activate=1</c>
    /// made those checks take the wrong branch and sit on a blank screen forever.
    /// </summary>
    public enum StartupMode
    {
        Launch = 1,
        Activate = 2,
    }
}
