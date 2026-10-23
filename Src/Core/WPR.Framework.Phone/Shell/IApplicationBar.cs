using System;
using System.Collections;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Shell
{
    /// <summary>Shim for <c>Microsoft.Phone.Shell.IApplicationBar</c>.</summary>
    public interface IApplicationBar
    {
        bool IsVisible { get; set; }
        double Opacity { get; set; }
        bool IsMenuEnabled { get; set; }
        Color BackgroundColor { get; set; }
        Color ForegroundColor { get; set; }
        ApplicationBarMode Mode { get; set; }
        double DefaultSize { get; }
        double MiniSize { get; }
        IList Buttons { get; }
        IList MenuItems { get; }
        event EventHandler<ApplicationBarStateChangedEventArgs> StateChanged;
    }
}
