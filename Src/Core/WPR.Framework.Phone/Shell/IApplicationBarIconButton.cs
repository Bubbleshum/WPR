using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>Shim for <c>Microsoft.Phone.Shell.IApplicationBarIconButton</c>.</summary>
    public interface IApplicationBarIconButton : IApplicationBarMenuItem
    {
        Uri IconUri { get; set; }
    }
}
