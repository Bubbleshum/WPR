using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.Page</c>. Navigation host. In SL the chain is
    /// <c>Page → UserControl → Control</c> and <c>PhoneApplicationPage</c> derives from
    /// <see cref="Page"/>. The page-lifecycle hooks (<see cref="OnNavigatedTo"/>,
    /// <see cref="OnNavigatedFrom"/>, <see cref="OnNavigatingFrom"/>) are declared
    /// here — overriding subclasses (Minesweeper's MainPage) emit
    /// <c>call base.OnNavigatedTo</c> against <c>System.Windows.Controls.Page</c>, which
    /// the patcher rewrites to land here. Without these methods on this exact type,
    /// user IL <see cref="MissingMethodException"/>s on the base call.
    /// </summary>
    public class Page : UserControl
    {
        public string? Title { get; set; }
        public NavigationService? NavigationService { get; internal set; }

        protected internal virtual void OnNavigatedTo(NavigationEventArgs e) { }
        protected internal virtual void OnNavigatedFrom(NavigationEventArgs e) { }
        protected internal virtual void OnNavigatingFrom(NavigatingCancelEventArgs e) { }
    }
}
