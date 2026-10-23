using System;

namespace Microsoft.Phone.Shell
{
    /// <summary>Shim for <c>Microsoft.Phone.Shell.IApplicationBarMenuItem</c>.</summary>
    public interface IApplicationBarMenuItem
    {
        bool IsEnabled { get; set; }
        string Text { get; set; }
        event EventHandler Click;
    }
}
